#!/usr/bin/env bash
# Usage: scripts/run-spike.sh [build|run|all]   (default: all)
# Copies the buildable parts of the repo to a Windows folder, builds with the Windows
# .NET SDK, and runs the spike exe. Works from WSL; the repo itself stays on the Linux side.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WIN_DIR='C:\dev\stagecast'
WSL_DIR='/mnt/c/dev/stagecast'
PROJ='spikes\Stagecast.Spike'
TFM='net8.0-windows10.0.19041.0'
MODE="${1:-all}"

sync_sources() {
  mkdir -p "$WSL_DIR"
  rm -rf "$WSL_DIR/spikes" "$WSL_DIR/src" "$WSL_DIR/tests"
  (cd "$REPO" && tar --exclude=bin --exclude=obj --exclude=logs --exclude=.gitkeep \
     -cf - spikes src tests global.json Directory.Build.props .editorconfig Stagecast.sln) \
    | tar -xf - -C "$WSL_DIR"
}

build() {
  powershell.exe -NoProfile -Command "Set-Location '$WIN_DIR'; dotnet build Stagecast.sln -c Debug -nologo -v minimal"
}

run() {
  local logfile="$WSL_DIR/spikes/Stagecast.Spike/bin/Debug/$TFM/logs/s0.log"
  rm -f "$logfile"
  powershell.exe -NoProfile -Command "Set-Location '$WIN_DIR\\$PROJ'; Start-Process -Wait -FilePath '.\\bin\\Debug\\$TFM\\Stagecast.Spike.exe' -ArgumentList '--autoclose'"
  echo '--- s0.log ---'
  cat "$logfile"
}

case "$MODE" in
  build) sync_sources; build ;;
  run) run ;;
  all) sync_sources; build; run ;;
  *) echo "usage: $0 [build|run|all]"; exit 2 ;;
esac
