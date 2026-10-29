# Renders the Haku Player logo (vector, via WPF) to logo.png and a multi-size haku.ico.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$dir = $PSScriptRoot

function Draw([int]$size) {
    $s = $size / 256.0
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($s, $s)))
    $rect = New-Object System.Windows.Rect(8, 8, 240, 240)

    # glass tile: pink -> violet -> blue
    $grad = New-Object System.Windows.Media.LinearGradientBrush
    $grad.StartPoint = '0,0'; $grad.EndPoint = '1,1'
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xFF,0x7A,0x8C), 0)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xC2,0x3B,0xD0), 0.55)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x3A,0x5B,0xFF), 1)))
    $dc.DrawRoundedRectangle($grad, $null, $rect, 62, 62)

    # soft light from the top-left
    $glow = New-Object System.Windows.Media.RadialGradientBrush
    $glow.Center = '0.25,0.15'; $glow.GradientOrigin = '0.25,0.1'; $glow.RadiusX = 0.8; $glow.RadiusY = 0.8
    $glow.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(110,255,255,255), 0)))
    $glow.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(0,255,255,255), 1)))
    $dc.DrawRoundedRectangle($glow, $null, $rect, 62, 62)

    # glossy sheen on the upper half
    $sheen = New-Object System.Windows.Media.LinearGradientBrush
    $sheen.StartPoint = '0,0'; $sheen.EndPoint = '0,1'
    $sheen.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(70,255,255,255), 0)))
    $sheen.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(0,255,255,255), 0.75)))
    $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry($rect, 62, 62)))
    $dc.DrawEllipse($sheen, $null, (New-Object System.Windows.Point(128, 40)), 170, 110)
    $dc.Pop()

    # glass edge
    $edge = New-Object System.Windows.Media.LinearGradientBrush
    $edge.StartPoint = '0,0'; $edge.EndPoint = '0,1'
    $edge.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(170,255,255,255), 0)))
    $edge.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(30,255,255,255), 1)))
    $inner = New-Object System.Windows.Rect(10, 10, 236, 236)
    $dc.DrawRoundedRectangle($null, (New-Object System.Windows.Media.Pen($edge, 3)), $inner, 60, 60)

    # soft white play symbol (rounded corners), with a faint shadow
    $tri = [System.Windows.Media.Geometry]::Parse("M 104,78 L 104,178 L 186,128 Z")
    $shadowPen = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(60,40,0,80))), 34)
    $shadowPen.LineJoin = 'Round'
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform(0, 6)))
    $dc.DrawGeometry($shadowPen.Brush, $shadowPen, $tri)
    $dc.Pop()
    $white = [System.Windows.Media.Brushes]::White
    $pen = New-Object System.Windows.Media.Pen($white, 34); $pen.LineJoin = 'Round'
    $dc.DrawGeometry($white, $pen, $tri)

    # sparkle (four-point star), top right
    $star = [System.Windows.Media.Geometry]::Parse("M 196,44 Q 199,61 214,64 Q 199,67 196,84 Q 193,67 178,64 Q 193,61 196,44 Z")
    $dc.DrawGeometry($white, $null, $star)
    $dc.DrawEllipse($white, $null, (New-Object System.Windows.Point(220, 92)), 4, 4)

    $dc.Pop(); $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream; $enc.Save($ms); return ,$ms.ToArray()
}

[System.IO.File]::WriteAllBytes("$dir\logo.png", (Draw 512))

# .ico with PNG-compressed entries
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = $sizes | ForEach-Object { ,(Draw $_) }
$ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter($ms)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = $sizes[$i] % 256
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush(); [System.IO.File]::WriteAllBytes("$dir\haku.ico", $ms.ToArray())
"ok"

