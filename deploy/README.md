# BuildOwnRAG — Install

Self-hosted RAG knowledge platform, run entirely via Docker. A browser-based
setup wizard does the configuration for you — no hand-editing of secrets.

## Requirements

- [Docker Desktop](https://docs.docker.com/get-docker/) (Windows/macOS) or Docker Engine + Compose plugin (Linux)
- ~4 GB free RAM and a few GB of disk

## 1. Get the compose file

Download **`docker-compose.yml`** (attached to this release) into a new empty folder, e.g. `buildownrag/`.

## 2. Create a minimal `.env`

In the same folder, create a file named `.env` with just a database password
(the only thing needed before first boot — the wizard generates everything else):

```ini
POSTGRES_PASSWORD=choose-a-strong-password-here
```

> Optional: add `IMAGE_TAG=1.2.3` to pin a version, or `APP_PORT` / `FRONTEND_PORT`
> / `SETUP_PORT` if those ports are taken.

## 3. Start with the setup wizard

```bash
docker compose --profile setup up -d
```

Open the wizard at **<http://localhost:8081>** and follow the three steps:

1. **Databases** — leave the hosts as `postgres` / `redis` / `qdrant`; for the
   Postgres password enter **the same value you put in `.env`**. Click *Test*.
2. **AI provider** — pick OpenAI / Azure / Gemini / Ollama / Claude and paste its
   API key (the wizard validates it).
3. **Admin account** — organisation name, admin email and password.

Click **Install**. The wizard runs migrations, creates your admin account,
initialises the vector store, and writes the full `.env` (JWT and encryption keys
are generated for you).

## 4. Restart to apply the generated config

```bash
docker compose up -d
```

## 5. Use it

Open <http://localhost:3000> and log in with the admin account you created.

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
- The `app` service is unhealthy until the wizard finishes and you restart in step 4 —
  that's expected; you only use port 8081 during setup.

| Service      | URL                   |
| ------------ | --------------------- |
| App (UI)     | http://localhost:3000 |
| API          | http://localhost:8080 |
| Setup wizard | http://localhost:8081 |
