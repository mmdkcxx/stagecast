# Runs the perf scenario with Stagecast.exe and msedgewebview2.exe forced to one GPU (Windows per-app graphics preference), then puts the
# preference back exactly as it was (also when the run fails). Run by scripts/perf-gpu.sh, inline from the Windows copy of the repo (script files
# are blocked by the execution policy here); the parameters come from PERF_GPU_PREF (igpu|gtx) and PERF_GPU_DATA.
$ErrorActionPreference = 'Stop'
$Pref = $env:PERF_GPU_PREF
$Data = $env:PERF_GPU_DATA
if ($Pref -notin 'igpu', 'gtx' -or -not $Data) { throw 'PERF_GPU_PREF (igpu|gtx) and PERF_GPU_DATA are required' }
$root = (Get-Location).Path
$exe = (Resolve-Path (Join-Path $root 'src\Stagecast.App\bin\Debug\net8.0-windows10.0.22621.0\Stagecast.exe')).Path
$webview = Get-ChildItem 'C:\Program Files (x86)\Microsoft\EdgeWebView\Application' -Directory | Where-Object Name -match '^\d' | Sort-Object Name | Select-Object -Last 1
$webviewExe = Join-Path $webview.FullName 'msedgewebview2.exe'
$key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
if (-not (Test-Path $key)) { New-Item $key -Force | Out-Null }
$value = if ($Pref -eq 'igpu') { 'GpuPreference=1;' } else { 'GpuPreference=2;' }

$saved = @{}
foreach ($f in @($exe, $webviewExe)) { $saved[$f] = (Get-ItemProperty $key -Name $f -ErrorAction SilentlyContinue).$f }
try {
    foreach ($f in @($exe, $webviewExe)) { Set-ItemProperty $key -Name $f -Value $value }
    Write-Host "Graphics preference $value set for $exe and $webviewExe"
    Remove-Item $Data -Recurse -Force -ErrorAction SilentlyContinue
    Start-Process -Wait -FilePath $exe -ArgumentList '--data-dir', $Data, '--selftest', 'perf'
}
finally {
    foreach ($f in $saved.Keys) {
        if ($null -ne $saved[$f]) { Set-ItemProperty $key -Name $f -Value $saved[$f] }
        else { Remove-ItemProperty $key -Name $f -ErrorAction SilentlyContinue }
    }
    Write-Host 'Graphics preference restored'
}
