#!/usr/bin/env bash
# Usage: scripts/run-spike.sh [build|run|all|browse [profile] [url]]   (default: all)
# browse = S6 interactive: opens the preview window; log in to Canva/Kahoot/Slido by hand, close the window when done.
set -euo pipefail
source "$(dirname "$0")/common.sh"
MODE="${1:-all}"

case "$MODE" in
  build) sync_sources; winps "dotnet build Stagecast.sln -c Debug -nologo -v minimal" ;;
  run) run_spike; echo '--- spike.log ---'; cat "$SPIKE_LOG" ;;
  all) sync_sources; winps "dotnet build Stagecast.sln -c Debug -nologo -v minimal"; run_spike; echo '--- spike.log ---'; cat "$SPIKE_LOG" ;;
  browse)
    sync_sources; winps "dotnet build Stagecast.sln -c Debug -nologo -v minimal"; rm -f "$SPIKE_LOG"
    winps "Start-Process -Wait -FilePath '.\\spikes\\Stagecast.Spike\\bin\\Debug\\$TFM\\Stagecast.Spike.exe' -ArgumentList '--mode','s6i','--profile','${2:-main}','--url','${3:-https://www.canva.com}'"
    echo '--- spike.log ---'; cat "$SPIKE_LOG" ;;
  *) echo "usage: $0 [build|run|all|browse [profile] [url]]"; exit 2 ;;
esac
