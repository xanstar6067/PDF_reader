param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path $PSScriptRoot -Parent
$assetDirectory = Join-Path $projectRoot 'PDF_Reader\PDF_Reader\Assets'
[IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(215, 53, 50))
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = [single]($size * .4)
    $edge = [single]($size - 1)
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc(($edge - $diameter), 0, $diameter, $diameter, 270, 90)
    $path.AddArc(($edge - $diameter), ($edge - $diameter), $diameter, $diameter, 0, 90)
    $path.AddArc(0, ($edge - $diameter), $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $graphics.FillPath($brush, $path)
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $font = [Drawing.Font]::new('Segoe UI', [single]($size * .34), [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $format = [Drawing.StringFormat]::new()
    $format.Alignment = [Drawing.StringAlignment]::Center
    $format.LineAlignment = [Drawing.StringAlignment]::Center
    $graphics.DrawString('PDF', $font, [Drawing.Brushes]::White, [Drawing.RectangleF]::new(0, 0, $edge, $edge), $format)
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $images += ,($stream.ToArray())
    $stream.Dispose(); $format.Dispose(); $font.Dispose(); $path.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $assetDirectory 'paperdesk.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
