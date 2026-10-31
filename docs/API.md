# API reference

All paths below are under `/api`. Requests and responses use JSON except multipart part bodies and redirect responses. Request record properties use camelCase. SQL-backed metadata rows currently use snake_case, as defined by the frontend types.

Use the session cookie from login/register. Every mutating request requires `X-Drive-Request: 1`. JSON bodies also require `Content-Type: application/json`. A missing header returns 403. Unauthenticated protected routes return 401. Missing/inaccessible resources return 404; known read-only resources reject mutations with 403. Name/quota/state conflicts return 409.

## Authentication

| Method | Path | Body / response |
|---|---|---|
| POST | `/auth/register` | `{email,password}` → `{id,email}` and cookie |
| POST | `/auth/login` | `{email,password}` → `{id,email}` and cookie |
| POST | `/auth/logout` | Clears cookie, 204 |
| GET | `/auth/me` | `{id,email}` |

Passwords are 12–128 characters on registration. Auth requests are rate-limited. Accounts register locally; no external identity service is required.

## Files and folders

| Method | Path | Body / behavior |
|---|---|---|
| GET | `/stats` | Account email, quota, used and reserved bytes |
| GET | `/entries?parent=UUID&search=term&view=files&offset=0` | At most 101 rows; show 100 and use the extra row as a next-page indicator |
| GET | `/entries?view=shared` | Explicit file grants to this account |
| GET | `/entries?view=trash` | Directly trashed entries owned by this account |
| POST | `/folders` | `{name,parentId:null}` → `{id}` |
| GET | `/entries/{id}` | Accessible entry metadata |
| PATCH | `/entries/{id}` | `{name,parentId:null}`; both fields describe the destination, 204 |
| POST | `/entries/{id}/trash` | Owner only, 204 |
| POST | `/entries/{id}/restore` | Restore from Trash, owner only, 204 |
| DELETE | `/entries/{id}` | Permanently delete a trashed subtree, owner only, 204 |
| GET | `/entries/{id}/versions` | Versions, selected/current first |
| POST | `/entries/{id}/versions/{version}/restore` | Select an existing version, owner/editor, 204 |
| GET | `/entries/{id}/download?version=UUID` | 302 to a five-minute S3 URL; omit version for current |

`search` searches all live filenames owned by the account and overrides parent filtering. Download endpoints force attachment/octet-stream rather than rendering arbitrary uploaded HTML. Public bearer URLs should not be placed in logs or analytics.

## Multipart uploads

Start with:

```json
{
  "name": "archive.zip",
  "size": 16777220,
  "sha256": "64-lowercase-hexadecimal-characters-from-the-original-file",
  "contentType": "application/zip",
  "parentId": null,
  "entryId": null
}
```

The hash above illustrates the field, not a valid hash value. Set `entryId` to upload a new version; the existing owner and access rules then apply. The filename does not rename that existing entry. New files use `parentId`.

| Method | Path | Response / behavior |
|---|---|---|
| POST | `/uploads` | `{id,partSize}`; reserves storage |
| GET | `/uploads` | Active/failed sessions for the current account |
| GET | `/uploads/{id}` | Session, `part_size`, and confirmed `parts` |
| PUT | `/uploads/{id}/parts/{number}` | Raw bytes with exact Content-Length and `X-Part-SHA256`; numbers start at 1 |
| POST | `/uploads/{id}/complete` | `{id}` of published entry; repeatable |
| DELETE | `/uploads/{id}` | Cancel incomplete/failed session and enqueue cleanup, 204 |

All non-final parts are exactly 8 MiB. The final part uses the remaining byte count. Empty files have no parts and can be completed directly. Parts can arrive out of order. A repeated confirmed number with a different claimed hash returns 409. Completion rejects missing parts with 409 and whole-file hash mismatch with 422. Deleting a completed upload session is rejected; move its resulting file to Trash instead.

## Sharing

| Method | Path | Body / behavior |
|---|---|---|
| GET | `/entries/{id}/shares` | Owner's link metadata and email grants; raw tokens are not returned |
| POST | `/entries/{id}/shares` | `{hours:24}` → `{token,expiresAt}`; 1–720 hours |
| DELETE | `/entries/{id}/shares/{share}` | Revoke link, 204 |
| PUT | `/entries/{id}/grants` | `{email,role:"viewer"}`; `editor` and `remove` also supported, 204 |
| GET | `/public/{token}` | Public name, size, checksum and link expiry |
| GET | `/public/{token}/download` | Public redirect to current version's five-minute signed URL |

Public routes need no cookie. A trashed ancestor also makes public resolution unavailable. Up to 20 non-expired links may exist per file.

## Health

`GET /health` returns `{status:"ready"}` after startup schema/bucket initialization. It is a liveness/startup check, not a continuous deep probe of PostgreSQL and S3. Runtime dependency failures surface through operation responses and container/service logs.
