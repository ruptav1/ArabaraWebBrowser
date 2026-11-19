# Package the ImageGen logo as a Windows ICO on a charcoal tile. The generated source artwork
# (arabara.png) is only scaled; the tile behind it is drawn here: a rounded square in the
# app's charcoal with a faint lighter edge, so the icon reads on dark and light taskbars.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourceImage = [System.Drawing.Bitmap]::new((Join-Path $PSScriptRoot 'arabara.png'))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$charcoal = [System.Drawing.Color]::FromArgb(255, 18, 16, 16)
$edge = [System.Drawing.Color]::FromArgb(255, 48, 42, 40)
# The logo's ink in the source (measured in arabara.png, 1254 px square): x 72..1180, y 212..1092.
$inkLeft = 72; $inkRight = 1180; $inkTop = 212; $inkBottom = 1092

function New-RoundedSquare([single]$x, [single]$y, [single]$side, [single]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $radius * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $side - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $side - $d, $y + $side - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$frames = [System.Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            # The tile: a little margin from 32 px up, corners rounded by a fifth of its side.
            $inset = [single][Math]::Floor($size / 32)
            $side = [single]($size - 2 * $inset)
            $tile = New-RoundedSquare $inset $inset $side ([single]($side * 0.2))
            $brush = [System.Drawing.SolidBrush]::new($charcoal)
            $graphics.FillPath($brush, $tile)
            $brush.Dispose()
            if ($size -ge 32) {
                $pen = [System.Drawing.Pen]::new($edge, [single][Math]::Max(1, $size / 128))
                $half = $pen.Width / 2
                $outline = New-RoundedSquare ($inset + $half) ($inset + $half) ($side - $pen.Width) ([single](($side - $pen.Width) * 0.2))
                $graphics.DrawPath($pen, $outline)
                $outline.Dispose(); $pen.Dispose()
            }
            $tile.Dispose()
            # The logo: its ink 64% of the tile's width (78% at the smallest sizes), centred on the tile.
            $share = if ($size -le 24) { 0.78 } else { 0.64 }
            $scale = $side * $share / ($inkRight - $inkLeft)
            $width = [single]($sourceImage.Width * $scale)
            $height = [single]($sourceImage.Height * $scale)
            $x = [single]($size / 2 - (($inkLeft + $inkRight) / 2) * $scale)
            $y = [single]($size / 2 - (($inkTop + $inkBottom) / 2) * $scale)
            $graphics.DrawImage($sourceImage, $x, $y, $width, $height)
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
            if ($size -eq 128) { $bitmap.Save((Join-Path $PSScriptRoot 'arabara-mark-128.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $sourceImage.Dispose() }
$output = [System.IO.File]::Create((Join-Path $PSScriptRoot 'arabara.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose() }
