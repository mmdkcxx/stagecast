#!/usr/bin/env bash
# Usage: scripts/soak.sh [minutes]   (default 3)
# A long run with NDI, OMT (and the screen when a second monitor exists) on, a moving page with sound and a tab switch every minute.
# Memory and frame counters are sampled every 10 s into <data-dir>\logs\soak-*.csv; the summary line is in the app log.
set -euo pipefail
source "$(dirname "$0")/common.sh"
MINUTES="${1:-3}"
data='C:\dev\stagecast-data'
log="/mnt/c/dev/stagecast-data/logs"
sync_sources
winps "dotnet build Stagecast.sln -c Debug -nologo -v q | Out-Null; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; \$env:STAGECAST_SOAK_MINUTES='$MINUTES'; Start-Process -Wait -FilePath '.\\src\\Stagecast.App\\bin\\Debug\\$TFM\\Stagecast.exe' -ArgumentList '--data-dir','$data','--selftest','soak'"
grep -E "SOAK" "$log"/stagecast-*.log
ls "$log"/soak-*.csv
