# Validation evidence

Validated on **1 November 2026 (Asia/Bangkok)**. Results describe this checkout and the environment actually used, rather than claiming unexecuted Docker coverage.

## Environment

- macOS Apple Silicon, arm64.
- .NET SDK 10.0.403; ASP.NET Core / .NET 10.
- PostgreSQL 14 on a private loopback port, with a dedicated `drive` database.
- React 19.2, TypeScript 5.9, Vite 7.2; Node.js 22.21.1.
- Browser automation using Playwright and installed Google Chrome.
- Native S3 service: **Silo**, a MinIO-derived fork, `RELEASE.2026-10-26T00-00-00Z`, using a private local test volume.

The host had no Docker runtime. The upstream MinIO source build also exceeded available free disk space; only the newly downloaded dependency cache was removed afterward. A native S3-compatible service was used to test real object storage without replacing it with an in-memory mock. The committed Compose stack continues to build **upstream MinIO** at the pinned release commit; this local test is not presented as a Docker/upstream-MinIO execution.

## Executed checks

| Check | Result |
|---|---|
| `dotnet build backend/Drive.csproj` | Passed, zero warnings/errors |
| `npm run build --prefix frontend` | Passed TypeScript checking and production bundle |
| `python3 tests/integration.py` | 95 checks passed against real HTTP, PostgreSQL and S3 |
| `python3 tests/database_checks.py` | Concurrent quota reservation, persisted expiry worker, Trash accounting and deletion release passed |
| `python3 tests/recovery.py` | Passed actual API process kill/restart between multipart parts |
| Browser workflows | File/folder operations, versions, share revocation, original-file resume, narrow-screen layout and pause/move flow |
| Diagram / README review | SVGs and real workspace screenshot rendered and inspected |

The quota test's request count includes polling for the 30-second worker and varies with timing; it is not used as a fixed headline test count. The restart test verifies persistent cookies, confirmed-part recovery and byte-identical final download. Integration data crosses two 8 MiB part boundaries and includes binary bytes. It does not establish 5 GiB throughput, high-concurrency capacity, or long-running disk utilization.

## Docker checks

`compose.yaml` and the HTTPS override define the full self-hosted stack. GitHub Actions is configured to build it and run the same real-storage HTTP/browser tests plus a container restart test. Consult the actual [workflow runs](https://github.com/pigglegiggle/google-drive-clone/actions) for Docker results. This document does not treat the presence of that workflow as evidence of a successful run.

Backup/restore, public-domain HTTPS certificate issuance, native Windows execution and multi-replica operation were not executed locally. Windows instructions use Docker Desktop Linux containers or WSL2, not native Windows MinIO/.NET Framework services.
