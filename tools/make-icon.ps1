<#
.SYNOPSIS
    Draws the Openhold icon (three stacked layers on a blue tile) and writes it as a multi-size .ico.

.DESCRIPTION
    Each size is drawn separately rather than scaled, so the small sizes stay crisp. Writes
    src\Openhold.Desktop\Openhold.ico (used by Openhold Explorer and the job worker). -Preview saves a 256 px PNG to look at.
#>
param([string] $Output = (Join-Path $PSScriptRoot "..\src\Openhold.Desktop\Openhold.ico"), [string] $Preview)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-RoundedRectangle([float] $x, [float] $y, [float] $w, [float] $h, [float] $r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# One layer of the stack: a flat diamond (a slab seen from above at an angle), with a darker front edge.
function Draw-Layer($g, [float] $cx, [float] $cy, [float] $w, [float] $h, [float] $thick, $top, $edge) {
    $topPoints = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($cx, ($cy - $h / 2))),
        (New-Object System.Drawing.PointF(($cx + $w / 2), $cy)),
        (New-Object System.Drawing.PointF($cx, ($cy + $h / 2))),
        (New-Object System.Drawing.PointF(($cx - $w / 2), $cy)))
    $edgePoints = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($cx - $w / 2), $cy)),
        (New-Object System.Drawing.PointF($cx, ($cy + $h / 2))),
        (New-Object System.Drawing.PointF(($cx + $w / 2), $cy)),
        (New-Object System.Drawing.PointF(($cx + $w / 2), ($cy + $thick))),
        (New-Object System.Drawing.PointF($cx, ($cy + $h / 2 + $thick))),
        (New-Object System.Drawing.PointF(($cx - $w / 2), ($cy + $thick))))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($edge)), $edgePoints)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($top)), $topPoints)
}

function New-IconImage([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile: deep engineering blue, a little lighter at the top.
    $s = [float] $size
    $tile = New-RoundedRectangle 0 0 ($s - 0.5) ($s - 0.5) ($s * 0.2)
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, $s)),
        [System.Drawing.Color]::FromArgb(255, 0x2B, 0x78, 0xB0), [System.Drawing.Color]::FromArgb(255, 0x12, 0x3E, 0x63))
    $g.FillPath($gradient, $tile)

    # Three layers, bottom to top, getting lighter.
    $w = $s * 0.66; $h = $s * 0.30; $thick = [Math]::Max(1.0, $s * 0.06); $step = $s * 0.15
    $cx = $s / 2; $base = $s * 0.66
    Draw-Layer $g $cx $base $w $h $thick ([System.Drawing.Color]::FromArgb(255, 0x7F, 0xB8, 0xE0)) ([System.Drawing.Color]::FromArgb(255, 0x4A, 0x86, 0xB4))
    Draw-Layer $g $cx ($base - $step) $w $h $thick ([System.Drawing.Color]::FromArgb(255, 0xB6, 0xD8, 0xF0)) ([System.Drawing.Color]::FromArgb(255, 0x6F, 0xA3, 0xCB))
    Draw-Layer $g $cx ($base - 2 * $step) $w $h $thick ([System.Drawing.Color]::White) ([System.Drawing.Color]::FromArgb(255, 0x9F, 0xC6, 0xE4))

    $g.Dispose()
    return $bitmap
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-IconImage $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256 -and $Preview) { $bitmap.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bitmap.Dispose()
    , $stream.ToArray()
}

# ICO file: header, one directory entry per size, then each image as PNG (supported since Windows Vista).
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $Output)).Path + "\" + (Split-Path $Output -Leaf), $file.ToArray())
Write-Host "Wrote $Output ($($sizes -join ', ') px)"
