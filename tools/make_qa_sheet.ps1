# 把生成的 .cur / .ani 第一帧渲染成对照图，供视觉检查
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Join-Path $PSScriptRoot "..\cursors"
$root = [System.IO.Path]::GetFullPath($root)
$schemes = @("classic","sakura","mint","night","sunset")
$roles = @("Arrow","IBeam","Crosshair","No","SizeAll","SizeNWSE","Hand","UpArrow","Help","Wait","AppStarting")
$cell = 44

function Get-FrameCur([string]$aniPath) {
    $b = [IO.File]::ReadAllBytes($aniPath)
    $needle = [System.Text.Encoding]::ASCII.GetBytes("icon")
    for ($i = 12; $i -lt $b.Length - 8; $i++) {
        if ($b[$i] -eq $needle[0] -and $b[$i+1] -eq $needle[1] -and $b[$i+2] -eq $needle[2] -and $b[$i+3] -eq $needle[3]) {
            $size = [BitConverter]::ToUInt32($b, $i + 4)
            $frame = New-Object byte[] $size
            [Array]::Copy($b, $i + 8, $frame, 0, $size)
            $tmp = Join-Path $env:TEMP "qa_frame.cur"
            [IO.File]::WriteAllBytes($tmp, $frame)
            return $tmp
        }
    }
    return $null
}

$bmp = New-Object System.Drawing.Bitmap (($roles.Count * $cell + 130), ($schemes.Count * $cell + 26))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$font = New-Object System.Drawing.Font("Microsoft YaHei", 9)
$brush = [System.Drawing.Brushes]::Black

for ($r = 0; $r -lt $schemes.Count; $r++) {
    $sc = $schemes[$r]
    $g.DrawString($sc, $font, $brush, 6, ($r * $cell + 14))
    for ($c = 0; $c -lt $roles.Count; $c++) {
        $role = $roles[$c]
        $ext = ".cur"
        if ($role -eq "Wait" -or $role -eq "AppStarting") { $ext = ".ani" }
        $path = Join-Path (Join-Path $root $sc) ($role + $ext)
        $x = 130 + $c * $cell; $y = $r * $cell + 6
        try {
            $loadPath = $path
            if ($ext -eq ".ani") { $loadPath = Get-FrameCur $path }
            $icon = New-Object System.Drawing.Icon($loadPath, 32, 32)
            $g.DrawIcon($icon, $x, $y)
            $icon.Dispose()
        } catch {
            $g.DrawString("ERR", $font, [System.Drawing.Brushes]::Red, $x, $y)
        }
    }
}
for ($c = 0; $c -lt $roles.Count; $c++) { $g.DrawString($roles[$c], $font, $brush, (130 + $c * $cell), ($schemes.Count * $cell + 4)) }
$g.Dispose()
$out = Join-Path $root "..\qa_sheet.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("sheet saved: " + $out)
