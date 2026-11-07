#!/usr/bin/env bash
# Usage: scripts/perf.sh            (about 3 minutes)
# The repeatable performance scenario: 2520x1080@60 with the preview on, an idle page, a page opening (one row per second),
# a steady page, and NDI / OMT with and without a receiver. One CSV row per phase is written to <data-dir>\logs\perf-*.csv
# and one "PERF <phase>" line per phase to the app log. Compare runs before and after a change; see docs/perf/.
# STAGECAST_PERF_URLS=https://site1,https://site2 uses the first real site instead of the built-in moving test page.
set -euo pipefail
source "$(dirname "$0")/common.sh"
data='C:\dev\stagecast-data'
log="/mnt/c/dev/stagecast-data/logs"
sync_sources
if [ -n "${STAGECAST_PERF_URLS:-}" ]; then export STAGECAST_PERF_URLS WSLENV="${WSLENV:+$WSLENV:}STAGECAST_PERF_URLS"; fi
winps "dotnet build Stagecast.sln -c Debug -nologo -v q | Out-Null; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; Start-Process -Wait -FilePath '.\\src\\Stagecast.App\\bin\\Debug\\$TFM\\Stagecast.exe' -ArgumentList '--data-dir','$data','--selftest','perf'"
grep -E "PERF" "$log"/stagecast-*.log
ls "$log"/perf-*.csv
