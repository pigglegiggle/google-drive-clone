# Self-hosting and backups

## One machine, Docker Compose

The base stack is a single-machine deployment with persistent volumes:

- `google-drive-clone_postgres-data`: users, directory tree, permissions, versions and resumable upload state.
- `google-drive-clone_object-data`: MinIO objects and multipart state.
- `google-drive-clone_cookie-keys`: encrypted-cookie signing/encryption keys.

The Compose project name is fixed in `compose.yaml`; the backup helper uses those volume names. If you change it, update the helper too. Run `python3 scripts/configure.py` once, keep `.env` private and back it up. Rebuilding/replacing API or web containers does not replace their persistent data.

PostgreSQL and the API are internal services. The web app, S3 endpoint and MinIO console bind to loopback by default. Docker Desktop provides the Linux-container environment on macOS/Windows; no Windows-only .NET Framework dependency is involved.

## Domain-based HTTPS

Point two DNS names to your VPS, for example `drive.your-domain.tld` and `objects.your-domain.tld`. The S3 host needs its own hostname because its signed path/host must be preserved. Do not put S3 behind a rewritten `/storage` path.

Add these values to the private `.env`:

```dotenv
APP_DOMAIN=drive.your-domain.tld
S3_DOMAIN=objects.your-domain.tld
S3_PUBLIC_ENDPOINT=https://objects.your-domain.tld
SECURE_COOKIES=true
BIND_ADDRESS=127.0.0.1
```

Allow inbound ports 80/443 and run:

```sh
docker compose -f compose.yaml -f compose.https.yaml up -d --build --wait
```

Caddy terminates HTTPS and routes the app to Nginx and object downloads to MinIO. Direct Compose ports 8090/9000 stay loopback-only. Do not expose port 9001 publicly. Secure cookies are enabled explicitly because TLS terminates before the HTTP API container.

The browser-visible S3 URL must match what the API signs. If your site loads over HTTPS but signs an HTTP S3 URL, the browser may reject or downgrade the download. There is no direct browser upload to S3 in this version, so browser S3 CORS is not required.

This showcase uses an archived pinned MinIO source version, local registration and one service credential. The app is designed for demonstrations and personal evaluation; this repository does not claim an externally audited multi-tenant production deployment. Storage administration and identity-provider integration are separate future changes, not hidden requirements for running the demo.

## Backup

From a running base Compose stack:

```sh
./scripts/backup.sh
```

The helper pauses web/API writes, captures a PostgreSQL custom-format dump, archives the object volume and cookie-key volume, copies `.env`, and starts the API/web again. It leaves PostgreSQL and MinIO running. Files are written beneath `.local/backups/TIMESTAMP/`, ignored by Git, with a restrictive shell umask. Run the script with Bash/WSL2 on Windows.

A PostgreSQL dump alone is insufficient: each retained version points to object bytes. Copy the entire backup directory to independent storage. It contains service credentials and authentication keys; do not publish it. Consistency assumes this application is the only writer to its private bucket; do not mutate objects through the MinIO console while backing up.

## Restore to a fresh deployment

Use a separate clean machine/project with **empty target volumes**. The following commands intentionally do not wipe existing volumes. If target volumes already hold data, stop and choose an isolated deployment before extracting archives.

```sh
# Copy the backup directory to ./backup first.
cp backup/environment.env .env
docker compose build
docker compose up -d postgres
docker compose create minio api

# Wait until PostgreSQL is healthy, then restore its metadata.
docker compose exec -T postgres pg_restore -U drive -d drive --clean --if-exists < backup/postgres.dump

# Restore objects and keys, preserving archive ownership.
docker run --rm -i -v google-drive-clone_object-data:/restore alpine:3.22 tar -C /restore -xzf - < backup/objects.tar.gz
docker run --rm -i -v google-drive-clone_cookie-keys:/restore alpine:3.22 tar -C /restore -xzf - < backup/cookie-keys.tar.gz

docker compose up -d --wait
```

After restoring, sign in, list a folder, download a known file and compare its SHA-256. Restart the API and verify an existing session/unfinished upload. For a domain-based deployment also include `compose.https.yaml` and update the copied domain settings. Backup/restore commands are documented but were not executed in the native validation environment without Docker.

## Updating application containers

Pull reviewed source, back up, then rebuild with `docker compose up -d --build --wait`. The initial schema initializer is not a migration system. Review future SQL changes and provide a migration before upgrading an existing database to a changed schema. Avoid deleting volumes as a workaround for an update failure.

The MinIO source commit is pinned in `infra/minio/Dockerfile`. Updating it is a deliberate dependency change requiring S3 regression tests. The app can target another compatible endpoint through `S3_ENDPOINT` and `S3_PUBLIC_ENDPOINT`, but compatibility must be tested; an S3-compatible label is not a promise that every optional S3 feature exists.
