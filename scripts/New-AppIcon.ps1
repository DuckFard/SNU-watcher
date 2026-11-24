param(
    [string]$OutputPath,
    [string]$PreviewPath
)

# Draws the SNU Watcher application icon: a registration-blue tile holding a 3x3 seat map in which
# one seat is lit ("a seat opened"). Every size is drawn from the same geometry with edges snapped
# to whole pixels, so small sizes stay sharp instead of being downscaled from the large frame.
# Frames up to 64 px are 32-bit DIBs; the 256 px frame is PNG, as Windows expects.
# Regenerate with: ./scripts/New-AppIcon.ps1 [-PreviewPath review.png]

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'src/SnuWatcher.Desktop/Assets/SnuWatcher.ico' }

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$tileTop = [Drawing.Color]::FromArgb(255, 0x4A, 0x80, 0xDA)
$tileBottom = [Drawing.Color]::FromArgb(255, 0x2F, 0x62, 0xB8)   # Snu.SelectedAccent

function New-RoundedRectangle([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius, [single]$bottomRadius = -1) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    if ($bottomRadius -lt 0) { $bottomRadius = $radius }
    $top = [Math]::Min($radius * 2, [Math]::Min($width, $height))
    $bottom = [Math]::Min($bottomRadius * 2, [Math]::Min($width, $height))
    if ($top -le 0 -and $bottom -le 0) { $path.AddRectangle([Drawing.RectangleF]::new($x, $y, $width, $height)); return $path }
    if ($top -gt 0) {
        $path.AddArc($x, $y, $top, $top, 180, 90)
        $path.AddArc($x + $width - $top, $y, $top, $top, 270, 90)
    }
    else { $path.AddLine($x, $y, $x + $width, $y) }
    if ($bottom -gt 0) {
        $path.AddArc($x + $width - $bottom, $y + $height - $bottom, $bottom, $bottom, 0, 90)
        $path.AddArc($x, $y + $height - $bottom, $bottom, $bottom, 90, 90)
    }
    else { $path.AddLine($x + $width, $y + $height, $x, $y + $height) }
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    $u = $size / 256.0
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
        $g.Clear([Drawing.Color]::Transparent)

        # Tile.
        $inset = if ($size -le 16) { 0 } else { [Math]::Max(1, [Math]::Round(8 * $u)) }
        $tileSize = $size - 2 * $inset
        $tile = New-RoundedRectangle $inset $inset $tileSize $tileSize ([Math]::Max(3, 56 * $u))
        $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new(
            [Drawing.PointF]::new(0, $inset), [Drawing.PointF]::new(0, $size - $inset), $tileTop, $tileBottom)
        $g.FillPath($gradient, $tile)
        $gradient.Dispose(); $tile.Dispose()

        # A 3x3 seat map. Seat size and gap are whole pixels with the canvas's parity, so the grid
        # is centred exactly; taken seats are translucent and the open seat (top right) is lit.
        $span = $size * 196 / 256.0
        $seat = 2 * [Math]::Round(($span - 2 * 20 * $u) / 6)
        $gap = [Math]::Max(1, [Math]::Round(($span - 3 * $seat) / 2))
        $origin = ($size - (3 * $seat + 2 * $gap)) / 2
        # From 32 px up, seats get rounder tops, like rows of seat backs.
        $back = if ($seat -ge 6) { [Math]::Max(1, $seat * 0.38) } else { 0 }
        $base = if ($seat -ge 6) { [Math]::Max(1, $seat * 0.14) } else { 0 }
        $taken = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(97, 255, 255, 255))
        $open = [Drawing.SolidBrush]::new([Drawing.Color]::White)
        for ($row = 0; $row -lt 3; $row++) {
            for ($column = 0; $column -lt 3; $column++) {
                $shape = New-RoundedRectangle ($origin + $column * ($seat + $gap)) ($origin + $row * ($seat + $gap)) $seat $seat $back $base
                $g.FillPath($(if ($row -eq 0 -and $column -eq 2) { $open } else { $taken }), $shape)
                $shape.Dispose()
            }
        }
        $taken.Dispose(); $open.Dispose()
    }
    finally { $g.Dispose() }
    return $bitmap
}

function Get-DibBytes([Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    # BITMAPINFOHEADER; the height covers the colour rows and the AND mask.
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $writer.Write([int]($size * $size * 4 + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    $mask = [byte[]]::new($maskStride * $size)
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = $size - 1 - $y
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$c.B); $writer.Write([byte]$c.G); $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
            if ($c.A -eq 0) { $mask[$row * $maskStride + [int][Math]::Floor($x / 8)] = $mask[$row * $maskStride + [int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) }
        }
    }
    $writer.Write($mask)
    $writer.Flush()
    # The comma keeps PowerShell from unrolling the byte array into the pipeline.
    return , $stream.ToArray()
}

$frames = foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    try {
        if ($size -ge 256) {
            $png = [IO.MemoryStream]::new()
            $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Bytes = $png.ToArray() }
        }
        else { [pscustomobject]@{ Size = $size; Bytes = (Get-DibBytes $bitmap) } }
    }
    finally { $bitmap.Dispose() }
}

$directory = Split-Path $OutputPath -Parent
if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
$file = [IO.File]::Create($OutputPath)
try {
    $writer = [IO.BinaryWriter]::new($file)
    $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]$frame.Bytes.Length); $writer.Write([int]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    $writer.Flush()
}
finally { $file.Dispose() }
Write-Host "Wrote $OutputPath ($($frames.Count) frames: $($sizes -join ', '))"

if ($PreviewPath) {
    # A review sheet: every frame at 1:1 and 4x (nearest neighbour) on light and dark backgrounds.
    $icons = foreach ($size in $sizes) { New-IconBitmap $size }
    $width = 24 + ($sizes | Where-Object { $_ -le 64 } | ForEach-Object { $_ * 4 + 24 } | Measure-Object -Sum).Sum
    $sheet = [Drawing.Bitmap]::new([int][Math]::Max($width, 600), [int](2 * (64 * 4 + 48) + 2 * (256 + 24)))
    $g = [Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
    $y = 0
    foreach ($background in @([Drawing.Color]::FromArgb(255, 0xF3, 0xF3, 0xF3), [Drawing.Color]::FromArgb(255, 0x20, 0x20, 0x20))) {
        $brush = [Drawing.SolidBrush]::new($background)
        $g.FillRectangle($brush, 0, $y, $sheet.Width, 64 * 4 + 48 + 256 + 24)
        $brush.Dispose()
        $x = 24
        foreach ($icon in $icons) {
            if ($icon.Width -gt 64) { continue }
            $g.DrawImage($icon, $x, $y + 8, $icon.Width, $icon.Height)
            $g.DrawImage($icon, $x, $y + 24 + 64, $icon.Width * 4, $icon.Height * 4)
            $x += $icon.Width * 4 + 24
        }
        $g.DrawImage($icons[-1], 24, $y + 64 * 4 + 48, 256, 256)
        $y += 64 * 4 + 48 + 256 + 24
    }
    $g.Dispose()
    $sheet.Save($PreviewPath, [Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose(); foreach ($icon in $icons) { $icon.Dispose() }
    Write-Host "Wrote preview $PreviewPath"
}
