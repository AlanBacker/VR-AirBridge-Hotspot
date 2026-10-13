# Generates src\VRAirBridge.App\app.ico (multi-size, PNG frames). Run: powershell -ExecutionPolicy Bypass -File make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\VRAirBridge.App\app.ico'))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 13, 71, 161)), ([System.Drawing.Color]::FromArgb(255, 30, 136, 229)), 60
    $g.FillPath($brush, $path)
    # Wi-Fi arcs
    $cx = [single]($s / 2); $cy = [single]($s * 0.74)
    $penW = [single][Math]::Max(1.5, $s * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $penW
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    foreach ($k in 0.20, 0.34, 0.48) {
        $rad = [single]($s * $k)
        if ($s -le 16 -and $k -eq 0.48) { continue }
        $g.DrawArc($pen, $cx - $rad, $cy - $rad, $rad * 2, $rad * 2, 225, 90)
    }
    $dot = [single]([Math]::Max(2, $s * 0.11))
    $g.FillEllipse([System.Drawing.Brushes]::White, $cx - $dot / 2, $cy - $dot / 2, $dot, $dot)
    # green "on" badge
    if ($s -ge 32) {
        $b = [single]($s * 0.26)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 204, 113))), $s - $b - $s * 0.06, $s * 0.06, $b, $b)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += , @($s, $ms.ToArray())
}
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]
    $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f[1]) }
$bw.Close()
Write-Output ("icon written: " + (Resolve-Path $Out) + " " + (Get-Item $Out).Length + " bytes")
