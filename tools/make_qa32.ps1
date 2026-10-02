# 把 puppy_src 下的 32px 中间图拼成放大检查图（深色背景，模拟深色桌面的可见性）
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$names = @("arrow_front","help_dimu","hand_happy","app_curious","wait_sleep")
$bmp = New-Object System.Drawing.Bitmap (5*160), 180
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(70,70,80))
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
for ($i = 0; $i -lt 5; $i++) {
    $img = [System.Drawing.Image]::FromFile((Join-Path $root "cursors\puppy_src\$($names[$i])_32.png"))
    $g.DrawImage($img, [System.Drawing.Rectangle]::new($i*160+10, 10, 128, 128))
    $img.Dispose()
}
$g.Dispose()
$bmp.Save((Join-Path $root "tools\dog\qa32.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved qa32.png"
