#!/usr/bin/env bash
# Usage: scripts/perf-gpu.sh igpu|gtx      (about 5 minutes)
# The perf scenario (see perf.sh) with Stagecast and WebView2 forced to the iGPU (power saving) or the GTX (high performance) through the
# Windows per-app graphics preference. The preference is restored afterwards. Compare with scripts/perf.sh (Windows chooses).
set -euo pipefail
source "$(dirname "$0")/common.sh"
PREF="${1:?usage: perf-gpu.sh igpu|gtx}"
data='C:\dev\stagecast-data'
log="/mnt/c/dev/stagecast-data/logs"
sync_sources
mkdir -p "$WSL_DIR/scripts" && cp "$REPO/scripts/perf-gpu.ps1" "$WSL_DIR/scripts/"
if [ -n "${STAGECAST_PERF_URLS:-}" ]; then export STAGECAST_PERF_URLS WSLENV="${WSLENV:+$WSLENV:}STAGECAST_PERF_URLS"; fi
winps "dotnet build Stagecast.sln -c Debug -nologo -v q | Out-Null; \$env:PERF_GPU_PREF='$PREF'; \$env:PERF_GPU_DATA='$data'; Get-Content .\\scripts\\perf-gpu.ps1 -Raw | Invoke-Expression"
grep -E "PERF|Capture device" "$log"/stagecast-*.log
ls "$log"/perf-*.csv
