#!/usr/bin/env python3
"""Check concurrent quota reservation and worker expiry using real persisted state."""

import argparse
import concurrent.futures
import hashlib
from pathlib import Path
import subprocess
import time
import uuid
from http_client import Client, expect, check_count

parser = argparse.ArgumentParser()
parser.add_argument("--compose", action="store_true")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]


def sql(statement):
    command = (
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "drive", "-d", "drive"]
        if args.compose
        else ["psql", "-h", "127.0.0.1", "-p", "55432", "-U", "drive", "-d", "drive"]
    )
    return subprocess.check_output(
        command + ["-v", "ON_ERROR_STOP=1", "-At", "-c", statement], cwd=root, text=True
    ).strip()


client = Client()
account = client.request(
    "POST",
    "/api/auth/register",
    {
        "email": f"quota-{uuid.uuid4().hex}@example.test",
        "password": "Concurrent-quota-password-2026",
    },
)
user = str(uuid.UUID(account["id"]))
sql(f"UPDATE users SET quota=10 WHERE id='{user}'")
payload = {"name": "reserved.bin", "size": 6, "sha256": hashlib.sha256(b"123456").hexdigest()}
# Give each worker its own cookie jar to avoid sharing mutable HTTP client internals.
workers = [Client(), Client()]
for worker in workers:
    worker.request(
        "POST",
        "/api/auth/login",
        {"email": account["email"], "password": "Concurrent-quota-password-2026"},
    )


def reserve(worker):
    import urllib.error
    import urllib.request
    import json

    request = urllib.request.Request(
        worker.base + "/api/uploads",
        data=json.dumps(payload).encode(),
        headers={"X-Drive-Request": "1", "Content-Type": "application/json"},
    )
    try:
        response = worker.opener.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    return response.status, json.loads(response.read())


with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    results = list(pool.map(reserve, workers))
expect(
    sorted(code for code, _ in results) == [200, 409],
    "Exactly one 6-byte reservation may fit in a 10-byte account",
)
upload = next(body["id"] for code, body in results if code == 200)
expect(
    client.request("GET", "/api/stats")["reserved"] == 6,
    "Concurrent requests cannot over-reserve quota",
)
client.request(
    "PUT",
    f"/api/uploads/{upload}/parts/1",
    b"123456",
    headers={"X-Part-SHA256": hashlib.sha256(b"123456").hexdigest()},
)
upload = str(uuid.UUID(upload))
sql(f"UPDATE uploads SET expires_at=now()-interval '1 minute' WHERE id='{upload}'")
for _ in range(80):
    status = client.request("GET", f"/api/uploads/{upload}")
    if status["state"] == "expired":
        break
    time.sleep(0.5)
expect(status["state"] == "expired", "The worker expires the persisted upload")
expect(client.request("GET", "/api/stats")["reserved"] == 0, "Expiry releases quota reservation")
# A quota of ten bytes is also enforced across retained versions.
u = client.request("POST", "/api/uploads", payload)["id"]
client.request(
    "PUT",
    f"/api/uploads/{u}/parts/1",
    b"123456",
    headers={"X-Part-SHA256": hashlib.sha256(b"123456").hexdigest()},
)
entry = client.request("POST", f"/api/uploads/{u}/complete")["id"]
client.request("POST", "/api/uploads", {**payload, "entryId": entry}, status=409)
client.request("POST", f"/api/entries/{entry}/trash", status=204)
expect(client.request("GET", "/api/stats")["used"] == 6, "Trash still counts toward quota")
client.request("DELETE", f"/api/entries/{entry}", status=204)
expect(
    client.request("GET", "/api/stats")["used"] == 0,
    "Permanent metadata deletion releases logical quota",
)
print(
    f"{check_count()} quota/expiry checks passed; concurrent reservation and background cleanup verified."
)
