#!/usr/bin/env python3
"""Verify the imported static landing matches the recorded source archive."""
from pathlib import Path
import hashlib
import json
import sys

root = Path(__file__).resolve().parents[1] / "website"
manifest_path = root / "source-manifest.json"
if not manifest_path.exists():
    sys.exit("Landing source not imported: run scripts/import-landing.py first")
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
entries = manifest.get("sha256", {})
if not entries:
    sys.exit("Landing source manifest has no entries")
failures = []
for name, expected in entries.items():
    path = root / name
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        failures.append(name)
if failures:
    sys.exit("Missing or modified original landing assets: " + ", ".join(failures))
print(f"Verified {len(entries)} original landing files against SHA-256 manifest")
