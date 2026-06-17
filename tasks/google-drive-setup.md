# Google Drive Connector — Setup & Personal Testing Guide (Route A: Service Account)

This guide shows how to get **test credentials** and wire up the Google Drive connector for personal
testing — using a normal personal Google account, no Google Workspace and no payment required.

## How it works (1-minute version)
The connector signs in as a **service account** — a non-human "robot" Google identity — using a JSON
key file (OAuth 2.0 JWT-bearer flow, server-to-server, no browser consent). A service account starts
with access to **nothing**. You grant it access by **sharing a Drive folder with its email address**,
exactly like sharing with a colleague. That share *is* the access boundary: the connector can only
read what you've shared.

---

## Step 1 — Create a Google Cloud project
1. Go to <https://console.cloud.google.com/>.
2. Top bar → project dropdown → **New Project**. Name it anything (e.g. `rag-drive-test`). Create.
3. Make sure the new project is selected in the top bar.

## Step 2 — Enable the Drive API
1. Left menu → **APIs & Services → Library**.
2. Search **Google Drive API** → open it → **Enable**.

## Step 3 — Create a service account + download its JSON key
1. Left menu → **APIs & Services → Credentials** (or **IAM & Admin → Service Accounts**).
2. **Create credentials → Service account**. Give it a name (e.g. `rag-drive-reader`). Create &
   continue. You can skip the optional role/grant steps → **Done**.
3. Click the new service account → **Keys** tab → **Add key → Create new key → JSON → Create**.
4. A `.json` file downloads. **This is the credential** the connector needs. Keep it private — it's a
   secret (the app stores it AES-encrypted, but treat the file like a password).

## Step 4 — Copy the service account email
On the service account page, copy its email — it looks like:

```
rag-drive-reader@rag-drive-test.iam.gserviceaccount.com
```

## Step 5 — Share a Drive folder with the service account
1. In **your personal Google Drive**, create a test folder, e.g. `RAG Test Docs`.
2. Put a few files in it for coverage:
   - a **binary** file: a real `.pdf` and/or a `.docx` you uploaded;
   - a **Google-native** file: create a **Google Doc** (and optionally a Google Sheet) inside the folder.
3. Right-click the folder → **Share** → paste the service account email → role **Viewer** → Send.
   (No email actually gets delivered to a robot account; the share just grants access.)

## Step 6 — Get the folder ID
Open the folder in the browser. The URL looks like:

```
https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz0123456
                                        └──────────── this is the folder ID ────────────┘
```

Copy the part after `/folders/`.

---

## What gets indexed (export/download behavior)
| Drive file kind | How the connector reads it | Ends up parsed as |
|---|---|---|
| Uploaded PDF | download (alt=media) | `application/pdf` |
| Uploaded Word `.docx` | download | Word parser |
| Uploaded Excel `.xlsx` | download | Excel parser |
| `.csv` / `.txt` / `.md` | download | Csv / Txt / Markdown parser |
| **Google Doc** | export → `.docx` | Word parser |
| **Google Sheet** | export → `.xlsx` | Excel parser |
| **Google Slides** | export → `.pdf` | PDF parser |
| Google Forms / Drawings / other native | **skipped** (no supported export) | — |

Files larger than `maxFileSizeMB` (default 50) are skipped.

---

## Step 6.5 — Run the app with the NEW connector code + get a JWT
The Google Drive connector is new code, so a previously-built container won't contain it — you must
run the freshly-built API. Pick one:

**Option A — rebuild the Docker image (closest to production, least config fiddling):**
```powershell
docker compose up -d --build
```
On first start the API seeds a default tenant + admin user and **prints the admin email/password in
the startup banner** — read it with:
```powershell
docker compose logs app | Select-String -Pattern "admin","password" -Context 0,1
```

**Option B — infra in Docker, API local (faster iteration while we build T4/T5):**
```powershell
docker compose up -d postgres redis qdrant
dotnet run --project src/ManufacturingAI.API
```
(Uses your local build directly. Ensure the API's connection strings point at `localhost`.)

**Get a JWT** (using the seeded admin creds):
```powershell
$login = Invoke-RestMethod -Method Post -Uri "http://localhost:8080/api/v1/auth/login" `
  -ContentType "application/json" `
  -Body (@{ email = "<admin-email>"; password = "<admin-password>" } | ConvertTo-Json)
$token = $login.data.accessToken   # ApiResponse wraps LoginResponse; access token lives here
```
> Alternatively: sign in at <http://localhost:3000>, open DevTools → Network → copy the
> `Authorization: Bearer ...` value from any `/api` request.

## Step 7 — Create the connector via the API
> The frontend / Setup Wizard UI for adding a Google Drive connector is a planned follow-up. For now
> create it through the existing connectors API. The token from Step 6.5 must belong to a user with
> the `CanManageConnectors` permission (the seeded admin has it).

The whole settings object is sent as a **JSON string** in `settingsJson` (the server encrypts it at
rest). Note the service-account key itself is JSON, so it must be embedded as a string — escape it, or
build the body programmatically.

**Settings shape:**
```json
{
  "serviceAccountJson": "<the entire contents of the downloaded key .json file>",
  "rootFolderId": "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456",
  "includeSubfolders": true,
  "maxFileSizeMB": 50
}
```

**Create (PowerShell example):**
```powershell
$token = "<your-JWT>"
$keyJson = Get-Content "C:\path\to\service-account-key.json" -Raw

$settings = @{
  serviceAccountJson = $keyJson
  rootFolderId       = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456"
  includeSubfolders  = $true
  maxFileSizeMB      = 50
} | ConvertTo-Json -Compress

$body = @{
  connectorType = "googledrive"
  displayName   = "My Drive Test"
  settingsJson  = $settings
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri "http://localhost:8080/api/v1/connectors" `
  -Headers @{ Authorization = "Bearer $token" } `
  -ContentType "application/json" -Body $body
```
The response contains the new connector's `id` — save it.

## Step 8 — Test the connection
```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://localhost:8080/api/v1/connectors/<connectorId>/test" `
  -Headers @{ Authorization = "Bearer $token" }
```
Expect `success: true` with a message like `N matching file(s) found`. (Available after task **T2**.)

## Step 9 — Trigger a sync (available after task T3)
```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:8080/api/v1/ingest/trigger" `
  -Headers @{ Authorization = "Bearer $token" } `
  -ContentType "application/json" `
  -Body (@{ connectorId = "<connectorId>" } | ConvertTo-Json)
```
Then watch the **Connectors** page (Settings → Connectors) and the **Documents** list — your Drive
files should move to `Indexed`. Re-triggering with no Drive changes should be a no-op (dedup).

---

## Troubleshooting
- **`/test` says 0 files** → the folder isn't actually shared with the service account email, or
  `rootFolderId` is wrong. Re-check Step 5 / Step 6.
- **403 / insufficient permissions** → the Drive API isn't enabled on the project (Step 2), or the key
  belongs to a different project.
- **A Google Doc didn't index** → confirm it's a Doc/Sheet/Slides (those export); Forms/Drawings are
  skipped by design.
- **Adjust the port** (`localhost:8080`) to match your local API.

## Security notes
- The service account key is a long-lived secret. The app stores `settingsJson` AES-encrypted at rest,
  but don't commit the key file or paste it into logs/issues.
- Scope is **read-only** (`drive.readonly`) — the connector can never modify or delete your Drive files.
- A purely personal account **cannot** use domain-wide delegation (impersonating users); the
  share-a-folder approach above is the supported personal-testing path.
