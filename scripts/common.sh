# Shared by run-spike.sh and check.sh. Source it; do not execute.
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WIN_DIR='C:\dev\stagecast'
WSL_DIR='/mnt/c/dev/stagecast'
TFM='net8.0-windows10.0.22621.0'
SPIKE_LOG="$WSL_DIR/spikes/Stagecast.Spike/bin/Debug/$TFM/logs/spike.log"

# Copy the buildable parts of the repo to the Windows folder (the repo itself stays on the Linux side).
sync_sources() {
  mkdir -p "$WSL_DIR"
  rm -rf "$WSL_DIR/spikes" "$WSL_DIR/src" "$WSL_DIR/tests" "$WSL_DIR/third_party" "$WSL_DIR/installer"
  (cd "$REPO" && tar --exclude=bin --exclude=obj --exclude=logs --exclude=.gitkeep \
     -cf - spikes src tests third_party installer global.json Directory.Build.props .editorconfig Stagecast.sln) \
    | tar -xf - -C "$WSL_DIR"
}

# Run a PowerShell command inside the Windows copy of the repo.
winps() {
  powershell.exe -NoProfile -Command "Set-Location '$WIN_DIR'; $1"
}

# Run the spike exe in the given mode (s1|s2, default s2) for a few seconds (it closes itself)
# and leave its log in $SPIKE_LOG.
run_spike() {
  local mode="${1:-s2}"
  rm -f "$SPIKE_LOG"
  winps "Start-Process -Wait -FilePath '.\\spikes\\Stagecast.Spike\\bin\\Debug\\$TFM\\Stagecast.Spike.exe' -ArgumentList '--autoclose','--mode','$mode'"
}
