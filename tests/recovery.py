#!/usr/bin/env python3
"""Restart the real API between multipart parts; verify both cookie and upload survive."""

import argparse
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--compose", action="store_true")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
process = None
base = "http://localhost:8090" if args.compose else "http://127.0.0.1:5081"
env = os.environ.copy()
env["ASPNETCORE_URLS"] = base
dotnet = shutil.which("dotnet") or str(root / ".tools/dotnet/dotnet")
log = None


def start():
    global process, log
    log = (root / ".local/logs/recovery.log").open("a")
    process = subprocess.Popen(
        [dotnet, str(root / "backend/bin/Debug/net10.0/Drive.dll")],
        env=env,
        cwd=root,
        stdout=log,
        stderr=log,
    )
    wait()


def wait():
    for _ in range(100):
        try:
            urllib.request.urlopen(base + "/api/health", timeout=1).close()
            return
        except Exception:
            time.sleep(0.2)
    raise AssertionError("API did not become healthy")


jar = http.cookiejar.CookieJar()
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))


def request(method, path, body=None, digest=None, raw=False):
    binary = isinstance(body, bytes)
    data = body if binary else json.dumps(body).encode() if body is not None else None
    headers = {
        "X-Drive-Request": "1",
        "Content-Type": "application/octet-stream" if binary else "application/json",
    }
    if digest:
        headers["X-Part-SHA256"] = digest
    with client.open(
        urllib.request.Request(base + path, data=data, method=method, headers=headers), timeout=120
    ) as response:
        content = response.read()
        return content if raw else json.loads(content) if content else None


try:
    if not args.compose:
        (root / ".local/logs").mkdir(parents=True, exist_ok=True)
        start()
    request(
        "POST",
        "/api/auth/register",
        {
            "email": f"recovery-{uuid.uuid4().hex}@example.test",
            "password": "Recovery-test-password-2026",
        },
    )
    data = b"r" * (8 * 1024 * 1024) + b"after-restart"
    digest = hashlib.sha256(data).hexdigest()
    upload = request(
        "POST", "/api/uploads", {"name": "recovery.bin", "size": len(data), "sha256": digest}
    )["id"]
    first = data[: 8 * 1024 * 1024]
    request("PUT", f"/api/uploads/{upload}/parts/1", first, hashlib.sha256(first).hexdigest())
    if args.compose:
        subprocess.run(
            ["docker", "compose", "restart", "api"], cwd=root, check=True, stdout=subprocess.DEVNULL
        )
        wait()
    else:
        process.kill()
        process.wait()
        log.close()
        start()
    request("GET", "/api/auth/me")  # Cookie is decryptable after restart.
    status = request("GET", f"/api/uploads/{upload}")
    assert len(status["parts"]) == 1 and status["parts"][0]["number"] == 1
    tail = data[len(first) :]
    request("PUT", f"/api/uploads/{upload}/parts/2", tail, hashlib.sha256(tail).hexdigest())
    entry = request("POST", f"/api/uploads/{upload}/complete")["id"]
    downloaded = request("GET", f"/api/entries/{entry}/download", raw=True)
    assert hashlib.sha256(downloaded).hexdigest() == digest
    request("POST", f"/api/entries/{entry}/trash")
    request("DELETE", f"/api/entries/{entry}")
    print(
        "Restart recovery passed: persistent cookie, confirmed part, resumed upload, and byte-identical download."
    )
finally:
    if process is not None and process.poll() is None:
        process.terminate()
        process.wait(timeout=10)
    if log is not None:
        log.close()
