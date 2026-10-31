#!/usr/bin/env python3
"""Create local Compose credentials without printing or overwriting them."""

from pathlib import Path
import os
import secrets

root = Path(__file__).resolve().parents[1]
path = root / ".env"
values = (root / ".env.example").read_text()
values = values.replace("replace-with-a-random-password", secrets.token_hex(24))
values = values.replace(
    "replace-with-a-random-secret-of-at-least-16-characters", secrets.token_hex(32)
)
try:
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
except FileExistsError:
    raise SystemExit(".env already exists; leaving it unchanged.")
with os.fdopen(descriptor, "w") as file:
    file.write(values)
print("Created .env. Start with: docker compose up -d --build")
