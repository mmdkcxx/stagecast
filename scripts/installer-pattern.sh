#!/usr/bin/env bash
# Usage: scripts/installer-pattern.sh
# Publishes Stagecast Pattern self-contained, then wraps it in a Windows installer with Inno Setup:
#   dist/StagecastPattern-Setup-<version>.exe  (Start Menu entry, optional Desktop icon, uninstaller)
# Uses the Inno Setup compiler that scripts/installer.sh puts in C:\dev\tools (fetched from NuGet when missing).
set -euo pipefail
source "$(dirname "$0")/common.sh"
INNO_VERSION=6.7.3
TOOLS='/mnt/c/dev/tools'
ISCC="$TOOLS/innosetup/tools/ISCC.exe"
VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$REPO/src/Stagecast.Pattern/Stagecast.Pattern.csproj")
OUT='C:\dev\stagecast-dist'
NAME="StagecastPattern-$VERSION-win-x64"

if [ ! -f "$ISCC" ]; then
  mkdir -p "$TOOLS/dl"
  curl -sSL -o "$TOOLS/dl/innosetup.zip" "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$INNO_VERSION"
  powershell.exe -NoProfile -Command "Expand-Archive -Force 'C:\\dev\\tools\\dl\\innosetup.zip' 'C:\\dev\\tools\\innosetup'"
fi

sync_sources
rm -rf "/mnt/c/dev/stagecast-dist/$NAME"
winps "dotnet publish src\\Stagecast.Pattern\\Stagecast.Pattern.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=embedded -nologo -v q -o $OUT\\$NAME"
rm -f "$WSL_DIR/dist/StagecastPattern-Setup-"*.exe
winps "& 'C:\\dev\\tools\\innosetup\\tools\\ISCC.exe' /Qp /DAppVersion=$VERSION '/DSourceDir=$OUT\\$NAME' installer\\stagecast-pattern.iss"
mkdir -p "$REPO/dist" && cp "$WSL_DIR/dist/StagecastPattern-Setup-$VERSION.exe" "$REPO/dist/"
echo "installer: $REPO/dist/StagecastPattern-Setup-$VERSION.exe ($(du -h "$REPO/dist/StagecastPattern-Setup-$VERSION.exe" | cut -f1))"
