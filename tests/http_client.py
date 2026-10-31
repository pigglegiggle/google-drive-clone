"""Small standard-library HTTP client shared by storage integration checks."""

import http.cookiejar
import json
import os
import urllib.error
import urllib.request

checks = 0


def expect(condition, message):
    global checks
    assert condition, message
    checks += 1


class Client:
    def __init__(self, base=None):
        self.base = base or os.getenv("BASE_URL", "http://127.0.0.1:5080")
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))

    def request(self, method, path, body=None, status=200, headers=None, raw=False):
        data = (
            body
            if isinstance(body, bytes)
            else json.dumps(body).encode() if body is not None else None
        )
        request = urllib.request.Request(
            self.base + path,
            data=data,
            method=method,
            headers={
                "X-Drive-Request": "1",
                "Content-Type": (
                    "application/octet-stream" if isinstance(body, bytes) else "application/json"
                ),
                **(headers or {}),
            },
        )
        try:
            response = self.opener.open(request, timeout=120)
        except urllib.error.HTTPError as error:
            response = error
        content = response.read()
        expect(
            response.status == status,
            f"{method} {path}: expected {status}, got {response.status}: {content[:400]!r}",
        )
        return content if raw else json.loads(content) if content else None


def check_count():
    return checks
