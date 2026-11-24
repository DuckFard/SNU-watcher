param(
    [string]$OutputPath,
    [string]$PreviewPath
)

# Draws the SNU Watcher application icon: the app's brand mark, a white bell on a registration-blue
# circle, as shown in the window header (StudentWindow BrandMark). The bell is drawn here as an original
# filled shape, not taken from the Segoe font glyph the header uses. Every size is drawn from the same
# vector geometry rather than downscaled, with the clapper kept clear of the bell at small sizes.
# Frames up to 64 px are 32-bit DIBs; the 256 px frame is PNG, as Windows expects.
# Regenerate with: ./scripts/New-AppIcon.ps1 [-PreviewPath review.png]

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'src/SnuWatcher.Desktop/Assets/SnuWatcher.ico' }

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$brand = [Drawing.Color]::FromArgb(255, 0x37, 0x6D, 0xC8)   # Snu.Navy, the header mark's background

function New-IconBitmap([int]$size) {
    $u = $size / 256.0
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
        $g.Clear([Drawing.Color]::Transparent)

        # The circle fills the frame at 16 px and keeps a small margin above that.
        $inset = if ($size -le 16) { 0 } else { [Math]::Max(0.5, 6 * $u) }
        $circle = [Drawing.SolidBrush]::new($brand)
        $g.FillEllipse($circle, [single]$inset, [single]$inset, [single]($size - 2 * $inset), [single]($size - 2 * $inset))
        $circle.Dispose()

        # Bell, in 256-unit coordinates. Below 32 px the knob would be sub-pixel, so it is left out
        # and the bell moves up to stay centred.
        $knob = $size -ge 32
        $dy = if ($knob) { 0 } else { -6 }
        function P([double]$x, [double]$y) { [Drawing.PointF]::new([single]($x * $u), [single](($y + $dy) * $u)) }
        $bell = [Drawing.Drawing2D.GraphicsPath]::new()
        $bell.AddBezier((P 64 174), (P 78 164), (P 84 150), (P 84 130))
        $bell.AddLine((P 84 130), (P 84 112))
        $bell.AddBezier((P 84 112), (P 84 86), (P 104 68), (P 128 68))
        $bell.AddBezier((P 128 68), (P 152 68), (P 172 86), (P 172 112))
        $bell.AddLine((P 172 112), (P 172 130))
        $bell.AddBezier((P 172 130), (P 172 150), (P 178 164), (P 192 174))
        $bell.CloseFigure()
        $white = [Drawing.SolidBrush]::new([Drawing.Color]::White)
        $g.FillPath($white, $bell)
        $bell.Dispose()
        if ($knob) { $g.FillEllipse($white, [single](118 * $u), [single](50 * $u), [single](20 * $u), [single](20 * $u)) }

        # Clapper: a half disc under the lip, always at least one pixel clear of it.
        $lip = (174 + $dy) * $u
        $radius = [Math]::Max(1.5, 20 * $u)
        $top = $lip + [Math]::Max(1, 12 * $u)
        $g.FillPie($white, [single](128 * $u - $radius), [single]($top - $radius), [single](2 * $radius), [single](2 * $radius), 0, 180)
        $white.Dispose()
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
