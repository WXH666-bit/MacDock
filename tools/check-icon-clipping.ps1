# Pixel-level check: are Dock icons clipped or flush against the glass bar edges?
#
# COORDINATE SPACE GOTCHA (cost me two wrong runs):
#   Graphics.CopyFromScreen / GetPixel work in PHYSICAL pixels (2560x1600 here),
#   but SystemInformation.VirtualScreen reports LOGICAL pixels (1707x1067).
#   WPF Left/Top/ActualWidth in the log are DIPs == logical px.
#   So: physical = DIP * (dpi/96). Bitmap must be allocated at PHYSICAL size.
#
# Method: read the LAYOUTDIAG log line for DIP rects, convert to physical, then for
# each resting icon slot measure the bounding box of pixels differing from the
# glass-bar background, and compare against the slot's own box.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$log = "$env:APPDATA\MacDock\logs\macdock-$(Get-Date -Format yyyy-MM-dd).log"
$line = Select-String -Path $log -Pattern 'LAYOUTDIAG' | Select-Object -Last 1 | ForEach-Object { $_.Line }
if (-not $line) { Write-Output 'NO LAYOUTDIAG LINE FOUND'; exit }

$n = '(-?[\d.]+)'
$re = "win L=$n T=$n W=$n H=$n panel staticW=$n c0=$n cN=$n icon=$n slot=$n bar L=$n T=$n W=$n H=$n"
$m = [regex]::Match($line, $re)
if (-not $m.Success) { Write-Output 'PARSE FAILED'; Write-Output $line; exit }
function V($i) { [double]$m.Groups[$i].Value }

$winL = V 1; $winT = V 2; $winH = V 4
$c0 = V 6; $cN = V 7; $iconSize = V 8; $slot = V 9
$barL = V 10; $barT = V 11; $barW = V 12; $barH = V 13
$count = [int][Math]::Round((($cN - $c0) / $slot)) + 1

$dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction SilentlyContinue).AppliedDPI
if (-not $dpi) { $dpi = 96 }
$s = $dpi / 96.0

$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$pw = [int][Math]::Round($vs.Width * $s)
$ph = [int][Math]::Round($vs.Height * $s)
$bmp = New-Object System.Drawing.Bitmap $pw, $ph
$gr = [System.Drawing.Graphics]::FromImage($bmp)
$gr.CopyFromScreen(0, 0, 0, 0, $bmp.Size)

# DIP geometry
$iconBottom = $winT + $winH
$iconTop = $iconBottom - $iconSize
$barBottom = $barT + $barH
$barRight = $barL + $barW

Write-Output "logical=$($vs.Width)x$($vs.Height)  physical=${pw}x${ph}  dpi=$dpi scale=$s"
Write-Output "icons=$count iconSize=$iconSize slot=$slot"
Write-Output ("bar DIP     L={0:F1} T={1:F1} R={2:F1} B={3:F1}" -f $barL, $barT, $barRight, $barBottom)
Write-Output ("iconRow DIP top={0:F1} bottom={1:F1}" -f $iconTop, $iconBottom)
Write-Output ("insets      top={0:F1} bottom={1:F1}  (resting icon row inside bar)" -f ($iconTop - $barT), ($barBottom - $iconBottom))
Write-Output ''

# Two disjoint scan bands so the bar's own bottom border can't be mistaken for icon art:
#   band A = the icon box itself      -> measures the icon's bounding box
#   band B = below icon box, but stopping short of the bar's border/rounded corner
#            -> any content here is genuine overflow past the icon box
$yTop = [int][Math]::Round($iconTop * $s)
$iconBottomPx = [int][Math]::Round($iconBottom * $s) - 1
$belowStart = $iconBottomPx + 2
$belowEnd = [Math]::Min([int][Math]::Round($barBottom * $s) - 4, $ph - 1)
# Background reference row: inside bar, above icons, clear of the 1px border
$bgRow = [int][Math]::Round(($barT + ($iconTop - $barT) / 2.0) * $s)

Write-Output "band A (icon box) y=[$yTop..$iconBottomPx]   band B (below) y=[$belowStart..$belowEnd]"
Write-Output ''
Write-Output 'slot  xrange(px)      margins L/R/T/B   bandB hits   verdict'

for ($i = 0; $i -lt $count; $i++) {
    $cx = $winL + $c0 + $i * $slot
    $x0 = [Math]::Max([int][Math]::Round(($cx - $iconSize / 2.0) * $s), 0)
    $x1 = [Math]::Min([int][Math]::Round(($cx + $iconSize / 2.0) * $s) - 1, $pw - 1)

    $minX = 999999; $maxX = -1; $minY = 999999; $maxY = -1
    $belowHits = 0
    for ($x = $x0; $x -le $x1; $x++) {
        $bg = $bmp.GetPixel($x, $bgRow)
        for ($y = $yTop; $y -le $belowEnd; $y++) {
            $p = $bmp.GetPixel($x, $y)
            $d = [Math]::Abs($p.R - $bg.R) + [Math]::Abs($p.G - $bg.G) + [Math]::Abs($p.B - $bg.B)
            if ($d -le 90) { continue }
            if ($y -le $iconBottomPx) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
            elseif ($y -ge $belowStart) { $belowHits++ }
        }
    }

    if ($maxX -lt 0) { Write-Output ("  {0}   [{1}..{2}]   NO CONTENT DETECTED" -f $i, $x0, $x1); continue }

    $ml = $minX - $x0; $mr = $x1 - $maxX; $mt = $minY - $yTop; $mb = $iconBottomPx - $maxY
    $verdict = if ($belowHits -gt 20) { 'OVERFLOW below icon box' }
               elseif ($mb -le 0 -or $mt -le 0 -or $ml -le 0 -or $mr -le 0) { 'FLUSH/CUT at a box edge' }
               else { 'ok' }
    Write-Output ("  {0}   [{1}..{2}]   {3}/{4}/{5}/{6}   {7}   {8}" -f `
        $i, $x0, $x1, $ml, $mr, $mt, $mb, $belowHits, $verdict)
}

# Zoomed crop of the whole bar for visual inspection
$cx0 = [Math]::Max([int](($barL - 14) * $s), 0)
$cy0 = [Math]::Max([int](($barT - 34) * $s), 0)
$cw = [Math]::Min([int](($barW + 28) * $s), $pw - $cx0)
$ch = [Math]::Min([int](($barH + 48) * $s), $ph - $cy0)
$crop = $bmp.Clone((New-Object System.Drawing.Rectangle $cx0, $cy0, $cw, $ch), $bmp.PixelFormat)
$zoom = New-Object System.Drawing.Bitmap ($cw * 2), ($ch * 2)
$gz = [System.Drawing.Graphics]::FromImage($zoom)
$gz.InterpolationMode = 'NearestNeighbor'
$gz.PixelOffsetMode = 'Half'
$gz.DrawImage($crop, 0, 0, $cw * 2, $ch * 2)
$out = "$env:TEMP\dock_check.png"
$zoom.Save($out)
Write-Output ''
Write-Output "saved zoomed crop: $out"

$gz.Dispose(); $zoom.Dispose(); $crop.Dispose(); $gr.Dispose(); $bmp.Dispose()
