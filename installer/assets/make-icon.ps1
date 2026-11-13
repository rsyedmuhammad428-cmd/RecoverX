<#
.SYNOPSIS  Draws the RecoverX icon (blue rounded square, white circular "recover" arrow, RX) with GDI+ and writes
           installer\assets\RecoverX.ico (16, 24, 32, 48, 64, 128, 256 px; PNG-compressed entries, supported since Windows Vista).
#>
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "RecoverX.ico"
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.TextRenderingHint = "AntiAliasGridFit"; $g.Clear([Drawing.Color]::Transparent)
    $r = [single]($s * 0.22); $pad = [single]([Math]::Max(0.5, $s * 0.03)); $w = [single]($s - 2 * $pad)
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $r, $r, 180, 90); $path.AddArc($pad + $w - $r, $pad, $r, $r, 270, 90)
    $path.AddArc($pad + $w - $r, $pad + $w - $r, $r, $r, 0, 90); $path.AddArc($pad, $pad + $w - $r, $r, $r, 90, 90); $path.CloseFigure()
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s),
        [Drawing.Color]::FromArgb(255, 0x3B, 0x8E, 0xF5), [Drawing.Color]::FromArgb(255, 0x1D, 0x5F, 0xC4))
    $g.FillPath($brush, $path)
    # circular arrow (recovery)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([single][Math]::Max(1.2, $s * 0.075))
    $pen.StartCap = "Round"; $pen.EndCap = "Round"
    $m = [single]($s * 0.22); $d = [single]($s - 2 * $m)
    $g.DrawArc($pen, $m, $m, $d, $d, -60, 300)
    $ax = [single]($s / 2 + $d / 2 * [Math]::Cos(-60 * [Math]::PI / 180)); $ay = [single]($s / 2 + $d / 2 * [Math]::Sin(-60 * [Math]::PI / 180))
    $h = [single]($s * 0.13)
    $tri = [Drawing.PointF[]]@((New-Object Drawing.PointF ($ax - $h * 0.2), ($ay - $h)), (New-Object Drawing.PointF ($ax + $h), ($ay - $h * 0.1)), (New-Object Drawing.PointF ($ax - $h * 0.3), ($ay + $h * 0.7)))
    $g.FillPolygon([Drawing.Brushes]::White, $tri)
    if ($s -ge 32) {
        $font = New-Object Drawing.Font "Segoe UI", ([single]($s * 0.26)), ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = "Center"; $fmt.LineAlignment = "Center"
        $g.DrawString("RX", $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF 0, 0, $s, $s), $fmt)
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $pngs += ,($ms.ToArray())
}
$o = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $o
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[IO.File]::WriteAllBytes($out, $o.ToArray())
"wrote $out ($($o.Length) bytes)"
