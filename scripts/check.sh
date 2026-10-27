#!/usr/bin/env bash
# Usage: scripts/check.sh [typecheck|unit|smoke|all]   (default: all)
#   typecheck  full rebuild with nullable analysis and warnings as errors
#   unit       xUnit tests (no window, no network)
#   smoke      runs the spike exe and asserts its log (opens a window for a few seconds, needs network)
# Run this once per test batch (see docs/TESTING.md), not after every small change.
set -uo pipefail
source "$(dirname "$0")/common.sh"
MODE="${1:-all}"
FAILED=()

step() { # name, command...
  local name="$1"; shift
  echo "=== $name ==="
  if "$@"; then echo "PASS  $name"; else echo "FAIL  $name"; FAILED+=("$name"); fi
  echo
}

typecheck() { winps "dotnet build Stagecast.sln -c Debug --no-incremental -nologo -v minimal -warnaserror"; }
unit() { winps "dotnet test Stagecast.sln -c Debug -nologo -v minimal"; }
smoke() {
  run_spike || return 1
  [ -f "$SPIKE_LOG" ] || { echo "no spike log produced"; return 1; }
  cat "$SPIKE_LOG"
  grep -q "webview2 runtime " "$SPIKE_LOG" || { echo "missing: webview2 runtime"; return 1; }
  grep -q "navigation completed success=True" "$SPIKE_LOG" || { echo "missing: successful navigation"; return 1; }
  ! grep -q "FAILED" "$SPIKE_LOG" || { echo "log contains FAILED"; return 1; }
}

sync_sources
case "$MODE" in
  typecheck) step typecheck typecheck ;;
  unit) step unit unit ;;
  smoke) step typecheck typecheck; step smoke smoke ;;
  all) step typecheck typecheck; step unit unit; step smoke smoke ;;
  *) echo "usage: $0 [typecheck|unit|smoke|all]"; exit 2 ;;
esac

if [ ${#FAILED[@]} -gt 0 ]; then echo "FAILED: ${FAILED[*]}"; exit 1; fi
echo "ALL PASSED ($MODE)"
