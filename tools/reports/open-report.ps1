<#
.SYNOPSIS
  Opens a player's problem report on the owner's PC (task P3-14, decision PR9): the area, the save and the settings
  they had, in a scratch library, with their system info printed.

.DESCRIPTION
  demo.bat 71 runs this. Give it a report zip, or a GitHub issue number whose report zip was dragged into it:
    open-report.ps1 -Report C:\path\report-20261025-143210.zip
    open-report.ps1 -Issue 12
    open-report.ps1            (the newest report in %LOCALAPPDATA%\SkiAreaDesignChallenge\Reports)
  It works in D:\skiAreaDesignChallenge\reports\<issue or report name> (beside the main checkout, outside git):
    1. An issue's zip is downloaded there (the issue's attachment link; public, no sign-in).
    2. The zip is unpacked, refusing any file that would land outside its folder (reports come from other PCs).
    3. report.json is printed: kind, title, note, version and commit, system, area, files.
    4. A scratch library (data\) gets the area, copied from your library when you have it (else the script names it,
       with its centre and size, to download in the game), and the save, as data\Saves\<package>\report-save.
    5. The game starts with -data <scratch> -reportsettings report.json: the player's settings for that session only,
       under their own prefix, never over yours. Load Area lists the save.
  Nothing is uploaded and nothing in your own library or settings changes. Crash dumps: see tools/reports/README.md.
#>
param(
    [string]$Report = '',
    [int]$Issue = 0,
    [string]$Library = (Join-Path $env:LOCALAPPDATA 'SkiAreaDesignChallenge'),
    [string]$Game = '',
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$common = (& git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
$work = Join-Path (Split-Path (Split-Path $common -Parent) -Parent) 'reports'
function Fail([string]$text) { Write-Host "OPEN REPORT FAILED: $text" -ForegroundColor Red; exit 1 }

# ---- 1. The zip ----
if ($Issue -gt 0) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { Fail 'The GitHub CLI (gh) was not found.' }
    $json = & gh issue view $Issue --repo ljs294/ski-area-design-challenge --json title,body,comments,url | ConvertFrom-Json
    $text = $json.body + "`n" + (($json.comments | ForEach-Object { $_.body }) -join "`n")
    $link = [regex]::Matches($text, 'https://github\.com/(?:user-attachments/files|[\w.-]+/[\w.-]+/files)/[^\s)\]]+\.zip') | Select-Object -First 1
    if (-not $link) { Fail "Issue #$Issue ($($json.url)) has no report zip attached." }
    $folder = Join-Path $work "issue-$Issue"
    New-Item -ItemType Directory -Force $folder | Out-Null
    $Report = Join-Path $folder (Split-Path -Leaf $link.Value)
    Write-Host "Issue #${Issue}: $($json.title)"
    Write-Host "  downloading $($link.Value)"
    Invoke-WebRequest -UseBasicParsing -Uri $link.Value -OutFile $Report
} elseif (-not $Report) {
    $newest = Get-ChildItem (Join-Path $Library 'Reports') -Filter 'report-*.zip' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $newest) { Fail "No report given, and none in $(Join-Path $Library 'Reports')." }
    $Report = $newest.FullName
}
if (-not (Test-Path $Report)) { Fail "$Report doesn't exist." }
$Report = (Resolve-Path $Report).Path
if ($Issue -le 0) { $folder = Join-Path $work ([System.IO.Path]::GetFileNameWithoutExtension($Report)) }

