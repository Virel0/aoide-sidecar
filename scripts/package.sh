#!/usr/bin/env bash
# Builds the plugin and produces both install shapes:
#
#   artifacts/Aoide Sidecar_<version>/        copy into <config>/plugins/ over ssh
#   artifacts/aoide-sidecar_<version>.zip     what the Jellyfin plugin repo serves
#
# The zip holds its files at the ROOT, not inside a folder. Jellyfin unpacks it with
# ZipFile.ExtractToDirectory straight into <plugins>/<name>_<version>, so a nested
# folder would bury the DLL one level too deep and the plugin would never load.
set -euo pipefail

VERSION="${1:-1.0.0.0}"
TARGET_ABI="10.11.0.0"
GUID="959763ae-fc57-4339-b8dc-a9c1800a2883"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Jellyfin.Plugin.AoideSidecar"
STAGE="$ROOT/artifacts/Aoide Sidecar_$VERSION"
ZIP="$ROOT/artifacts/aoide-sidecar_$VERSION.zip"

export PATH="$HOME/.dotnet:$PATH"

rm -rf "$ROOT/artifacts"
mkdir -p "$STAGE"

dotnet build "$PROJECT" -c Release \
  -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"

# Only our own assembly ships. Microsoft.Data.Sqlite and SQLitePCLRaw come from the
# server; a second copy would load without a registered native provider.
cp "$PROJECT/bin/Release/net9.0/Jellyfin.Plugin.AoideSidecar.dll" "$STAGE/"

# Jellyfin regenerates meta.json for repository installs, but a manual scp install
# needs it present, so it ships either way.
cat > "$STAGE/meta.json" <<EOF
{
    "category": "General",
    "changelog": "",
    "description": "Sync service for the Aoide curation store: playlists, folders, likes, play history and queue state.",
    "guid": "$GUID",
    "name": "Aoide Sidecar",
    "overview": "Relays the Aoide curation-store op log between a user's devices.",
    "owner": "aoide",
    "targetAbi": "$TARGET_ABI",
    "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
    "version": "$VERSION",
    "status": "Active",
    "autoUpdate": false
}
EOF

(cd "$STAGE" && zip -qr "$ZIP" .)

echo
echo "Folder: $STAGE"
echo "Zip:    $ZIP"
echo "MD5:    $("$ROOT/scripts/md5.sh" "$ZIP")"
