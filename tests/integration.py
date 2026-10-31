#!/usr/bin/env python3
"""Exercise real HTTP, PostgreSQL, and S3 storage; no mocked persistence."""

import concurrent.futures
import hashlib
import http.cookiejar
import json
import os
import time
import urllib.error
import urllib.request
import uuid

BASE = os.getenv("BASE_URL", "http://127.0.0.1:5080")
from http_client import Client, expect, check_count

owner, stranger, editor, public = Client(), Client(), Client(), Client()
run = uuid.uuid4().hex[:12]
password = "Integration-check-password-2026"
for client, label in [(owner, "owner"), (stranger, "stranger"), (editor, "editor")]:
    client.email = f"{label}-{run}@example.test"
    client.request("POST", "/api/auth/register", {"email": client.email, "password": password})
public.request("GET", "/api/entries", status=401)
public.request(
    "POST", "/api/auth/login", {"email": owner.email, "password": "wrong-password"}, status=401
)
owner.request("GET", "/api/auth/me")
owner.request(
    "POST", "/api/auth/register", {"email": owner.email, "password": password}, status=409
)
owner.request("POST", "/api/folders", {"name": "../bad"}, status=400)
folder = owner.request("POST", "/api/folders", {"name": f"Checks {run}"})["id"]
child = owner.request("POST", "/api/folders", {"name": "Nested", "parentId": folder})["id"]
owner.request("PATCH", f"/api/entries/{folder}", {"name": "Loop", "parentId": child}, status=400)
stranger.request("GET", f"/api/entries/{folder}", status=404)


def start(client, data, name, entry=None, parent=folder, digest=None):
    return client.request(
        "POST",
        "/api/uploads",
        {
            "name": name,
            "size": len(data),
            "sha256": digest or hashlib.sha256(data).hexdigest(),
            "contentType": "application/octet-stream",
            "parentId": parent,
            "entryId": entry,
        },
    )["id"]


def part(client, upload, number, data, status=200, digest=None):
    return client.request(
        "PUT",
        f"/api/uploads/{upload}/parts/{number}",
        data,
        status=status,
        headers={"X-Part-SHA256": digest or hashlib.sha256(data).hexdigest()},
    )


def complete(client, upload, status=200):
    return client.request("POST", f"/api/uploads/{upload}/complete", status=status)