# ---- 2. Unpack, safely ----
$unpacked = Join-Path $folder 'report'
if (Test-Path $unpacked) { Remove-Item -Recurse -Force $unpacked }
New-Item -ItemType Directory -Force $unpacked | Out-Null
$prefix = (Resolve-Path $unpacked).Path.TrimEnd('\') + '\'
$zip = [System.IO.Compression.ZipFile]::OpenRead($Report)
try {
    foreach ($entry in $zip.Entries) {
        if (-not $entry.Name) { continue }
        $target = [System.IO.Path]::GetFullPath((Join-Path $unpacked $entry.FullName))
        if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { Fail "The report holds a file outside its folder ($($entry.FullName)); it wasn't opened." }
        New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $zip.Dispose() }
$jsonPath = Join-Path $unpacked 'report.json'
if (-not (Test-Path $jsonPath)) { Fail "$Report has no report.json: it isn't a problem report." }
$r = Get-Content -Raw $jsonPath | ConvertFrom-Json
if ($r.Version -gt 1) { Write-Host "  This report is version $($r.Version); this script knows version 1. Some fields may be missing." -ForegroundColor Yellow }

# ---- 3. What it says ----
Write-Host ''
Write-Host "$($r.Kind): $($r.Title)" -ForegroundColor Cyan
if ($r.Note) { Write-Host "  $($r.Note -replace "`n", "`n  ")" }
$crashed = ''
if ($r.AfterCrash) { $crashed = ', sent after a crash' }
Write-Host "  Game     $($r.DisplayVersion) (commit $($r.Commit))$crashed"
Write-Host "  Made     $($r.CreatedUtc)"
if ($r.System) { $r.System.PSObject.Properties | ForEach-Object { Write-Host ("  {0,-16} {1}" -f $_.Name, $_.Value) } }
$area = $r.Area
if ($area) {
    $line = "  Area     $($area.Name) ($($area.PackageId)), $([math]::Round($area.Latitude, 4)), $([math]::Round($area.Longitude, 4)), $($area.SizeMetres / 1000) km"
    if ($area.SaveName) { $line += "; save '$($area.SaveName)'" }
    Write-Host $line
}
Write-Host "  Settings $(@($r.Settings.PSObject.Properties).Count) values"
Write-Host "  Files    $(($r.Files | ForEach-Object { $_.Path }) -join ', ')"
Write-Host "  Unpacked in $unpacked"
$dump = Get-ChildItem (Join-Path $unpacked 'crash') -Filter '*.dmp' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($dump) {
    $symbols = Join-Path (Split-Path $work -Parent) "symbols\$($r.GameVersion)"
    Write-Host "  Crash dump: $($dump.FullName)" -ForegroundColor Yellow
    if (Test-Path $symbols) { Write-Host "    symbol path: $symbols\native;$symbols\burst;srv*$env:TEMP\symcache*https://symbolserver.unity3d.com" }
    else { Write-Host "    no symbols kept for $($r.GameVersion) (release.ps1 keeps them for tagged releases)" }
}

# ---- 4. A scratch library ----
$data = Join-Path $folder 'data'
New-Item -ItemType Directory -Force $data | Out-Null
$haveArea = $false
if ($area -and $area.PackageId) {
    $mine = Join-Path $Library "Resorts\$($area.PackageId)"
    $theirs = Join-Path $data "Resorts\$($area.PackageId)"
    if (Test-Path $theirs) { $haveArea = $true }
    elseif (Test-Path $mine) {
        Write-Host "  Copying $($area.Name) from your library..."
        & robocopy $mine $theirs /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { Fail "robocopy couldn't copy $mine (exit $LASTEXITCODE)." }
        $haveArea = $true
    } else {
        Write-Host "  $($area.Name) isn't in your library ($Library). If it isn't the built-in demo, download it in the game" -ForegroundColor Yellow
        Write-Host "  (New Area) at $([math]::Round($area.Latitude, 5)), $([math]::Round($area.Longitude, 5)), $($area.SizeMetres / 1000) km: it downloads into this scratch library." -ForegroundColor Yellow
    }
    $save = Join-Path $unpacked 'save'
    if (Test-Path (Join-Path $save 'save.json')) {
        $target = Join-Path $data "Saves\$($area.PackageId)\report-save"
        if (Test-Path $target) { Remove-Item -Recurse -Force $target }
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item (Join-Path $save '*') $target -Recurse
        Write-Host "  The save is in Load Area under $($area.Name)."
    }
}

# ---- 5. The game ----
if ($NoLaunch) { Write-Host "Scratch library: $data"; exit 0 }
if (-not $Game) {
    foreach ($candidate in (Join-Path $root 'Builds\Windows\SkiAreaDesignChallenge.exe'), (Join-Path $root 'Builds\Release\Player\SkiAreaDesignChallenge.exe')) {
        if (Test-Path $candidate) { $Game = $candidate; break }
    }
}
if (-not $Game -or -not (Test-Path $Game)) { Fail 'No game build found: build it (demo.bat 71 builds it), or pass -Game.' }
Write-Host ''
Write-Host "Starting $Game with the report's settings for this session only (your own settings stay as they are)."
if ($r.GameVersion) { Write-Host "  The report came from $($r.DisplayVersion); a different build may behave differently." }
& $Game -data $data -reportsettings $jsonPath
exit 0
