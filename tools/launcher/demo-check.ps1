<#
.SYNOPSIS
  What demo.bat 114 and 115 (task 4.23) show after the launcher ran: the versions in an install root, how many of its
  files carry the Mark of the Web (Zone.Identifier; it should be none, since the launcher downloads them itself), and a
  fingerprint of the saves folder, so a run before and after an update shows the saves were untouched.
  Read-only: it changes nothing.
#>
param([Parameter(Mandatory = $true)][string]$Root, [string]$Label = '')
$ErrorActionPreference = 'Stop'
if ($Label) { Write-Host $Label }
$versions = Join-Path $Root 'versions'
if (Test-Path $versions) {
    foreach ($d in Get-ChildItem $versions -Directory | Sort-Object Name) {
        $complete = Test-Path (Join-Path $d.FullName '.complete')
        $files = @(Get-ChildItem $d.FullName -Recurse -File)
        $mb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB)
        $state = 'incomplete (never played)'
        if ($complete) { $state = 'complete' }
        Write-Host ("  versions\{0}: {1} files, {2} MB, {3}" -f $d.Name, $files.Count, $mb, $state)
    }
} else { Write-Host "  No versions folder in $Root." }
$all = @(Get-ChildItem $Root -Recurse -File -ErrorAction SilentlyContinue)
$marked = @($all | Where-Object { Get-Item -LiteralPath $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue })
Write-Host ("  Files with a download mark (Zone.Identifier): {0} of {1}" -f $marked.Count, $all.Count)
foreach ($m in $marked | Select-Object -First 3) { Write-Host "    $($m.FullName)" }

$saves = Join-Path $env:LOCALAPPDATA 'SkiAreaDesignChallenge\Saves'
if (Test-Path $saves) {
    $items = @(Get-ChildItem $saves -Recurse -File | Sort-Object FullName)
    $text = ($items | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-', '').Substring(0, 12).ToLowerInvariant()
    Write-Host ("  Your saves (%LOCALAPPDATA%\SkiAreaDesignChallenge\Saves): {0} files, fingerprint {1}" -f $items.Count, $hash)
} else { Write-Host '  No saves folder yet.' }
