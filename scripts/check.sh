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
# Run one spike mode, print its log and assert that each given pattern appears and nothing FAILED.
smoke_mode() { # mode, pattern...
  local mode="$1"; shift
  run_spike "$mode" || return 1
  [ -f "$SPIKE_LOG" ] || { echo "no spike log produced ($mode)"; return 1; }
  echo "--- $mode ---"; cat "$SPIKE_LOG"
  local p
  for p in "$@"; do
    grep -q "$p" "$SPIKE_LOG" || { echo "missing in $mode log: $p"; return 1; }
  done
  ! grep -q "FAILED" "$SPIKE_LOG" || { echo "$mode log contains FAILED"; return 1; }
}

smoke() {
  smoke_mode s1 "webview2 runtime " "navigation completed success=True" || return 1
  smoke_mode s2 "composition controller created" "root visual target set" "navigation completed success=True" "page viewport 2520x1080" "S2 ok" || return 1
  smoke_mode s3 "S3 summary pass=16 fail=0" "S3 ok" || return 1
  smoke_mode s4 "S4 ok 2520x1080" "S4 ok"
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
