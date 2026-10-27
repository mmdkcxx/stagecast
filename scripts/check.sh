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

# Run the real application with a throwaway data folder and assert what it logged.
smoke_app() { # pattern...
  local data='C:\dev\stagecast-data' log="/mnt/c/dev/stagecast-data/logs"
  winps "dotnet build Stagecast.sln -c Debug -nologo -v q | Out-Null; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; Start-Process -Wait -FilePath '.\\src\\Stagecast.App\\bin\\Debug\\$TFM\\Stagecast.exe' -ArgumentList '--autoclose','--data-dir','$data'${APP_EXTRA:-}" || return 1
  echo "--- app ---"; cat "$log"/stagecast-*.log
  local p
  for p in "$@"; do
    grep -q "$p" "$log"/stagecast-*.log || { echo "missing in app log: $p"; return 1; }
  done
}

smoke() {
  smoke_app "Config \"CreatedDefault\": 2520x1080 at 60 fps" "Main window shown" "exiting with code 0" || return 1
  APP_EXTRA=",'--selftest','a2'" smoke_app "A2 selftest ok" "exiting with code 0" || return 1
  APP_EXTRA=",'--selftest','a2b'" smoke_app "A2b selftest ok" "exiting with code 0" || return 1
  APP_EXTRA=",'--selftest','a2c'" smoke_app "A2c selftest ok" "exiting with code 0" || return 1
  APP_EXTRA=",'--selftest','a3'" smoke_app "A3 selftest ok" "exiting with code 0" || return 1
  smoke_mode s1 "webview2 runtime " "navigation completed success=True" || return 1
  smoke_mode s2 "composition controller created" "root visual target set" "navigation completed success=True" "page viewport 2520x1080" "S2 ok" || return 1
  smoke_mode s3 "S3 summary pass=16 fail=0" "S3 ok" || return 1
  smoke_mode s4 "S4 ok 2520x1080" "S4 ok" || return 1
  smoke_mode s5 "S5 summary 30 fps" "S5 ok" || return 1
  smoke_mode s6 "S6 ok video plays" || return 1
  smoke_mode s8 "WebView2 browser process" "S8 ok" || return 1
  smoke_mode s9 "tab B loaded" "S9 ok" || return 1
  smoke_mode s10 "S10 accuracy Fit" "S10 keyboard" "S10 ok" || return 1
  smoke_mode s11 "restore width 131,25%\|restore width 131.25%" "S11 ok" || return 1
  smoke_mode s12 "S12 take" "S12 ok"
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
