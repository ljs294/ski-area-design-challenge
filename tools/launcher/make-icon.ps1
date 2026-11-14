<#
.SYNOPSIS
  Packs the game's app icon PNGs (Art/UI/Brand/Icon/icon-<size>.png, task P2-10) into one .ico for the launcher
  (task 4.23). Each size is stored as PNG, which Windows Vista and later read. Exit 1 when a PNG is a Git LFS pointer.
#>
param([Parameter(Mandatory = $true)][string]$Pngs, [Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $Pngs "icon-$s.png"))
    if ($bytes.Length -lt 8 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50) { Write-Host "icon-$s.png isn't a PNG (an LFS pointer?)"; exit 1 }
    , $bytes
}
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$stream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($stream)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Out, $stream.ToArray())
exit 0
