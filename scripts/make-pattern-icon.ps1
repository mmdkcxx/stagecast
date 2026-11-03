# Draws the Stagecast Pattern icon (a yellow "P" over seven colour bars on a dark rounded square) and writes it as a multi-size .ico (PNG frames).
# Usage (PowerShell, from the repo root): .\scripts\make-pattern-icon.ps1 -Out src\Stagecast.Pattern\pattern.ico
param([string]$Out = "src\Stagecast.Pattern\pattern.ico", [string]$PreviewPng = "")
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($size * 0.22); $d = $r * 2; $w = [single]($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 12, 11, 9))), $path)
    $border = [single][Math]::Max(1, $size / 32)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 247, 194, 67)), $border), $path)
    # The "P" on the upper part, seven colour bars along the bottom (same geometry as the logo mockup, 100 units = the icon).
    $u = [single]($size / 100)
    $font = New-Object System.Drawing.Font 'Segoe UI Black', ([single]($size * 0.60)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $box = New-Object System.Drawing.RectangleF 0, ([single](($size * 0.40) - ($size * 0.30) + $size * 0.02)), $size, ([single]($size * 0.60))
    $g.DrawString('P', $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 247, 194, 67))), $box, $fmt)
    $g.SmoothingMode = 'None' # whole pixels, so the bars stay crisp at 16 px
    $colours = '#FFFFFF', '#FFFF00', '#00FFFF', '#00FF00', '#FF00FF', '#FF0000', '#0000FF'
    $x0 = [Math]::Round(16 * $u); $x1 = [Math]::Round(84 * $u); $y0 = [Math]::Round(66 * $u); $y1 = [Math]::Round(84 * $u)
    for ($i = 0; $i -lt 7; $i++) {
        $bx0 = [int][Math]::Round($x0 + ($x1 - $x0) * $i / 7); $bx1 = [int][Math]::Round($x0 + ($x1 - $x0) * ($i + 1) / 7)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($colours[$i]))), $bx0, [int]$y0, ($bx1 - $bx0), ([int]($y1 - $y0)))
    }
    $g.SmoothingMode = 'AntiAlias'
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewPng -and $size -eq 256) { [System.IO.File]::WriteAllBytes($PreviewPng, $ms.ToArray()) }
    if ($PreviewPng -and $size -eq 16) { [System.IO.File]::WriteAllBytes($PreviewPng + '.16.png', $ms.ToArray()) }
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
    $g.Dispose(); $bmp.Dispose()
}
$stream = [System.IO.File]::Create((Join-Path (Get-Location) $Out))
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Close(); $stream.Close()
"icon written: $Out ($($frames.Count) sizes)"
