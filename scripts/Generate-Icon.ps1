$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $root 'src\PixelPin.App\Assets'
$iconPath = Join-Path $assetDirectory 'PixelPin.ico'
$previewPath = Join-Path $assetDirectory 'PixelPin.png'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-PixelPinBitmap([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $scale = $size / 256.0
    $graphics.ScaleTransform($scale, $scale)

    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 23, 105, 224))
    $whitePen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 13)
    $whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $whitePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pinBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 180, 67))
    $backgroundPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $backgroundPath.AddArc(8, 8, 108, 108, 180, 90)
    $backgroundPath.AddArc(140, 8, 108, 108, 270, 90)
    $backgroundPath.AddArc(140, 140, 108, 108, 0, 90)
    $backgroundPath.AddArc(8, 140, 108, 108, 90, 90)
    $backgroundPath.CloseFigure()
    $graphics.FillPath($background, $backgroundPath)

    $graphics.DrawLine($whitePen, 70, 96, 70, 70)
    $graphics.DrawLine($whitePen, 70, 70, 96, 70)
    $graphics.DrawLine($whitePen, 160, 70, 186, 70)
    $graphics.DrawLine($whitePen, 186, 70, 186, 96)
    $graphics.DrawLine($whitePen, 186, 160, 186, 186)
    $graphics.DrawLine($whitePen, 186, 186, 160, 186)
    $graphics.DrawLine($whitePen, 96, 186, 70, 186)
    $graphics.DrawLine($whitePen, 70, 186, 70, 160)

    $pinPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $pinPath.FillMode = [System.Drawing.Drawing2D.FillMode]::Winding
    $pinPath.AddEllipse(82, 78, 92, 92)
    $pinPath.AddPolygon([System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(92, 132),
        [System.Drawing.Point]::new(164, 132),
        [System.Drawing.Point]::new(128, 196)
    ))
    $graphics.FillPath($pinBrush, $pinPath)
    $graphics.FillEllipse($accentBrush, 118, 114, 20, 20)

    $accentBrush.Dispose()
    $pinPath.Dispose()
    $pinBrush.Dispose()
    $backgroundPath.Dispose()
    $whitePen.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = New-PixelPinBitmap $size
    try {
        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
        if ($size -eq 256) {
            $bitmap.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $stream.Dispose()
    }
    finally {
        $bitmap.Dispose()
    }
}

$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)
$dataOffset = 6 + 16 * $sizes.Count
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $size = $sizes[$index]
    $frame = $frames[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frame.Length)
    $writer.Write([UInt32]$dataOffset)
    $dataOffset += $frame.Length
}
foreach ($frame in $frames) {
    $writer.Write($frame)
}
[System.IO.File]::WriteAllBytes($iconPath, $output.ToArray())
$writer.Dispose()
$output.Dispose()

Write-Host "Icon: $iconPath"
Write-Host "Preview: $previewPath"