# Multipart data contains binary bytes and crosses two exact part boundaries.
part_size = 8 * 1024 * 1024
data = (bytes(range(256)) * (part_size // 256)) * 2 + b"final-tail\0\xff"
upload = start(owner, data, "binary.dat")
complete(owner, upload, status=409)
part(owner, upload, 1, data[:part_size], digest="0" * 64, status=400)
part(owner, upload, 1, data[:part_size])
status = owner.request("GET", f"/api/uploads/{upload}")
expect(len(status["parts"]) == 1, "Only the confirmed part should be persisted")
# A fresh client represents closing the browser and signing in again.
resumed = Client()
resumed.request("POST", "/api/auth/login", {"email": owner.email, "password": password})
expect(
    resumed.request("GET", f"/api/uploads/{upload}")["parts"][0]["number"] == 1,
    "Upload survives a new session",
)
expect(part(resumed, upload, 1, data[:part_size])["reused"], "Duplicate part is idempotent")
part(owner, upload, 1, data[:part_size], digest="1" * 64, status=409)
stranger.request("GET", f"/api/uploads/{upload}", status=404)
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    futures = [
        pool.submit(
            part, resumed, upload, number, data[(number - 1) * part_size : number * part_size]
        )
        for number in [2, 3]
    ]
    for future in futures:
        future.result()
entry = complete(owner, upload)["id"]
expect(complete(owner, upload)["id"] == entry, "Repeated finalization returns the same file")
expect(
    owner.request("GET", f"/api/entries/{entry}/download", raw=True) == data,
    "Downloaded bytes match the original",
)
expect(
    len(owner.request("GET", f"/api/entries/{entry}/versions")) == 1,
    "Finalization does not duplicate versions",
)
stranger.request("GET", f"/api/entries/{entry}/download", status=404)

# Permissions and grants are checked again when publishing a new version.
owner.request(
    "PUT", f"/api/entries/{entry}/grants", {"email": editor.email, "role": "viewer"}, status=204
)
expect(
    len(editor.request("GET", "/api/entries?view=shared")) == 1,
    "Shared file is visible to the grantee",
)
editor.request(
    "PATCH", f"/api/entries/{entry}", {"name": "Unauthorized.dat", "parentId": folder}, status=403
)
editor.request(
    "POST",
    "/api/uploads",
    {"name": "new", "size": 3, "sha256": hashlib.sha256(b"new").hexdigest(), "entryId": entry},
    status=403,
)
owner.request(
    "PUT", f"/api/entries/{entry}/grants", {"email": editor.email, "role": "editor"}, status=204
)
v2data = b"new version content"
v2 = start(editor, v2data, "binary.dat", entry=entry)
part(editor, v2, 1, v2data)
owner.request(
    "PUT", f"/api/entries/{entry}/grants", {"email": editor.email, "role": "remove"}, status=204
)
complete(editor, v2, status=404)
owner.request(
    "PUT", f"/api/entries/{entry}/grants", {"email": editor.email, "role": "editor"}, status=204
)
complete(editor, v2)
versions = owner.request("GET", f"/api/entries/{entry}/versions")
expect(len(versions) == 2, "Two immutable objects back two versions")
expect(
    owner.request("GET", f"/api/entries/{entry}/download", raw=True) == v2data,
    "Latest version downloads",
)
owner.request("POST", f"/api/entries/{entry}/versions/{versions[1]['id']}/restore", status=204)
expect(
    owner.request("GET", f"/api/entries/{entry}/download", raw=True) == data,
    "Restore selects original bytes",
)

# Link revocation and hiding a parent invalidate future share requests.
link = owner.request("POST", f"/api/entries/{entry}/shares", {"hours": 1})
public.request("GET", f"/api/public/{link['token']}")
expect(
    public.request("GET", f"/api/public/{link['token']}/download", raw=True) == data,
    "Public link downloads",
)
share_id = owner.request("GET", f"/api/entries/{entry}/shares")["links"][0]["id"]
owner.request("DELETE", f"/api/entries/{entry}/shares/{share_id}", status=204)
public.request("GET", f"/api/public/{link['token']}", status=404)
link = owner.request("POST", f"/api/entries/{entry}/shares", {"hours": 1})
owner.request("POST", f"/api/entries/{folder}/trash", status=204)
owner.request("GET", f"/api/entries/{entry}/download", status=404)
public.request("GET", f"/api/public/{link['token']}", status=404)
owner.request("POST", f"/api/entries/{folder}/restore", status=204)
public.request("GET", f"/api/public/{link['token']}")

# A bad whole-file checksum never publishes metadata, even if each part was valid.
bad = start(owner, b"bad-digest", "bad.dat", digest="0" * 64)
part(owner, bad, 1, b"bad-digest")
complete(owner, bad, status=422)
expect(
    owner.request("GET", f"/api/uploads/{bad}")["state"] == "failed",
    "Checksum mismatch is recorded",
)
owner.request("DELETE", f"/api/uploads/{bad}", status=204)
empty = start(owner, b"", "empty.txt")
empty_id = complete(owner, empty)["id"]
expect(
    owner.request("GET", f"/api/entries/{empty_id}/download", raw=True) == b"", "Empty files work"
)
# Name conflict after S3 completion is recoverable without uploading the bytes twice.
conflict = start(owner, b"conflict", "empty.txt")
part(owner, conflict, 1, b"conflict")
complete(owner, conflict, status=409)
owner.request(
    "PATCH", f"/api/entries/{empty_id}", {"name": "renamed.txt", "parentId": folder}, status=204
)
conflict_entry = complete(owner, conflict)["id"]
expect(
    owner.request("GET", f"/api/entries/{conflict_entry}/download", raw=True) == b"conflict",
    "Completion recovers after S3 succeeded but DB rejected a duplicate name",
)
quota = start(owner, b"x", "cancel.txt")
owner.request("DELETE", f"/api/uploads/{quota}", status=204)
complete(owner, quota, status=409)
# Missing custom header is rejected even when the session cookie exists.
request = urllib.request.Request(
    BASE + "/api/folders", data=b'{"name":"csrf"}', headers={"Content-Type": "application/json"}
)
try:
    owner.opener.open(request)
    raise AssertionError("Mutation without verification header succeeded")
except urllib.error.HTTPError as error:
    expect(error.code == 403, "CSRF header enforcement")
# Delete a subtree only after moving it to Trash.
owner.request("DELETE", f"/api/entries/{folder}", status=400)
owner.request("POST", f"/api/entries/{folder}/trash", status=204)
owner.request("DELETE", f"/api/entries/{folder}", status=204)
expect(
    not owner.request("GET", "/api/entries?view=trash"),
    "Permanent delete removes the folder subtree",
)
owner.request("POST", "/api/auth/logout", status=204)
owner.request("GET", "/api/auth/me", status=401)
print(f"{check_count()} integration checks passed against real PostgreSQL and S3 storage.")
