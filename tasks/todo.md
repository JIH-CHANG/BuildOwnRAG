# Todo — Google Drive Connector (Service Account)

Legend: `[ ]` todo · `[~]` in progress · `[x]` done

---

## T1 — Prerequisites & testing guide (no code) `[x]`
Done → `tasks/google-drive-setup.md`. Documents how YOU personally get test credentials.

**Steps to document**
1. Google Cloud Console → create project (free).
2. APIs & Services → Library → enable **Google Drive API**.
3. IAM & Admin → Service Accounts → create one → Keys → Add Key → **JSON** → download.
4. Copy the service account email (`*@*.iam.gserviceaccount.com`).
5. In personal Drive, make a test folder, drop in a PDF/docx + 1 Google Doc, **Share** it with the
   service-account email (Viewer is enough).
6. Copy the folder ID from the URL (`/folders/<THIS>`).
7. How to create the connector via API once code exists:
   `POST /api/v1/connectors` with `connectorType:"googledrive"` and `settingsJson` =
   `{ "serviceAccountJson": "<paste key>", "rootFolderId": "<id>", "includeSubfolders": true, "maxFileSizeMB": 50 }`
   then `POST /api/v1/connectors/{id}/test`.

**Acceptance**: doc lets a fresh reader obtain a working key + shared folder without prior GCP knowledge.
**Verify**: follow it end-to-end yourself; confirm the service account email can see the folder.

---

## T2 — Project setup + auth + TestConnectionAsync `[x]`  (vertical: connect & test) — build green, awaiting CP-A live test
**Files**
- `src/ManufacturingAI.Connectors.GoogleDrive/ManufacturingAI.Connectors.GoogleDrive.csproj`
  — add `Google.Apis.Drive.v3` package; add ProjectReference to `ManufacturingAI.Infrastructure`
  (for `IEncryptionService`, `ISyncStateRepository`), matching Folder's csproj.
- Delete `Class1.cs`.
- Add `GoogleDriveConnectorSettings.cs` (ServiceAccountJson, RootFolderId, IncludeSubfolders, MaxFileSizeMB).
- Add `GoogleDriveConnector.cs` — `ConnectorType => "googledrive"`; `Deserialize()` mirrors Folder;
  build `DriveService` from `GoogleCredential.FromJson(...).CreateScoped(DriveReadonly)`;
  implement `TestConnectionAsync` (validate JSON parses, folder is reachable, return matching file count);
  `FetchDeltaAsync` returns `[]` for now.
- Add `DependencyInjection.cs` — `AddGoogleDriveConnector()` mirroring `AddFolderConnector`.
- `src/ManufacturingAI.API/Program.cs` — call `builder.Services.AddGoogleDriveConnector();` near line 69.

**Acceptance**: `dotnet build` green; controller resolves the googledrive impl; `/test` against the
shared folder returns `Success=true` with a file count; bad/empty JSON returns a clear error.
**Verify**: `dotnet build`; create connector via API (T1); `POST /connectors/{id}/test`.
**→ CHECKPOINT CP-A: stop for review.**

---

## T3 — FetchDeltaAsync: full crawl + download/export `[x]`  (vertical: indexes files) — build green, awaiting CP-B live test
**File**: `GoogleDriveConnector.cs`
- `Files.List` crawl scoped to `RootFolderId` (recurse subfolders if enabled), apply `MaxFileSizeMB`.
- Build `SourceDocument` per file:
  - SourceId = file ID; Title = name; LastModified = modifiedTime; VersionHash = `version`/`md5Checksum`.
  - Binary → `Files.Get(id).DownloadAsync` (alt=media).
  - Google-native → `Files.Export(id, target)`; map Docs→docx, Sheets→xlsx, Slides→pdf; skip unsupported.
  - MimeType = effective mime (export target or native binary mime), aligned with `IParserFactory`.
- Stream into a buffered `MemoryStream` (Drive streams aren't seekable; `IngestService` rewinds).
- Reuse the `SyncState` version-hash dedup that `IngestService` already performs (no extra logic here).

**Acceptance**: a manual sync indexes every matching file in the shared folder; at least one binary
**and** one Google Doc land as `Indexed`; oversized files skipped; re-running with no changes is a no-op
(dedup hit).
**Verify**: trigger `POST /api/v1/ingest/sync?connectorId=...` (or Sync in UI); check Documents list +
logs; re-trigger and confirm "unchanged → skipped".
**→ CHECKPOINT CP-B: stop for review.**

---

## T4 — Delta via changes.list + page-token persistence `[ ]`
**File**: `GoogleDriveConnector.cs`
- Reserved sentinel `SyncState` row (e.g. `SourceId = "__drive_page_token__"`) holds the token in
  `VersionHash`; read/write via `ISyncStateRepository.GetByConnectorAsync` / `UpsertAsync`.
- No token → `Changes.GetStartPageToken`, persist, then run the T3 full crawl (seed).
- Token present → `Changes.List(token)` paged; collect added/modified file IDs in scope; map each to a
  `SourceDocument` (reusing T3 download/export); advance stored token to `NewStartPageToken`.
- Ignore the `since` argument (Drive cursor is authoritative).

**Acceptance**: first sync seeds + stores a token; editing one Drive file and re-syncing fetches only
that file; an untouched re-sync fetches zero.
**Verify**: sync once; edit one Doc in Drive; sync again; logs show 1 changed; token row advanced.

---

## T5 — Relax SyncSchedulerJob `[ ]`  (vertical: scheduled sync)
**File**: `src/ManufacturingAI.Services.Ingest/SyncSchedulerJob.cs:20-32`
- Replace the `ConnectorType == "folder"` filter so it enqueues **every enabled connector whose type
  has a registered `IKnowledgeConnector`** (the `IEnumerable<IKnowledgeConnector>` is already injected).
- Keep logging the per-type counts.

**Acceptance**: scheduled run enqueues both folder and googledrive connectors; no regression for folder.
**Verify**: run `RunAllTenantsAsync` (or wait for schedule); confirm a googledrive job is enqueued and
processed; folder still works.
**→ CHECKPOINT CP-C: final review.**

---

## Follow-ups (not in this plan — confirm before starting)
- [ ] Deletion/trash propagation (needs `IKnowledgeConnector` delete signal).
- [x] Frontend UI to add a connector (Connectors page → "Add Connector" modal, Google Drive + Folder,
      creates then auto-runs the connection test). Setup Wizard integration still optional.
- [ ] Unit tests for `GoogleDriveConnector` (held back per "don't touch unit tests unprompted").
- [ ] Update `README.md:103` connector status (GoogleDrive: scaffold → functional).
