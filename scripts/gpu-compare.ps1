# S7: run the spike on each GPU (Windows per-app graphics preference) and report capture stats plus real GPU engine usage.
# Usage (from the Windows copy): powershell -File scripts\gpu-compare.ps1 -Seconds 40
param([int]$Seconds = 40, [string]$Only = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'spikes\Stagecast.Spike\bin\Debug\net8.0-windows10.0.22621.0\Stagecast.Spike.exe'
$log = Join-Path (Split-Path $exe) 'logs\spike.log'
$webview = Get-ChildItem 'C:\Program Files (x86)\Microsoft\EdgeWebView\Application' -Directory | Where-Object Name -match '^\d' | Sort-Object Name | Select-Object -Last 1
$webviewExe = Join-Path $webview.FullName 'msedgewebview2.exe'
$key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
if (-not (Test-Path $key)) { New-Item $key -Force | Out-Null }

# Adapter luid -> name, so counters can be attributed.
$adapters = @{}
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\DirectX' | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    if ($p.Description -and $p.AdapterLuid) { $adapters[('luid_0x{0:x8}_0x{1:x8}' -f ([int64]$p.AdapterLuid -shr 32), ([int64]$p.AdapterLuid -band 0xffffffff))] = $p.Description }
}

$video = [Uri]::new((Join-Path (Split-Path $exe) 'assets\video.html')).AbsoluteUri + '?src=' + [Uri]::EscapeDataString([Uri]::new('C:\dev\media\bbb.mp4').AbsoluteUri)
$scenarios = @(
    @{ Name = 'animation'; Url = $null },
    @{ Name = 'video';     Url = $video }
)
$prefs = @(@{ Name = 'iGPU (power saving)'; Value = 'GpuPreference=1;' }, @{ Name = 'GTX 1650 (high performance)'; Value = 'GpuPreference=2;' })

$saved = @{}
foreach ($f in @($exe, $webviewExe)) { $saved[$f] = (Get-ItemProperty $key -Name $f -ErrorAction SilentlyContinue).$f }
try {
    foreach ($pref in $prefs) {
        if ($Only -and $pref.Name -notlike "*$Only*") { continue }
        foreach ($f in @($exe, $webviewExe)) { Set-ItemProperty $key -Name $f -Value $pref.Value }
        foreach ($sc in $scenarios) {
            Remove-Item $log -ErrorAction SilentlyContinue
            $args = @('--autoclose', '--mode', 's5', '--seconds', $Seconds, '--switch', 'no', '--nopreview')
            if ($sc.Url) { $args += @('--url', $sc.Url) }
            $p = Start-Process $exe -ArgumentList $args -PassThru
            Start-Sleep -Seconds 8
            $perAdapter = @{}
            $samples = 5
            for ($i = 0; $i -lt $samples; $i++) {
                $pids = @($p.Id) + @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -match 'Stagecast' } | ForEach-Object ProcessId)
                $counters = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples
                $byKey = @{}
                foreach ($c in $counters) {
                    if ($c.InstanceName -notmatch 'pid_(\d+)_(luid_0x[0-9a-f]{8}_0x[0-9a-f]{8})_.*engtype_(.+)$') { continue }
                    if ($pids -notcontains [int]$Matches[1]) { continue }
                    $ck = $Matches[2] + '|' + $(switch -Regex ($Matches[3]) { '^3d$' { '3D' } '^video' { 'video' } '^copy$' { 'copy' } default { 'other' } })
                    $byKey[$ck] += $c.CookedValue
                }
                foreach ($k in $byKey.Keys) { $perAdapter[$k] += , $byKey[$k] }
                Start-Sleep -Milliseconds 700
            }
            $p.WaitForExit(($Seconds + 30) * 1000) | Out-Null
            $summary = (Select-String -Path $log -Pattern 'S5 summary' | Select-Object -First 1).Line
            "== $($pref.Name) / $($sc.Name)"
            $summary
            foreach ($k in $perAdapter.Keys) {
                $v = $perAdapter[$k]
                $luid, $engine = $k -split '\|'
                $name = if ($adapters.ContainsKey($luid)) { $adapters[$luid] } else { $luid }
                if (($v | Measure-Object -Maximum).Maximum -lt 0.05) { continue }
                '   GPU {0} [{1}]: avg {2:F1}% max {3:F1}% ({4} samples)' -f $name, $engine, ($v | Measure-Object -Average).Average, ($v | Measure-Object -Maximum).Maximum, $v.Count
            }
            if ($perAdapter.Count -eq 0) { '   no GPU engine counters matched' }
        }
    }
}
finally {
    foreach ($f in $saved.Keys) {
        if ($null -eq $saved[$f]) { Remove-ItemProperty $key -Name $f -ErrorAction SilentlyContinue } else { Set-ItemProperty $key -Name $f -Value $saved[$f] }
    }
    'per-app GPU preference restored'
}
