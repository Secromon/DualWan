$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$target = Join-Path $PSScriptRoot '..\DualWAN.Dashboard\Assets\DualWAN.ico'
$sizes = @(16,24,32,48,64,128,256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size,$size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = $size / 256.0
    $g.ScaleTransform($scale,$scale)
    $dark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(20,33,61))
    $cyan = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(53,200,232),22)
    $violet = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(142,124,247),22)
    $white = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(244,248,255),22)
    foreach ($pen in @($cyan,$violet,$white)) { $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round; $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round }
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(0,0,108,108,180,90); $path.AddArc(148,0,108,108,270,90)
    $path.AddArc(148,148,108,108,0,90); $path.AddArc(0,148,108,108,90,90); $path.CloseFigure()
    $g.FillPath($dark,$path)
    $g.DrawLines($cyan,[System.Drawing.Point[]]@([System.Drawing.Point]::new(48,70),[System.Drawing.Point]::new(105,70),[System.Drawing.Point]::new(140,119)))
    $g.DrawLines($violet,[System.Drawing.Point[]]@([System.Drawing.Point]::new(48,186),[System.Drawing.Point]::new(105,186),[System.Drawing.Point]::new(140,137)))
    $g.DrawLine($white,143,128,203,128)
    $g.FillEllipse([System.Drawing.Brushes]::White,122,106,44,44)
    $stream = [System.IO.MemoryStream]::new(); $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    $stream.Dispose(); $bitmap.Dispose(); $g.Dispose(); $dark.Dispose(); $cyan.Dispose(); $violet.Dispose(); $white.Dispose(); $path.Dispose()
}
$file = [System.IO.File]::Create($target)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $width = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$width); $writer.Write([byte]$width); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Dispose()
