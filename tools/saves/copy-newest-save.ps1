# Copies the player's newest save that holds a simulation, and its area's package, into a scratch data folder, with a
# recent.json that makes the title's Continue open that area (demo 136, finding B2c-5). The player's own folder is only
# read. Writes <scratch>\pick.txt: "<area name>, <save folder> (saved <time>)", or "none".
param(
    [Parameter(Mandatory = $true)][string]$Scratch,
    [string]$Mine = (Join-Path $env:LOCALAPPDATA 'SkiAreaDesignChallenge')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Scratch | Out-Null
$pickFile = Join-Path $Scratch 'pick.txt'
# Library folders aren't always named by package id: map each manifest's id to its folder.
$packages = @{}
Get-ChildItem (Join-Path $Mine 'Resorts') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $manifest = Join-Path $_.FullName 'manifest.json'
    if (Test-Path $manifest) { $packages[(Get-Content $manifest -Raw | ConvertFrom-Json).PackageId] = $_ }
}
# Saves whose area is still in the library and which hold a simulation sidecar; the newest.
$pick = Get-ChildItem (Join-Path $Mine 'Saves') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $packages.ContainsKey($_.Name) } |
    ForEach-Object { Get-ChildItem $_.FullName -Directory } |
    Where-Object { (Test-Path (Join-Path $_.FullName 'save.json')) -and (Get-ChildItem (Join-Path $_.FullName 'sim') -Filter *.sim -ErrorAction SilentlyContinue) } |
    Sort-Object { (Get-Item (Join-Path $_.FullName 'save.json')).LastWriteTime } |
    Select-Object -Last 1
if (-not $pick) { 'none' | Set-Content $pickFile; exit 0 }
$area = $pick.Parent.Name
$package = $packages[$area]
$saves = Join-Path $Scratch "Saves\$area"
New-Item -ItemType Directory -Force $saves | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Scratch 'Resorts') | Out-Null
Copy-Item -Recurse $package.FullName (Join-Path $Scratch 'Resorts')
Copy-Item -Recurse $pick.FullName $saves
"{`"Version`": 1, `"Opened`": {`"$area`": `"2099-01-01T00:00:00Z`"}}" | Set-Content -Encoding ascii (Join-Path $Scratch 'recent.json')
$name = (Get-Content (Join-Path $package.FullName 'manifest.json') -Raw | ConvertFrom-Json).Site.Name
$when = (Get-Item (Join-Path $pick.FullName 'save.json')).LastWriteTime.ToString('MMM d h:mm tt')
"$name, $($pick.Name) (saved $when)" | Set-Content $pickFile
