# Builds assets\opencfdrive.ico (the service's application icon, which
# Explorer also shows for the drive) from assets\logo.svg.
#
# Microsoft Edge renders the SVG once at 1024 px (headless, transparent
# background); each icon size is scaled down from that. As usual for .ico
# files, 256 px is stored as PNG and the smaller sizes as 32-bit bitmaps,
# which every icon reader understands.
#
# Usage:
#   .\scripts\assets\Build-Icon.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path (Split-Path $PSScriptRoot)
$svg = Join-Path $repoRoot "assets\logo.svg"
$ico = Join-Path $repoRoot "assets\opencfdrive.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function ConvertTo-PngBytes([System.Drawing.Bitmap]$Bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $stream.ToArray()
}

# An icon bitmap: BITMAPINFOHEADER (height doubled for the mask), BGRA rows
# bottom-up, then the 1-bit AND mask (set where a pixel is fully transparent).
function ConvertTo-IconDib([System.Drawing.Bitmap]$Bitmap) {
    $size = $Bitmap.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $Bitmap.UnlockBits($data)

    $maskStride = [int][Math]::Floor(($size + 31) / 32) * 4
    $mask = New-Object byte[] ($maskStride * $size)

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([uint32]40); $writer.Write([int32]$size); $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
    $writer.Write([uint32]($size * $size * 4 + $mask.Length))
    $writer.Write([int32]0); $writer.Write([int32]0); $writer.Write([uint32]0); $writer.Write([uint32]0)
    for ($row = $size - 1; $row -ge 0; $row--) {
        $writer.Write($pixels, $row * $data.Stride, $size * 4)
        $maskRow = ($size - 1 - $row) * $maskStride
        for ($column = 0; $column -lt $size; $column++) {
            if ($pixels[$row * $data.Stride + $column * 4 + 3] -eq 0) {
                $mask[$maskRow + [int][Math]::Floor($column / 8)] = $mask[$maskRow + [int][Math]::Floor($column / 8)] -bor (0x80 -shr ($column % 8))
            }
        }
    }
    $writer.Write($mask)
    $writer.Flush()
    return , $stream.ToArray()
}

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge not found; it renders the SVG." }

$work = Join-Path $env:TEMP ("opencfdrive-icon-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Copy-Item $svg (Join-Path $work "logo.svg")
    $html = '<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}' +
            'img{display:block;width:1024px;height:1024px}</style></head><body><img src="logo.svg"></body></html>'
    [System.IO.File]::WriteAllText((Join-Path $work "render.html"), $html)

    $png = Join-Path $work "logo-1024.png"
    $page = "file:///" + (Join-Path $work "render.html").Replace('\', '/')
    $edgeArgs = @("--headless=new", "--disable-gpu", "--hide-scrollbars", "--default-background-color=00000000",
                  "--window-size=1024,1024", "--screenshot=$png", $page)
    Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
    if (-not (Test-Path $png)) { throw "Edge did not render $svg." }

    Add-Type -AssemblyName System.Drawing
    $source = [System.Drawing.Image]::FromFile($png)
    $images = foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.DrawImage($source, 0, 0, $size, $size)
        $graphics.Dispose()

        $bytes = if ($size -ge 256) { ConvertTo-PngBytes $bitmap } else { ConvertTo-IconDib $bitmap }
        $bitmap.Dispose()
        [pscustomobject]@{ Size = $size; Bytes = $bytes }
    }
    $source.Dispose()

    # ICO: a 6-byte header, one 16-byte entry per image, then the PNG data.
    $out = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $out
    $writer.Write([uint16]0)               # reserved
    $writer.Write([uint16]1)               # type: icon
    $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }   # 0 means 256
        $writer.Write([byte]$dimension)    # width
        $writer.Write([byte]$dimension)    # height
        $writer.Write([byte]0)             # palette colours
        $writer.Write([byte]0)             # reserved
        $writer.Write([uint16]1)           # colour planes
        $writer.Write([uint16]32)          # bits per pixel
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) { $writer.Write($image.Bytes) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($ico, $out.ToArray())
    $writer.Dispose()

    Write-Host "Wrote $ico ($($sizes -join ', ') px)" -ForegroundColor Green
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
