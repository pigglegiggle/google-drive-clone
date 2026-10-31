# Architecture and invariants

## Ownership

React owns view state and file selection. A Web Worker incrementally hashes the original file without loading it entirely into browser memory. The upload client then sends missing 8 MiB chunks with up to three in-flight requests and exponential retry for network/429/5xx errors. Cancellation aborts active requests. The server is the source of truth for confirmed parts; browser progress counts confirmed bytes, not speculative socket progress.

ASP.NET Core owns user authentication, access checks, quota reservations, folder mutations, multipart coordination and publication. PostgreSQL owns durable metadata. MinIO owns file bytes. There is no local-filesystem storage fallback in the API.

`Database.cs` provides parameterized Npgsql commands and transaction-scoped sessions. `Files.cs` handles directory and permission operations. `Uploads.cs` implements the transfer state machine. `Storage.cs` is the only S3 adapter. `Cleanup.cs` processes the deletion outbox.

## Upload lifecycle

1. Validate the filename, size and whole-file hash. Determine the destination owner and verify access.
2. Lock the owner's user row. Sum retained version sizes and active reservations. Reject if the request would exceed quota.
3. Initiate S3 multipart upload and persist an upload session with a random object key. Empty files skip multipart initiation.
4. Each part request takes a transaction-scoped advisory lock keyed by upload ID and part number, then a shared row lock on the session. Different parts may upload concurrently; the same part number is serialized. The bounded request spools to disk while hashing. Only a matching part is sent to S3 and recorded with its ETag.
5. Finalization takes the owner lock and exclusive upload-row lock. This waits for in-flight parts, rechecks access and expiry, and requires every expected part and byte.
6. Complete S3 multipart assembly. If the object already exists, a previous attempt may have succeeded at S3 before the database commit; skip reassembly.
7. Read the assembled object from S3 and verify its complete size and SHA-256. Publish entry/version metadata and completed-session state together, or record a failed checksum and enqueue deletion.

The `assembling` state is written inside the finalization transaction. It is not an independently committed progress marker. A process crash rolls that transaction back to active, allowing a retry. The UI's “Verifying file” label is based on the completion request, not a fabricated server progress percentage.

S3 and PostgreSQL do not share a transaction. The design provides retryable publication, not a two-phase commit across the services. Repeated completion of a committed upload returns its same entry ID. A completed upload whose file was permanently deleted returns 410.

## Failures and recovery

| Failure point | Durable result | Next action |
|---|---|---|
| Part body is truncated or hash is wrong | No confirmed part | Retry that part |
| S3 part succeeds, SQL commit fails | S3 may contain an unrecorded part | Retry the same part number; it replaces those bytes |
| Response is lost after the part commit | Part is recorded | Status/resume skips it |
| S3 assembly succeeds, SQL publication fails | Object exists; session remains active | Retry completion and reverify |
| Whole-file verification fails | Session failed, no version published | Cancel and start a new upload |
| Access is revoked during transfer | Previously received parts may remain | Finalization rechecks and rejects publication |
| Parent is moved to Trash during transfer | Session remains reserved | Restore the parent or cancel the upload |
| API restarts | Metadata and confirmed parts remain in PostgreSQL | Resume with the same file; persistent keys preserve cookies |
| S3 deletion fails | Deletion outbox remains | Worker retries |

An initiation can succeed just before a crash without its upload ID ever being committed to PostgreSQL. Compose configures MinIO's stale multipart cleanup for this boundary. Session expiry is 24 hours; storage cleanup uses 48 hours so it does not intentionally race a still-valid session. Cleanup timing is eventual, not an exact deletion deadline.

## File tree and names

The directory is an adjacency list. Parent validation is owner-scoped and requires a live folder. A recursive `live_entries` view excludes a trashed item and any descendants under a trashed ancestor. A recursive ancestor check prevents moving a folder into its own subtree. Moves check both ancestor depth and subtree height against the 64-level limit.

A partial unique index enforces case-insensitive sibling names only for untrashed entries. Restoring into a conflicting name returns 409. Folder moves/renames are metadata operations and never rewrite S3 object keys. Search uses a parameterized substring match over the owner's live filenames. It is not a full-text content index.

Lists request 101 rows for 100 visible rows plus a next-page indicator. Offset pagination is intended for this showcase's scale; concurrent inserts may shift page boundaries. Versions are returned as a complete list for the selected file.

## Versions and quota

Each version points to a unique immutable object key. Restoring a version updates its `selected_at` ordering, leaving its original creation date, content and checksum unchanged. It does not create another stored copy. “Version history” is retained content versions, not an immutable audit trail of every restore action.

Quota includes all retained version bytes, even under Trash, and active upload reservations. Owner-row locks serialize reservations and file metadata mutations. Physical deletion is asynchronous; quota is released when metadata is permanently removed, which can temporarily leave more physical bytes on disk than the logical quota reports.

An owner lock remains held during finalization and full-object verification. This favors correctness and straightforward recovery over high write throughput within one account. There are no benchmark claims for concurrent large-file finalization.

## Sharing and authentication

Only file grants are supported: viewer or editor, attached to an existing registered account. An editor can upload/restore content but cannot rename, move, delete, grant access or issue public links. Permission checks occur at initiation and finalization. Folder grants and ACL inheritance are outside this implementation.

Public links use 256-bit random tokens. Only SHA-256 token hashes are stored. A link resolves the current version and must be live, unrevoked and unexpired. Download routes issue five-minute S3 GET URLs with attachment disposition and octet-stream content type. Issued URLs remain valid until their signed expiry even after subsequent revocation.

Passwords use the framework PBKDF2 password hasher with 210,000 iterations. Cookies are encrypted/authenticated using persisted ASP.NET Data Protection keys. They are HttpOnly and SameSite=Strict; deployment enables Secure cookies. Mutations require `X-Drive-Request: 1`. The API has no permissive CORS policy, so another browser origin cannot use that custom header to send an authenticated mutation. Nginx adds a same-origin CSP, nosniff, no-referrer and framing protection.

The showcase shares one S3 service credential between API operations. It is never sent to the browser. There is no per-user S3 credential or bucket policy. Compose does not expose PostgreSQL/API ports by default, and exposes S3 and the web app to loopback until domain routing is configured.

## Lifecycle and deployment boundaries

The worker scans at most 20 expired sessions and 30 queued deletions per pass. Pending work persists across process restarts. Duplicate worker deletion attempts are safe because S3 deletion and abort-of-missing-upload are treated idempotently. Old completed upload session metadata is retained for completion idempotency; this repository does not implement a metadata retention policy or individual-version deletion UI.

SQL bootstrapping is idempotent for the initial schema and uses an advisory lock. Future schema changes need an explicit migration; `CREATE TABLE IF NOT EXISTS` is not a migration framework. Single-instance Docker Compose is the documented deployment. Shared keys and database locks support API replicas in principle, but no replica/load-balancer deployment was validated.
