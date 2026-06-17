# Plan — Google Drive Connector (Route A: Service Account / 2-legged OAuth 2.0 JWT)

## Goal
Replace the scaffold in `src/ManufacturingAI.Connectors.GoogleDrive` with a functional
`IKnowledgeConnector` that authenticates with a **service account JSON key** (no interactive
browser consent), crawls a shared Drive folder, and feeds documents into the existing ingest
pipeline — wired into both manual and scheduled sync.

## Why Service Account (recap)
Sync runs headless via Hangfire (`SyncSchedulerJob.RunConnectorAsync`), so there is no user at a
browser to complete a 3-legged consent. A service account uses the OAuth 2.0 JWT-bearer flow
server-to-server: the JSON key is stored (already AES-encrypted) in `ConnectorConfig.SettingsJson`,
and the connector exchanges it for access tokens on every run. The user shares the target Drive
folder with the service account's email — that sharing *is* the access boundary.

## Architecture fit (grounding in existing code)
- `IKnowledgeConnector` (`src/ManufacturingAI.Core/Interfaces/IKnowledgeConnector.cs`) — implement
  `ConnectorType`, `TestConnectionAsync`, `FetchDeltaAsync`. Returns `SourceDocument` records.
- `FolderConnector` (`src/ManufacturingAI.Connectors.Folder/FolderConnector.cs`) is the reference
  implementation: decrypts settings via `IEncryptionService`, reads `ISyncStateRepository`, returns
  changed `SourceDocument`s. We mirror its shape.
- `IngestService.IngestDocumentAsync` already dedups on `SourceDocument.VersionHash` vs the stored
  `SyncState.VersionHash`, parses by `MimeType` via `IParserFactory`, chunks, embeds, indexes. The
  connector only needs to produce correct `SourceId` / `VersionHash` / `MimeType` / `Content`.
- `SyncState` (`src/ManufacturingAI.Core/Models/SyncState.cs`) is keyed per `SourceId`. We reuse one
  reserved sentinel row to persist the Drive **changes page token** (connector-level cursor).
- DI mirrors `AddFolderConnector` (`src/ManufacturingAI.Connectors.Folder/DependencyInjection.cs`),
  registered in `Program.cs:69`.
- `SyncSchedulerJob` (`SyncSchedulerJob.cs:21-22`) currently hardcodes `ConnectorType == "folder"`;
  this must be relaxed or googledrive will never be scheduled.

## Key design decisions
1. **`ConnectorType` = `"googledrive"`** (lowercase, matches the `folder` convention).
2. **Settings shape** (`GoogleDriveConnectorSettings`, serialized → encrypted into `SettingsJson`):
   - `ServiceAccountJson` (string) — raw service-account key file contents.
   - `RootFolderId` (string) — the shared folder to scope crawling to.
   - `IncludeSubfolders` (bool, default `true`).
   - `MaxFileSizeMB` (int, default `50`).
3. **Auth**: `GoogleCredential.FromJson(json).CreateScoped(DriveService.Scope.DriveReadonly)` →
   `DriveService`. Read-only scope is sufficient and least-privilege.
4. **SourceId** = Drive **file ID** (stable, globally unique). `Title` = file name.
5. **VersionHash** = Drive file `version` (or `md5Checksum` when present for binary files). This lets
   `IngestService` dedup unchanged files without re-downloading content.
6. **Download vs export**:
   - Binary files (uploaded PDF/docx/xlsx/…): `Files.Get(id)` + `DownloadAsync` (alt=media).
   - Google-native (`application/vnd.google-apps.*`): `Files.Export(id, targetMime)` —
     Docs→`docx`, Sheets→`xlsx`, Slides→`pdf`. Targets chosen to match what `IParserFactory`
     already parses. Unsupported native types (Forms, Drawings) are skipped with a log line.
7. **Delta strategy**:
   - First run (no stored token): call `Changes.GetStartPageToken`, store it, then do a **full
     `Files.List` crawl** of `RootFolderId` and return every matching file.
   - Subsequent runs: `Changes.List(storedToken)`, page through, collect added/modified file IDs
     scoped to the folder, advance the stored token to `NewStartPageToken`.
   - The `since` parameter is ignored (Drive's own cursor is authoritative).
8. **Scope filtering**: a file is in-scope if `RootFolderId` is in its `parents`, or (when
   `IncludeSubfolders`) an ancestor chain reaches `RootFolderId`. MVP resolves ancestry with a
   small cached parent lookup; depth limit documented.

## Out of scope (explicit, follow-up tasks)
- **Deletions/trashing**: `FetchDeltaAsync` returns only `SourceDocument`s (no delete signal in the
  interface). Removing a trashed Drive file from the index needs an interface extension — deferred.
- **Frontend / Setup Wizard UI** to add a googledrive connector. For now the connector is created
  via the existing `POST /api/v1/connectors` API (documented in the testing task).
- **Domain-wide delegation / per-user impersonation** (needs Google Workspace admin).
- **Unit tests**: not added in this plan per the standing "don't touch unit tests unprompted" rule.
  Flagged as an opt-in task — say the word and I'll add them.

## Dependency graph
```
T1 (credentials + test doc)  ─┐
                              ├─> T2 (project setup + auth + TestConnection)  [vertical: connect+test]
                              │        │
                              │        v
                              │   T3 (FetchDelta full crawl + download/export) [vertical: indexes files]
                              │        │
                              │        v
                              │   T4 (delta via changes.list + page-token persistence)
                              │        │
                              │        v
                              └─> T5 (relax SyncSchedulerJob → scheduled sync picks up googledrive)
```

## Checkpoints
- **CP-A (after T2)**: build green; a real service-account key + shared folder yields
  `/test` → success with a file count. Stop for review.
- **CP-B (after T3)**: a manual sync indexes real Drive files (binary + at least one Google Doc).
  Stop for review.
- **CP-C (after T5)**: scheduled run enqueues the googledrive connector and re-sync is incremental.
  Final review.
