# Renders the Haku Player logo (vector, via WPF) to logo.png and a multi-size haku.ico.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$dir = $PSScriptRoot

function Hex([string]$h, [int]$a = 255) {
    [System.Windows.Media.Color]::FromArgb($a, [Convert]::ToByte($h.Substring(1,2),16), [Convert]::ToByte($h.Substring(3,2),16), [Convert]::ToByte($h.Substring(5,2),16))
}
function Brush([string]$h) { New-Object System.Windows.Media.SolidColorBrush((Hex $h)) }
function VGrad([string]$a, [string]$b) {
    $g = New-Object System.Windows.Media.LinearGradientBrush((Hex $a), (Hex $b), 90.0); return $g
}
function Poly($dc, [string]$pts, [string]$h) {
    $p = $pts -split ' '
    $dc.DrawGeometry((Brush $h), $null, [System.Windows.Media.Geometry]::Parse("M $($p[0]) L $($p[1..($p.Count-1)] -join ' ') Z"))
}

# Iceberg shaped like a play button, in a day-sky / deep-sea tile (design "I3").
# Below 40 px a simpler two-tone iceberg is drawn: the facets turn to mush that small.
function Draw([int]$size) {
    $s = $size / 256.0
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($s, $s)))
    $rect = New-Object System.Windows.Rect(8, 8, 240, 240)
    $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry($rect, 62, 62)))
    $small = $size -lt 40
    if ($small) {
        $sea = if ($size -le 16) { 96 } else { 98 }
        $dc.DrawRectangle((Brush '#4FB0DA'), $null, $rect)
        $dc.DrawRectangle((Brush '#0A3F5E'), $null, (New-Object System.Windows.Rect(8, $sea, 240, (248 - $sea))))
        if ($size -le 16) { $x = 16; $y = 14; $k = 224 / 256.0 } else { $x = 26; $y = 24; $k = 204 / 256.0 }
        $dc.PushTransform((New-Object System.Windows.Media.MatrixTransform($k, 0, 0, $k, $x, $y)))
        Poly $dc '56,93 56,18 88,46 100,40 171,93' '#DDF7FD'
        Poly $dc '56,93 171,93 222,134 132,190 136,210 56,240' '#2A9ACB'
        $dc.Pop()
    } else {
        $dc.DrawRectangle((VGrad '#2C8FC0' '#7DCFEA'), $null, $rect)
        $dc.DrawRectangle((VGrad '#0E5F8E' '#062638'), $null, (New-Object System.Windows.Rect(8, 94, 240, 154)))
        $k = 210 / 256.0
        $dc.PushTransform((New-Object System.Windows.Media.MatrixTransform($k, 0, 0, $k, 23, 18)))
        Poly $dc '66,38 80,60 66,92' '#F2FDFF'
        Poly $dc '80,60 92,54 106,72 66,92' '#CDF3FA'
        Poly $dc '106,72 155,92 66,92' '#A6E6F2'
        Poly $dc '106,72 116,68 155,92' '#7FD3EA'
        Poly $dc '66,92 155,92 120,124' '#3FB0D8'
        Poly $dc '155,92 212,128 120,124' '#2A9ACB'
        Poly $dc '66,92 120,124 66,160' '#2391C2'
        Poly $dc '120,124 212,128 146,169 150,188 132,179 100,170' '#1A78AA'
        Poly $dc '66,160 120,124 100,170' '#136896'
        Poly $dc '66,160 100,170 132,179 103,197 108,216 92,204 72,232 66,220' '#0C5480'
        $dc.Pop()
    }
    $dc.Pop()
    if (-not $small) {
        $rim = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush((Hex '#7FDCEB' 90))), 3)
        $dc.DrawRoundedRectangle($null, $rim, (New-Object System.Windows.Rect(9.5, 9.5, 237, 237)), 60.5, 60.5)
    }

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

