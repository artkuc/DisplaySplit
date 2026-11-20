# Regenerates src/DisplaySplit/Assets/app.ico (multi-size PNG ICO). Run: pwsh tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\DisplaySplit\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [Math]::Max(1, [int]($s * 0.06)); $w = $s - 2 * $pad; $h = [int]($w * 0.72); $y = [int](($s - $h) / 2)
    $r = [Math]::Max(2, [int]($s * 0.12))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $y, $r, $r, 180, 90); $path.AddArc($pad + $w - $r, $y, $r, $r, 270, 90)
    $path.AddArc($pad + $w - $r, $y + $h - $r, $r, $r, 0, 90); $path.AddArc($pad, $y + $h - $r, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 36, 44))), $path)
    $gap = [Math]::Max(1, [int]($s * 0.05)); $inner = $w - 2 * $gap; $colW = ($inner - 2 * $gap) / 4.0
    $cols = @(@(0, 1, 120), @(1, 2, 255), @(3, 1, 120))
    foreach ($c in $cols) {
        $x0 = $pad + $gap + $c[0] * $colW + $c[0] * 0 + ([Math]::Min($c[0], 2) * $gap / 1.0)
        if ($c[0] -eq 3) { $x0 = $pad + $gap + 3 * $colW + 2 * $gap }
        $cw = $c[1] * $colW
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($c[2], 76, 141, 255))
        $g.FillRectangle($brush, [float]$x0, [float]($y + $gap), [float]$cw, [float]($h - 2 * $gap))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
"wrote $out"
