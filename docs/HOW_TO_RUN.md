# How to run

## Docker Compose

1. Install Docker Desktop on macOS/Windows, or Docker Engine with the Compose v2 plugin on Linux. On Windows enable Linux containers/WSL2.
2. Run `python3 scripts/configure.py` (`py -3` on Windows). This creates private `.env` credentials without overwriting existing settings.
3. Run `docker compose up -d --build --wait` from the repository root. The first build compiles upstream MinIO; allow several GB of temporary build space.
4. Open http://localhost:8090 and create an account. Upload a file, select it, and inspect its version history.
5. Run `docker compose down` to stop services. Named volumes preserve data.

The application origin and browser-visible object-store origin are separate. By default the API uses `http://minio:9000` inside Compose and signs downloads for `http://localhost:9000` outside Compose. If you open the app from another computer, `localhost` points to that computer: change `S3_PUBLIC_ENDPOINT` to a reachable URL. See DEPLOYMENT.md for the domain-based setup.

## Native development

Requirements: .NET SDK 10.0.403 or a compatible .NET 10 SDK, Node.js 22.12+, npm, PostgreSQL 14+, and a running S3-compatible service. Docker Compose can run just the stateful services:

```sh
python3 scripts/configure.py
docker compose up -d postgres minio
```

The Compose database is intentionally not published to the host. For native API development either use your own loopback PostgreSQL instance or add a local-only override mapping `127.0.0.1:55432:5432`. Use the private password from `.env` in your native connection settings.

Set these variables in the shell that starts the API:

```sh
export DATABASE_URL='Host=127.0.0.1;Port=55432;Database=drive;Username=drive;Password=YOUR_LOCAL_PASSWORD'
export S3_ENDPOINT='http://127.0.0.1:9000'
export S3_PUBLIC_ENDPOINT='http://127.0.0.1:9000'
export S3_ACCESS_KEY='YOUR_LOCAL_ACCESS_KEY'
export S3_SECRET_KEY='YOUR_LOCAL_SECRET_KEY'
export KEY_DIRECTORY="$PWD/.local/keys"
export TEMP_DIRECTORY="$PWD/.local/parts"
export ASPNETCORE_URLS='http://127.0.0.1:5080'
dotnet run --project backend --no-launch-profile
```

In another terminal:

```sh
npm ci --prefix frontend
npm run dev --prefix frontend
```

Open http://127.0.0.1:5173. Vite proxies `/api` to 5080. The key directory must remain the same across API restarts for cookies to stay valid. Service credentials are required at startup; nothing falls back to a built-in MinIO password.

`dotnet build backend/Drive.csproj` compiles the API. `npm run build --prefix frontend` type-checks and bundles the UI. `dotnet format backend/Drive.csproj` and `npm run format --prefix frontend` maintain formatting. No IDE-specific project files are required.

## Tests

```sh
BASE_URL=http://127.0.0.1:5080 python3 tests/integration.py
cd frontend
npx playwright install chromium
UI_URL=http://127.0.0.1:5173 npm run test:e2e
cd ..
# With the same database/S3/key-directory environment as the native API:
python3 tests/recovery.py
```

Native recovery testing starts a second API at port 5081, kills it after one confirmed part, restarts it, and verifies the resumed download. It does not stop the development API at 5080. The Compose equivalent is `python3 tests/recovery.py --compose`, which restarts the `api` service.

Browser tests can use an already installed Chrome by setting `CHROME_PATH` to its executable. On macOS: `/Applications/Google Chrome.app/Contents/MacOS/Google Chrome`. Tests create unique accounts; use a disposable test database/storage for repeated runs. They remove their main file trees, but intentionally leave account rows and some browser-test data.

## Troubleshooting

| Symptom | Check |
|---|---|
| Docker command is missing | Install Docker Engine/Desktop and start its daemon. The repository does not install or start a VM for you. |
| MinIO build runs out of space | Free Docker build space before retrying; its Go dependency build is larger than the application. |
| Services are waiting for health | Run `docker compose logs postgres minio api`. API startup initializes SQL and checks the bucket before listening. |
| Download points at an unreachable host | Set `S3_PUBLIC_ENDPOINT` to the address the browser can reach; preserve the exact host and scheme used for signing. |
| Upload remains paused | Choose Resume upload and select the original file. A different SHA-256 is rejected. |
| Storage refuses writes | Check free storage on the underlying volume. MinIO can reserve free capacity even before the application quota is reached. |
| Account quota is full | Trash and old versions still count. Permanently delete unneeded files; physical object cleanup follows asynchronously. |
| Permanent delete reports an active upload | Cancel uploads targeting the file or folder first. |
| New login is rate-limited | Wait one minute. Auth operations have a bounded fixed-window limiter. |
| Cookies stop working after restart | Check the persistent key directory/volume and application name. |

## Ports

| Service | Compose host port | Native development |
|---|---|---|
| Web | 8090 | Vite 5173 (local validation used 5174) |
| API | Internal 8080 | 5080 |
| PostgreSQL | Internal 5432 | 55432 in local validation |
| S3 API | 9000, loopback by default | 9000 |
| Object-store console | 9001, loopback only | 9001 |
