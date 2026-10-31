#!/bin/sh
# Pause application writes, then capture metadata, objects, and cookie keys together.
set -eu
cd "$(dirname "$0")/.."
umask 077
target=".local/backups/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$target"
docker compose stop web api
trap 'docker compose start api web >/dev/null' EXIT INT TERM
docker compose exec -T postgres pg_dump -U drive -d drive -Fc > "$target/postgres.dump"
docker run --rm -v google-drive-clone_object-data:/source:ro alpine:3.22 tar -C /source -czf - . > "$target/objects.tar.gz"
docker run --rm -v google-drive-clone_cookie-keys:/source:ro alpine:3.22 tar -C /source -czf - . > "$target/cookie-keys.tar.gz"
cp .env "$target/environment.env"
printf 'Backup saved in %s\n' "$target"
