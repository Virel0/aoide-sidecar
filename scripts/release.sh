#!/usr/bin/env bash
# Cuts a release: build, publish to GitHub Releases, and point manifest.json at it.
#
#   ./scripts/release.sh 1.0.1.0 "Fixed the thing"
#
# Jellyfin re-reads manifest.json on its own schedule, so the new version shows up in
# the dashboard shortly after this finishes. Nothing needs copying to the server.
set -euo pipefail

VERSION="${1:?usage: release.sh <version> [changelog]}"
CHANGELOG="${2:-}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Version must have four parts, e.g. 1.0.1.0 — Jellyfin parses it as System.Version." >&2
    exit 1
fi

if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
    echo "Working tree has uncommitted changes. Commit them first so the tag means something." >&2
    exit 1
fi

if git rev-parse "v$VERSION" >/dev/null 2>&1; then
    echo "Tag v$VERSION already exists. Pick a new version." >&2
    exit 1
fi

export PATH="$HOME/.dotnet:$PATH"

echo "==> Tests"
dotnet test --nologo --verbosity quiet

echo "==> Package"
./scripts/package.sh "$VERSION" >/dev/null
ZIP="artifacts/aoide-sidecar_$VERSION.zip"
CHECKSUM="$(./scripts/md5.sh "$ZIP")"

REPO="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"
SOURCE_URL="https://github.com/$REPO/releases/download/v$VERSION/aoide-sidecar_$VERSION.zip"

echo "==> GitHub release v$VERSION"
git tag "v$VERSION"
git push origin "v$VERSION"
gh release create "v$VERSION" "$ZIP" \
    --title "v$VERSION" \
    --notes "${CHANGELOG:-Release $VERSION}"

echo "==> manifest.json"
VERSION="$VERSION" CHECKSUM="$CHECKSUM" SOURCE_URL="$SOURCE_URL" CHANGELOG="$CHANGELOG" \
    python3 scripts/update_manifest.py

git add manifest.json
git commit -m "Publish $VERSION to the plugin manifest"
git push

echo
echo "Published $VERSION."
echo "Jellyfin will offer it under Dashboard -> Plugins -> Catalog."
