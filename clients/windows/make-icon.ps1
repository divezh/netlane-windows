# Builds every icon file from the raster master assets/netlane-icon.png:
# the Windows app icon, the panel favicon and the PNG used by the panel and its previews.
# Run after replacing the artwork: powershell -ExecutionPolicy Bypass -File clients/windows/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$master = [Drawing.Image]::FromFile((Join-Path $root 'assets/netlane-icon.png'))

function New-Scaled([int]$size) {
    $bitmap = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.DrawImage($master, 0, 0, $size, $size)
    $graphics.Dispose()
    return $bitmap
}

function Write-Png([string]$path, [int]$size) {
    $bitmap = New-Scaled $size
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $images = @()
    foreach ($size in $sizes) {
        $bitmap = New-Scaled $size
        $stream = New-Object IO.MemoryStream
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $images += , @{ Size = $size; Bytes = $stream.ToArray() }
        $stream.Dispose(); $bitmap.Dispose()
    }
    $file = [IO.File]::Open($path, [IO.FileMode]::Create)
    $writer = New-Object IO.BinaryWriter($file)
    try {
        $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$images.Count)
        $offset = 6 + 16 * $images.Count
        foreach ($image in $images) {
            $side = [byte]$(if ($image.Size -ge 256) { 0 } else { $image.Size })
            $writer.Write($side); $writer.Write($side); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([UInt16]1); $writer.Write([UInt16]32)
            $writer.Write([UInt32]$image.Bytes.Length); $writer.Write([UInt32]$offset)
            $offset += $image.Bytes.Length
        }
        foreach ($image in $images) { $writer.Write($image.Bytes) }
    } finally { $writer.Dispose(); $file.Dispose() }
    Write-Output ("Icon written: " + $path + " (sizes " + ($sizes -join ', ') + ")")
}

try {
    Write-Ico (Join-Path $PSScriptRoot 'Netlane.ico') @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    Write-Ico (Join-Path $root 'web/favicon.ico') @(16, 32, 48)
    Write-Png (Join-Path $root 'web/icon.png') 128
    Write-Png (Join-Path $root 'design/icon.png') 128
} finally { $master.Dispose() }
