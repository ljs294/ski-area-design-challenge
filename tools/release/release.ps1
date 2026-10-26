<#
.SYNOPSIS
  The one-command Windows release (task P2-10): checks, EditMode tests, the release build with the Jackson Hole demo,
  a smoke test of the shipped exe, then the zip a tester unpacks and runs.

.DESCRIPTION
  release.bat (repo root) and demo.bat 58 run this. Steps:
    1. Checks: Unity 6000.3.25f1, the project closed in the editor, a clean working tree, real LFS files (it runs
       `git lfs checkout`, local only, when some are pointers; it never runs `git lfs pull`).
    2. The version: a commit tagged vX.Y.Z is release X.Y.Z; anything else is a preview, "0.2.0+dev.7.abc1234".
    3. EditMode tests in batch mode, which also compiles every assembly (CI's dotnet build doesn't); fresh results only,
       and exit 198 means the Unity licence lapsed. -SkipTests runs only the compile check.
    4. The release build (ViewerSetup.BuildRelease) into <Out>\Player. Without the demo it fails: the demo comes from
       -DemoPackage, else the newest Jackson Hole 5 km in this PC's library (demo.bat 12 downloads it).
    5. The smoke test, in the GPU render lane: the exe starts as a fresh install (empty library, offline), the title comes
       in from the built-in demo, a forced exception reaches the error log, and it quits 0. -NoSmoke skips it.
    5b. Symbols (task P3-14): a tagged release keeps what crash dumps need in <Symbols>\<version> (default
       D:\skiAreaDesignChallenge\symbols, beside the main checkout), outside git and never in the zip; -KeepSymbols
       keeps a preview's too. tools/reports/README.md says how to use them.
    6. The zip <Out>\SkiAreaDesignChallenge-<version>-win64.zip with README.txt and NOTICES.txt, its .sha256 and
       release.json (what was built and checked).
  A tagged release refuses -AllowDirty, -SkipTests and -NoSmoke.
#>
param(
    [switch]$AllowDirty,
    [switch]$SkipTests,
    [switch]$NoSmoke,
    [string]$DemoPackage = '',
    [string]$Out = '',
    [switch]$KeepSymbols,
    [string]$Symbols = '',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-lib.ps1')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'Builds\Release' }
$results = Join-Path $root 'test-results\release'
$clock = [System.Diagnostics.Stopwatch]::StartNew()

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text, [int]$code = 1) {
    Write-Host ''
    Write-Host "RELEASE FAILED: $text" -ForegroundColor Red
    exit $code
}

