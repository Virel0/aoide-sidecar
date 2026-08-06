#!/usr/bin/env bash
# Prints the MD5 of a file as lowercase hex, on macOS or Linux.
# Jellyfin verifies plugin downloads against this exact value (case-insensitively).
set -euo pipefail

if command -v md5 >/dev/null 2>&1; then
    md5 -q "$1"
else
    md5sum "$1" | cut -d' ' -f1
fi
