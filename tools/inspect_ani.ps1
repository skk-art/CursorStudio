# Inspect Windows' own animated cursor files to learn the real .ani chunk layout,
# especially the frame-delay units (anih.jifRate / 'rate' chunk).
$ErrorActionPreference = "Stop"

function Read-U32($b, [int]$o) {
    return [uint32]($b[$o]) -bor ([uint32]($b[$o+1]) -shl 8) -bor ([uint32]($b[$o+2]) -shl 16) -bor ([uint32]($b[$o+3]) -shl 24)
}
function FourCC($b, [int]$o) {
    return [System.Text.Encoding]::ASCII.GetString($b, $o, 4)
}

function Inspect-Ani([string]$path) {
    if (-not (Test-Path $path)) { Write-Output "MISSING: $path"; return }
    Write-Output "==== $path ===="
    $b = [IO.File]::ReadAllBytes($path)
    Write-Output ("file size: " + $b.Length)
    Write-Output ("riff: " + (FourCC $b 0) + " size=" + (Read-U32 $b 4) + " form=" + (FourCC $b 8))

    $pos = 12
    $iconCount = 0
    $rateSamples = @()
    while ($pos -lt $b.Length - 8) {
        $id = FourCC $b $pos
        $size = [int](Read-U32 $b ($pos + 4))
        if ($size -lt 0 -or $pos + 8 + $size -gt $b.Length) { Write-Output ("bad chunk at " + $pos); break }
        $dataOff = $pos + 8
        switch ($id) {
            "anih" {
                $cbSize   = Read-U32 $b ($dataOff + 0)
                $nFrames  = Read-U32 $b ($dataOff + 4)
                $nSteps   = Read-U32 $b ($dataOff + 8)
                $cx       = Read-U32 $b ($dataOff + 12)
                $cy       = Read-U32 $b ($dataOff + 16)
                $bitCount = Read-U32 $b ($dataOff + 20)
                $planes   = Read-U32 $b ($dataOff + 24)
                $jifRate  = Read-U32 $b ($dataOff + 28)
                $fl       = Read-U32 $b ($dataOff + 32)
                Write-Output ("anih: cbSize=$cbSize nFrames=$nFrames nSteps=$nSteps ${cx}x${cy} bitCount=$bitCount planes=$planes jifRate=$jifRate flags=$fl")
            }
            "rate" {
                $n = [int]($size / 4)
                $vals = @()
                for ($i = 0; $i -lt [Math]::Min($n, 6); $i++) { $vals += (Read-U32 $b ($dataOff + 4 * $i)) }
                Write-Output ("rate: count=$n first=" + ($vals -join ","))
                $script:rateSamples = $vals
            }
            "seq" {
                $n = [int]($size / 4)
                $vals = @()
                for ($i = 0; $i -lt [Math]::Min($n, 6); $i++) { $vals += (Read-U32 $b ($dataOff + 4 * $i)) }
                Write-Output ("seq : count=$n first=" + ($vals -join ","))
            }
            "LIST" {
                $listType = FourCC $b $dataOff
                Write-Output ("LIST type=$listType size=$size")
                if ($listType -eq "fram") {
                    $p = $dataOff + 4
                    $end = $dataOff + $size
                    while ($p -lt $end - 8) {
                        $cid = FourCC $b $p
                        $csz = [int](Read-U32 $b ($p + 4))
                        if ($cid -eq "icon") { $iconCount++ }
                        $p += 8 + $csz + (($csz % 2))  # chunks are word-aligned
                    }
                }
            }
            default { Write-Output ("chunk: $id size=$size") }
        }
        $pos = $dataOff + $size + ($size % 2)
    }
    Write-Output ("embedded icon frames: $iconCount")
    Write-Output ""
}

Inspect-Ani "$env:SystemRoot\Cursors\aero_busy.ani"
Inspect-Ani "$env:SystemRoot\Cursors\aero_working.ani"
Inspect-Ani "$env:SystemRoot\Cursors\appstart.ani"
