#!/usr/bin/env python3
"""Adds a version to manifest.json, the file Jellyfin reads as a plugin repository.

The manifest is a list of packages, each holding its versions newest-first. Jellyfin
verifies the downloaded zip against the MD5 recorded here, so the checksum has to be
computed from the exact artifact that gets uploaded, not rebuilt afterwards.
"""

import datetime
import json
import os
import pathlib

GUID = "959763ae-fc57-4339-b8dc-a9c1800a2883"
TARGET_ABI = "10.11.0.0"

ROOT = pathlib.Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "manifest.json"

version = os.environ["VERSION"]
checksum = os.environ["CHECKSUM"]
source_url = os.environ["SOURCE_URL"]
changelog = os.environ.get("CHANGELOG", "")

package = {
    "guid": GUID,
    "name": "Aoide Sidecar",
    "description": (
        "Sync service for the Aoide curation store: playlists, folders, likes, "
        "play history and queue state."
    ),
    "overview": "Relays the Aoide curation-store op log between a user's devices.",
    "owner": "Virel0",
    "category": "General",
    "imageUrl": "",
    "versions": [],
}

if MANIFEST.exists():
    packages = json.loads(MANIFEST.read_text())
    for existing in packages:
        if existing.get("guid") == GUID:
            package = existing
            break
    else:
        packages.append(package)
else:
    packages = [package]

versions = [v for v in package.get("versions", []) if v.get("version") != version]
versions.insert(
    0,
    {
        "version": version,
        "changelog": changelog,
        "targetAbi": TARGET_ABI,
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        ),
    },
)
package["versions"] = versions

MANIFEST.write_text(json.dumps(packages, indent=4) + "\n")
print(f"manifest.json now offers {version} ({checksum})")
