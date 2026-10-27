#!/usr/bin/env bash
# Usage: scripts/package.sh
# Builds a self-contained portable folder (no .NET install needed on the target) and zips it to dist/ in the repo.
set -euo pipefail
source "$(dirname "$0")/common.sh"
VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$REPO/src/Stagecast.App/Stagecast.App.csproj")
OUT='C:\dev\stagecast-dist'
NAME="Stagecast-$VERSION-win-x64"
sync_sources
cp "$REPO/THIRD_PARTY_NOTICES.md" "$REPO/README.md" "$REPO/scripts/BACA-SAYA.txt" "$WSL_DIR/"
rm -rf "/mnt/c/dev/stagecast-dist"
winps "dotnet publish src\\Stagecast.App\\Stagecast.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=embedded -nologo -v q -o $OUT\\$NAME"
PUB="/mnt/c/dev/stagecast-dist/$NAME"
cp "$WSL_DIR/THIRD_PARTY_NOTICES.md" "$WSL_DIR/BACA-SAYA.txt" "$PUB/"
mkdir -p "$PUB/licenses" && cp "$REPO/third_party/omt/LICENSE.txt" "$PUB/licenses/OMT-LICENSE.txt"
winps "Compress-Archive -Path '$OUT\\$NAME' -DestinationPath '$OUT\\$NAME.zip' -Force"
mkdir -p "$REPO/dist" && cp "/mnt/c/dev/stagecast-dist/$NAME.zip" "$REPO/dist/"
echo "package: $REPO/dist/$NAME.zip ($(du -h "$REPO/dist/$NAME.zip" | cut -f1)); folder $OUT\\$NAME"
