#!/usr/bin/env bash
# Usage: scripts/run-spike.sh [build|run|all]   (default: all)
set -euo pipefail
source "$(dirname "$0")/common.sh"
MODE="${1:-all}"

case "$MODE" in
  build) sync_sources; winps "dotnet build Stagecast.sln -c Debug -nologo -v minimal" ;;
  run) run_spike; echo '--- spike.log ---'; cat "$SPIKE_LOG" ;;
  all) sync_sources; winps "dotnet build Stagecast.sln -c Debug -nologo -v minimal"; run_spike; echo '--- spike.log ---'; cat "$SPIKE_LOG" ;;
  *) echo "usage: $0 [build|run|all]"; exit 2 ;;
esac
