#!/usr/bin/env bash
# Usage: ./run.sh [build|run|all]   (default: all)
# Copies the project to a Windows folder, builds with the Windows .NET SDK, runs the exe.
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)/Stagecast.Spike"
WIN_DIR='C:\dev\browser_render_spike'
WSL_DIR='/mnt/c/dev/browser_render_spike'
MODE="${1:-all}"

sync_sources() {
  mkdir -p "$WSL_DIR"
  rm -rf "$WSL_DIR/Stagecast.Spike/obj" "$WSL_DIR/Stagecast.Spike/bin/logs"
  mkdir -p "$WSL_DIR/Stagecast.Spike"
  # copy sources only; never carry WSL-side bin/obj over
  (cd "$SRC" && find . -path ./bin -prune -o -path ./obj -prune -o -type f -print0 | xargs -0 -I{} cp --parents {} "$WSL_DIR/Stagecast.Spike/")
}

build() {
  powershell.exe -NoProfile -Command "Set-Location '$WIN_DIR\\Stagecast.Spike'; dotnet build -c Debug -nologo -v minimal"
}

run() {
  rm -f "$WSL_DIR/Stagecast.Spike/bin/Debug/net8.0-windows10.0.19041.0/logs/s0.log"
  powershell.exe -NoProfile -Command "Set-Location '$WIN_DIR\\Stagecast.Spike'; Start-Process -Wait -FilePath '.\\bin\\Debug\\net8.0-windows10.0.19041.0\\Stagecast.Spike.exe' -ArgumentList '--autoclose'"
  echo '--- s0.log ---'
  cat "$WSL_DIR/Stagecast.Spike/bin/Debug/net8.0-windows10.0.19041.0/logs/s0.log"
}

case "$MODE" in
  build) sync_sources; build ;;
  run) run ;;
  all) sync_sources; build; run ;;
  *) echo "usage: $0 [build|run|all]"; exit 2 ;;
esac
