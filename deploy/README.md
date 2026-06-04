# BuildOwnRAG — Install

Self-hosted RAG knowledge platform, run entirely via Docker.

## Requirements

- [Docker Desktop](https://docs.docker.com/get-docker/) (Windows/macOS) or Docker Engine + Compose plugin (Linux)
- ~4 GB free RAM and a few GB of disk

## 1. Get the compose file

Download **`docker-compose.yml`** (attached to this release) into a new empty folder, e.g. `buildownrag/`.

## 2. Create a `.env` next to it

The app needs a few secrets before first boot. Create a file named `.env` in the same folder:

```ini
# Pull this release's images (or set to a specific version / "latest")
IMAGE_TAG=latest

# Ports on the host
APP_PORT=8080
FRONTEND_PORT=3000
SETUP_PORT=8081

# Database
POSTGRES_DB=buildownrag
POSTGRES_USER=postgres
POSTGRES_PASSWORD=__CHANGE_ME__

# Secrets — replace with the generated values below
JWT_SECRET=__CHANGE_ME__
ENCRYPTION_KEY=__CHANGE_ME__   # must be exactly 32 characters
ENCRYPTION_IV=__CHANGE_ME__    # must be exactly 16 characters
```

Generate the four secrets:

- **Linux / macOS**
  ```bash
  echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)"
  echo "JWT_SECRET=$(openssl rand -hex 48)"
  echo "ENCRYPTION_KEY=$(openssl rand -hex 16)"   # 32 chars
  echo "ENCRYPTION_IV=$(openssl rand -hex 8)"     # 16 chars
  ```
- **Windows (PowerShell)**
  ```powershell
  function Hex($n){ -join (1..$n | % { '{0:x2}' -f (Get-Random -Max 256) }) }
  "POSTGRES_PASSWORD=$(Hex 24)"; "JWT_SECRET=$(Hex 48)"; "ENCRYPTION_KEY=$(Hex 16)"; "ENCRYPTION_IV=$(Hex 8)"
  ```

Paste the printed values into `.env`. (API keys and the admin account are set later in the setup wizard, not here.)

## 3. First run — launches the setup wizard

```bash
docker compose --profile setup up -d
```

Open the wizard at <http://localhost:8081>, choose your AI provider, paste its API key, and create the admin account.

## 4. Restart to apply the config

```bash
docker compose up -d
```

## 5. Use it

Open <http://localhost:3000> and log in.

---

## Everyday commands

```bash
docker compose up -d                            # start
docker compose down                             # stop (keeps your data)
docker compose pull && docker compose up -d     # update to newer images
docker compose logs -f app                      # view API logs
docker compose down -v                          # wipe ALL data and start clean
```

## Notes

- Your documents, vectors and settings live in Docker named volumes
  (`*_postgres_data`, `*_qdrant_data`, `*_redis_data`) — they survive restarts and updates.
- PostgreSQL, Redis and Qdrant are internal-only (not exposed to the host).
- Change ports in `.env` (`FRONTEND_PORT`, `APP_PORT`, `SETUP_PORT`) if they clash.

| Service      | URL                   |
| ------------ | --------------------- |
| App (UI)     | http://localhost:3000 |
| API          | http://localhost:8080 |
| Setup wizard | http://localhost:8081 |
