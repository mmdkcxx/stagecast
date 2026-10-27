#!/usr/bin/env bash
# Usage: scripts/installer.sh
# Builds the portable package (scripts/package.sh), then wraps it in a Windows installer with Inno Setup:
#   dist/Stagecast-Setup-<version>.exe  (Start Menu entry, optional Desktop icon, prerequisite check, WebView2 download, uninstaller)
# The Inno Setup compiler is fetched from NuGet (Tools.InnoSetup) into C:\dev\tools when missing; nothing is installed system-wide.
set -euo pipefail
source "$(dirname "$0")/common.sh"
INNO_VERSION=6.7.3
TOOLS='/mnt/c/dev/tools'
ISCC="$TOOLS/innosetup/tools/ISCC.exe"
VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$REPO/src/Stagecast.App/Stagecast.App.csproj")

if [ ! -f "$ISCC" ]; then
  mkdir -p "$TOOLS/dl"
  curl -sSL -o "$TOOLS/dl/innosetup.zip" "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$INNO_VERSION"
  powershell.exe -NoProfile -Command "Expand-Archive -Force 'C:\\dev\\tools\\dl\\innosetup.zip' 'C:\\dev\\tools\\innosetup'"
fi

"$REPO/scripts/package.sh"
rm -f "$WSL_DIR/dist/Stagecast-Setup-"*.exe
winps "& 'C:\\dev\\tools\\innosetup\\tools\\ISCC.exe' /Qp /DAppVersion=$VERSION '/DSourceDir=C:\\dev\\stagecast-dist\\Stagecast-$VERSION-win-x64' installer\\stagecast.iss"
mkdir -p "$REPO/dist" && cp "$WSL_DIR/dist/Stagecast-Setup-$VERSION.exe" "$REPO/dist/"
echo "installer: $REPO/dist/Stagecast-Setup-$VERSION.exe ($(du -h "$REPO/dist/Stagecast-Setup-$VERSION.exe" | cut -f1))"
