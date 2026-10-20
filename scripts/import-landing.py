#!/usr/bin/env python3
"""Import the original SVAULT landing ZIP into website/ without altering binary assets.

Usage:
    python3 scripts/import-landing.py "/path/to/svault-website-docker (1).zip"
    python3 scripts/import-landing.py "/path/to/svault-landing-repo-package.zip"

Run from the repository root on feature/maui-shared-app.
"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "website"
PREFIXES = ("svault-website/", "website/")
REQUIRED = {
    "index.html", "privacy.html", "terms.html", "Dockerfile",
    "nginx.conf", "assets/styles.css", "assets/app.js",
    "robots.txt", "sitemap.xml", "llms.txt", "llms-full.txt",
}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    args = parser.parse_args()
    if not args.archive.is_file():
        parser.error(f"Archive not found: {args.archive}")
    manifest = {}
    with zipfile.ZipFile(args.archive) as archive:
        candidates = {}
        for info in archive.infolist():
            if info.is_dir():
                continue
            name = info.filename.replace("\\", "/")
            prefix = next((p for p in PREFIXES if name.startswith(p)), None)
            if prefix is None:
                continue
            relative = name[len(prefix):]
            path = PurePosixPath(relative)
            if (not relative or path.is_absolute() or
                    any(part in ("", ".", "..") for part in path.parts)):
                raise ValueError(f"Unsafe archive member: {info.filename}")
            if relative in candidates:
                raise ValueError(f"Duplicate website path: {relative}")
            candidates[relative] = info
        missing = REQUIRED - set(candidates)
        if missing:
            raise ValueError(f"Missing required landing files: {sorted(missing)}")
        for relative, info in sorted(candidates.items()):
            # Reject Unix symlinks; only regular files may be imported.
            if ((info.external_attr >> 16) & 0o170000) == 0o120000:
                raise ValueError(f"Symlink not allowed: {relative}")
            data = archive.read(info)
            output = TARGET.joinpath(*PurePosixPath(relative).parts)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(data)
            manifest[relative] = hashlib.sha256(data).hexdigest()
    (TARGET / "source-manifest.json").write_text(
        json.dumps({"source": args.archive.name, "sha256": manifest}, indent=2) + "\n",
        encoding="utf-8")
    print(f"Imported {len(manifest)} original files into {TARGET}")
    print("Review changes, verify website, and commit to feature/maui-shared-app.")
    print("Never import into main directly.")

if __name__ == "__main__":
    main()