# ---- 1. Checks ----
Step 'Checks'
if (-not (Test-Path $Unity)) { Fail "Unity 6000.3.25f1 was not found at $Unity." }
if (Test-EditorOpen $root) { Fail 'The project is open in the Unity editor: close it first.' }
$status = & git -C $root status --porcelain --untracked-files=no
$dirty = [bool]$status
if ($dirty -and -not $AllowDirty) { Fail "The working tree has uncommitted changes (commit them, or pass -AllowDirty for a preview):`n$($status -join "`n")" }
$pointers = Get-LfsPointers $root
if ($pointers.Count -gt 0) {
    Write-Host "  $($pointers.Count) LFS files are still pointers: git lfs checkout (from the local LFS store, no download)..."
    & git -C $root lfs checkout | Out-Host
    $pointers = Get-LfsPointers $root
    if ($pointers.Count -gt 0) {
        Fail "$($pointers.Count) LFS files have no local object, e.g. $($pointers[0]). Fetching them uses the free GitHub LFS bandwidth: ask before running git lfs pull."
    }
}
Write-Host '  Unity found, project closed, LFS files real.'

# ---- 2. Version ----
$tag = Get-ReleaseTag $root
$release = ($null -ne $tag) -and -not $dirty
if ($release -and ($SkipTests -or $NoSmoke)) { Fail "$tag is a release: it runs every test (drop -SkipTests and -NoSmoke)." }
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($release) { Write-Host "  Release $tag at $($commit.Substring(0, 7))" }
else { Write-Host "  Preview build at $($commit.Substring(0, 7)) (no clean vX.Y.Z tag here); the version reads 0.x.y+dev..." }

New-Item -ItemType Directory -Force $results, $Out | Out-Null
$report = [ordered]@{ commit = $commit; dirty = $dirty; tag = $tag; tests = 'skipped'; smoke = 'skipped' }

# ---- 3. EditMode tests (and the compile check) ----
if ($SkipTests) {
    Step 'Compile check (tests skipped)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'compile-check.ps1') -ProjectPath $root -Unity $Unity -LogFile (Join-Path $results 'compile.log')
    if ($LASTEXITCODE -ne 0) { Fail 'The compile check failed (above).' $LASTEXITCODE }
    $report.tests = 'compile only'
} else {
    Step 'EditMode tests (this compiles every assembly too; about 5 minutes)'
    $xml = Join-Path $results 'editmode.xml'
    $log = Join-Path $results 'editmode.log'
    $started = Get-Date
    $code = Invoke-Unity $Unity @('-batchmode', '-projectPath', $root, '-runTests', '-testPlatform', 'EditMode', '-testResults', $xml, '-logFile', $log)
    $t = Read-TestResult $xml $started $code
    if (-not $t.Ok) {
        $compile = Read-CompileResult $log $started $code
        foreach ($line in $compile.Errors) { Write-Host "  $line" }
        Fail "EditMode tests: $($t.Message). See $log and $xml." $(if ($code -eq 198) { 198 } else { 1 })
    }
    Write-Host "  EditMode $($t.Message)"
    # The trail paint's upload broke only with a render thread, as the player runs (findings 2026-10-20): its checks
    # again under -force-gfx-mt.
    Step 'Trail paint upload with a render thread'
    $xml = Join-Path $results 'editmode-gfx-mt.xml'
    $log = Join-Path $results 'editmode-gfx-mt.log'
    $started = Get-Date
    $code = Invoke-Unity $Unity @('-batchmode', '-force-gfx-mt', '-projectPath', $root, '-runTests', '-testPlatform', 'EditMode', '-testFilter', 'MountainPlanner.Tests.TrailPaintMapTests', '-testResults', $xml, '-logFile', $log)
    $mt = Read-TestResult $xml $started $code
    if (-not $mt.Ok) { Fail "The trail paint checks with a render thread: $($mt.Message). See $log and $xml." 1 }
    Write-Host "  Render thread $($mt.Message)"
    $report.tests = "EditMode $($t.Message); render thread $($mt.Message)"
}

# ---- 4. Build ----
Step 'Release build with the bundled demo (a few minutes)'
$player = Join-Path $Out 'Player'
$buildLog = Join-Path $results 'build.log'
$buildArgs = @('-batchmode', '-projectPath', $root, '-executeMethod', 'MountainPlanner.Editor.ViewerSetup.BuildRelease', '-out', $player, '-logFile', $buildLog)
if ($DemoPackage) { $buildArgs += @('-demoPackage', $DemoPackage) }
$started = Get-Date
$code = Invoke-Unity $Unity $buildArgs
if ($code -eq 198) { Fail 'Unity exited 198: the licence lapsed. Refresh it in Unity Hub, then run this again.' 198 }
$versionFile = Join-Path $player 'version.txt'
if ($code -ne 0 -or -not (Test-Path $versionFile) -or (Get-Item $versionFile).LastWriteTime -lt $started) {
    $why = @(Select-String -Path $buildLog -Pattern 'Bundling the demo failed|error CS\d{4}|Build Failed' -ErrorAction SilentlyContinue | Select-Object -First 5 | ForEach-Object { $_.Line.Trim() })
    foreach ($line in $why) { Write-Host "  $line" }
    Fail "The build failed (Unity exited $code). See $buildLog."
}
$version = (Get-Content -Raw $versionFile).Trim()
if ($release -and $version -ne $tag.Substring(1)) { Fail "The build stamped $version, but HEAD is tagged $tag." }
if ($release -and -not (Test-ReleaseVersion $version)) { Fail "$version isn't a release version." }
$exe = Join-Path $player "$($script:ProductFile).exe"
Write-Host "  Built $version"
$report.version = $version

# ---- 5. Smoke test ----
if (-not $NoSmoke) {
    Step 'Smoke test of the shipped exe (GPU render lane): fresh install, offline, forced exception'
    $smoke = Join-Path $results 'smoke'
    if (Test-Path $smoke) { Remove-Item -Recurse -Force $smoke }
    New-Item -ItemType Directory -Force (Join-Path $smoke 'library'), (Join-Path $smoke 'logs') | Out-Null
    $gpu = Join-Path $root 'tools\gpu\gpu.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $gpu run -Lane render -Name release -TimeoutSec 600 -- $exe -smoketest -offline -data (Join-Path $smoke 'library') -errorlogs (Join-Path $smoke 'logs') -logFile (Join-Path $smoke 'player.log')
    $code = $LASTEXITCODE
    $line = Select-String -Path (Join-Path $smoke 'player.log') -Pattern '\[Smoke\]' -ErrorAction SilentlyContinue | Select-Object -Last 1
    $errorLog = Get-ChildItem (Join-Path $smoke 'logs') -Filter 'errors-*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
    $text = ''
    if ($errorLog) { $text = Get-Content -Raw $errorLog.FullName }
    if ($code -ne 0 -or -not $errorLog -or $text -notmatch [regex]::Escape("v$version") -or $text -notmatch 'Forced test exception') {
        if ($line) { Write-Host "  $($line.Line.Trim())" }
        Fail "The smoke test failed (exit $code). See $smoke."
    }
    Write-Host "  $($line.Line.Trim())"
    $report.smoke = 'passed'
}

# ---- 5b. Symbols ----
if ($release -or $KeepSymbols) {
    Step 'Symbols (kept outside git and the zip)'
    if (-not $Symbols) { $Symbols = Get-SymbolsRoot $root }
    $kept = Save-Symbols $player $root $Unity $version $Symbols
    Write-Host "  $($kept.Files) files in $($kept.Folder) (game PDBs $($kept.ManagedPdbs) of $($kept.ManagedDlls))"
    $report.symbols = $kept.Folder
}

# ---- 6. Zip ----
Step 'Zip'
$name = Get-ReleaseName $version
$stage = Join-Path $Out 'stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$top = Join-Path $stage $name
New-Item -ItemType Directory -Force $top | Out-Null
Get-ChildItem $player | Where-Object { $_.Name -notmatch 'DoNotShip' -and $_.Name -ne 'version.txt' } | Copy-Item -Destination $top -Recurse
$stagedBytes = (Get-ChildItem $top -Recurse -File | Measure-Object -Property Length -Sum).Sum
$size = "$([math]::Ceiling($stagedBytes / 100MB) * 100) MB"
$readme = (Get-Content -Raw (Join-Path $PSScriptRoot 'README.txt')).Replace('{VERSION}', $version).Replace('{SIZE}', $size)
[System.IO.File]::WriteAllText((Join-Path $top 'README.txt'), $readme.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
$licences = Get-Content -Raw (Join-Path $root 'Assets\MountainPlanner\Art\UI\Flow\Resources\MountainPlannerFlow\Licences.txt')
$notices = (Get-Content -Raw (Join-Path $PSScriptRoot 'NOTICES.txt')) + "`n" + $licences
[System.IO.File]::WriteAllText((Join-Path $top 'NOTICES.txt'), $notices.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
$zip = Join-Path $Out (Get-ZipName $version)
if (Test-Path $zip) { Remove-Item -Force $zip }
& $script:Tar -a -c -f $zip -C $stage $name
if ($LASTEXITCODE -ne 0) { Fail "tar couldn't write $zip." }
$problems = Test-ZipLayout $zip $version
if ($problems.Count -gt 0) { Fail "The zip's layout is wrong: $($problems -join '; ')." }
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -Encoding ascii
Remove-Item -Recurse -Force $stage
$report.zip = Split-Path -Leaf $zip
$report.bytes = (Get-Item $zip).Length
$report.sha256 = $hash
$report.minutes = [math]::Round($clock.Elapsed.TotalMinutes, 1)
$report | ConvertTo-Json | Set-Content -Path (Join-Path $Out 'release.json') -Encoding utf8

Write-Host ''
$kind = 'Preview'
if ($release) { $kind = 'Release' }
Write-Host "$kind $version is ready: $zip ($([math]::Round($report.bytes / 1MB)) MB, $($report.minutes) min)" -ForegroundColor Green
Write-Host "  SHA-256 $hash"
exit 0
