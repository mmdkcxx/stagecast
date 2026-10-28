#!/usr/bin/env bash
# Usage: scripts/check.sh [typecheck|unit|smoke|all]   (default: all)
#   typecheck  full rebuild with nullable analysis and warnings as errors
#   unit       xUnit tests (no window, no network)
#   smoke      runs the spike exe and asserts its log (opens a window for a few seconds, needs network)
#   selftest NAME   one app self test only (a2, a2b, d1a, d5, p2, p1, p4, p5, ...): builds, runs it, passes when its log says "selftest ok"
# Environment: CONTINUE=1 lets smoke run every step and list the failures at the end (default: stop at the first failure).
#              STAGECAST_TEST_PDF=/path/to/file.pdf (WSL path) adds a real document to the d5 self test.
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
  # A WSL path is handed to the Windows process as a Windows path.
  if [ -n "${STAGECAST_TEST_PDF:-}" ]; then export STAGECAST_TEST_PDF WSLENV="${WSLENV:+$WSLENV:}STAGECAST_TEST_PDF/p"; fi
  local data='C:\dev\stagecast-data' log="/mnt/c/dev/stagecast-data/logs"
  winps "dotnet build Stagecast.sln -c Debug -nologo -v q | Out-Null; $(if [ -n "${KEEP_DATA:-}" ]; then :; else echo "Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue;"; fi) Start-Process -Wait -FilePath '.\\src\\Stagecast.App\\bin\\Debug\\$TFM\\Stagecast.exe' -ArgumentList '--autoclose','--data-dir','$data'${APP_EXTRA:-}" || return 1
  echo "--- app ---"; cat "$log"/stagecast-*.log
  local p
  for p in "$@"; do
    grep -q "$p" "$log"/stagecast-*.log || { echo "missing in app log: $p"; return 1; }
  done
}

# With CONTINUE=1 a failing step is recorded and the next one still runs.
smoke_stop() {
  SMOKE_BAD=1
  [ -n "${CONTINUE:-}" ]
}

# One self test of the app: ./check.sh selftest d5
selftest() { # name
  local name="$1"
  [ -n "$name" ] || { echo "usage: $0 selftest NAME"; return 2; }
  APP_EXTRA=",'--selftest','$name'" smoke_app "selftest ok" "exiting with code 0" || return 1
  ! grep -q "selftest FAILED" /mnt/c/dev/stagecast-data/logs/stagecast-*.log
}

smoke() {
  SMOKE_BAD=
  smoke_app "Config \"CreatedDefault\": 2520x1080 at 60 fps" "Main window shown" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a2'" smoke_app "A2 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a2b'" smoke_app "A2b selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a2c'" smoke_app "A2c selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a3'" smoke_app "A3 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a3b'" smoke_app "A3b selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','d1a'" smoke_app "D1a selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','d5'" smoke_app "D5 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','p2'" smoke_app "P2 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','p1'" smoke_app "P1 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','p4'" smoke_app "P4 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','p5'" smoke_app "P5 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','e1'" smoke_app "E1 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a4'" smoke_app "A4 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a5'" smoke_app "A5 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a6'" smoke_app "A6 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a7'" smoke_app "A7 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a8'" smoke_app "A8 selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  APP_EXTRA=",'--selftest','a3c-set'" smoke_app "A3c set selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  KEEP_DATA=1 APP_EXTRA=",'--selftest','a3c-check'" smoke_app "A3c check selftest ok" "exiting with code 0" || { smoke_stop || return 1; }
  smoke_mode s1 "webview2 runtime " "navigation completed success=True" || { smoke_stop || return 1; }
  smoke_mode s2 "composition controller created" "root visual target set" "navigation completed success=True" "page viewport 2520x1080" "S2 ok" || { smoke_stop || return 1; }
  smoke_mode s3 "S3 summary pass=16 fail=0" "S3 ok" || { smoke_stop || return 1; }
  smoke_mode s4 "S4 ok 2520x1080" "S4 ok" || { smoke_stop || return 1; }
  smoke_mode s5 "S5 summary 30 fps" "S5 ok" || { smoke_stop || return 1; }
  smoke_mode s6 "S6 ok video plays" || { smoke_stop || return 1; }
  smoke_mode s8 "WebView2 browser process" "S8 ok" || { smoke_stop || return 1; }
  smoke_mode s9 "tab B loaded" "S9 ok" || { smoke_stop || return 1; }
  smoke_mode s10 "S10 accuracy Fit" "S10 keyboard" "S10 ok" || { smoke_stop || return 1; }
  smoke_mode s11 "restore width 131,25%\|restore width 131.25%" "S11 ok" || { smoke_stop || return 1; }
  smoke_mode s12 "S12 take" "S12 ok"
  [ -z "$SMOKE_BAD" ]
}

sync_sources
case "$MODE" in
  typecheck) step typecheck typecheck ;;
  unit) step unit unit ;;
  smoke) step typecheck typecheck; step smoke smoke ;;
  all) step typecheck typecheck; step unit unit; step smoke smoke ;;
  selftest) step "selftest ${2:-}" selftest "${2:-}" ;;
  *) echo "usage: $0 [typecheck|unit|smoke|all|selftest NAME]"; exit 2 ;;
esac

if [ ${#FAILED[@]} -gt 0 ]; then echo "FAILED: ${FAILED[*]}"; exit 1; fi
echo "ALL PASSED ($MODE)"
