# 生成 Assets/app.ico（多尺寸 PNG 内嵌），无需二进制资源入库。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param([System.Drawing.RectangleF]$Rect, [float]$Radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Y, $d, $d, 270, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.RectangleF(($Size * 0.03), ($Size * 0.03), ($Size * 0.94), ($Size * 0.94))
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 92, 160, 255),
        [System.Drawing.Color]::FromArgb(255, 32, 108, 240),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)

    $path = New-RoundedPath -Rect $rect -Radius ($Size * 0.24)
    $g.FillPath($brush, $path)

    $pad = $Size * 0.26
    $gap = $Size * 0.075
    $cell = ($Size - $pad * 2 - $gap) / 2
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    for ($row = 0; $row -lt 2; $row++) {
        for ($col = 0; $col -lt 2; $col++) {
            $x = $pad + $col * ($cell + $gap)
            $y = $pad + $row * ($cell + $gap)
            $cellRect = New-Object System.Drawing.RectangleF($x, $y, $cell, $cell)
            $cellPath = New-RoundedPath -Rect $cellRect -Radius ($cell * 0.3)
            $g.FillPath($white, $cellPath)
            $cellPath.Dispose()
        }
    }

    $white.Dispose()
    $path.Dispose()
    $brush.Dispose()
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

$sizes = @(256, 128, 64, 48, 32, 16)
$images = @{}
foreach ($s in $sizes) { $images[$s] = [byte[]](New-IconPng -Size $s) }

$outPath = Join-Path $PSScriptRoot '..\src\DeskBox\Assets\app.ico'
$outDir = Split-Path $outPath -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $images[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}

foreach ($s in $sizes) { $bw.Write([byte[]]$images[$s]) }

$bw.Flush()
$bw.Close()
$fs.Close()

Write-Output "已生成 $outPath ($((Get-Item $outPath).Length) 字节)"
