<#
.SYNOPSIS
  Runs the EditMode tests a change can move (task 4.26 BB10): the Unity-side check CI can't make (CI has no Unity, and
  Unity runs the engine-free Core tests under Mono, where timings and allocations differ from .NET's). Run by the opt-in
  pre-push hook (tools/git-hooks/pre-push); by hand: tools/release/editmode-affected.ps1 [-Base origin/main] [-ListOnly].

.DESCRIPTION
  A test fixture is picked when its file changed, or when it names a type from a changed C# file (the file's name up to
  its first dot: WeatherFields.cs and MountainViewer.Skiers.cs give WeatherFields and MountainViewer). An asmdef change
  adds the architecture tests. Running tests compiles every assembly too, so a compile error fails it.
  Exit codes: 0 passed; 1 a test or the compile failed; 2 the project is open in the editor; 3 Unity not found or no fresh
  results; 4 no fixture picked (nothing run); 198 the Unity licence lapsed.
#>
param(
    [string]$ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe',
    [string]$ChangedList = '',
    [string]$Base = 'origin/main',
    [switch]$ListOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-lib.ps1')

# The changed paths (repo-relative, forward slashes): from the hook's list, or this branch against $Base.
if ($ChangedList) {
    $changed = @(Get-Content $ChangedList | Where-Object { $_ })
} else {
    $ErrorActionPreference = 'Continue'
    $changed = @(& git -C $ProjectPath diff --name-only "$Base...HEAD" 2>$null) + @(& git -C $ProjectPath diff --name-only HEAD 2>$null)
    $ErrorActionPreference = 'Stop'
}
$changed = @($changed | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)

$tests = 'Assets/MountainPlanner/Tests'
$names = @($changed | Where-Object { $_ -match '^Assets/.*\.cs$' -and $_ -notmatch "^$tests/" } |
           ForEach-Object { ([System.IO.Path]::GetFileName($_) -split '\.')[0] } | Where-Object { $_.Length -ge 4 } | Select-Object -Unique)
$word = $null
if ($names.Count -gt 0) { $word = '\b(' + (($names | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b' }

$fixtures = New-Object System.Collections.Generic.List[string]
foreach ($dir in @('EditMode', 'Core')) {
    $full = Join-Path $ProjectPath ("$tests/$dir" -replace '/', '\')
    if (-not (Test-Path $full)) { continue }
    foreach ($file in Get-ChildItem $full -Recurse -Filter '*.cs') {
        $rel = $file.FullName.Substring($ProjectPath.TrimEnd('\').Length + 1) -replace '\\', '/'
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if ($text -notmatch '\[Test') { continue }   # helpers (Budget, Allocations) hold no tests
        $pick = $changed -contains $rel
        if (-not $pick -and $word) { $pick = $text -match $word }
        if (-not $pick) { continue }
        foreach ($m in [regex]::Matches($text, '\bclass\s+(\w+)')) { $fixtures.Add($m.Groups[1].Value) }
    }
}
if (@($changed | Where-Object { $_ -match '\.(asmdef|asmref)$' }).Count -gt 0) { $fixtures.Add('AssemblyArchitectureTests') }
$fixtures = @($fixtures | Select-Object -Unique | Sort-Object)

if ($fixtures.Count -eq 0) { Write-Host '[editmode] No EditMode or Core fixture names a changed type: nothing to run.'; exit 4 }
Write-Host "[editmode] $($fixtures.Count) fixtures: $($fixtures -join ', ')"
if ($ListOnly) { exit 0 }

if (-not (Test-Path $Unity)) { Write-Host "[editmode] Unity 6000.3.25f1 was not found at $Unity"; exit 3 }
if (Test-EditorOpen $ProjectPath) { Write-Host '[editmode] The project is open in the Unity editor: close it first.'; exit 2 }
$out = Join-Path $ProjectPath 'test-results'
New-Item -ItemType Directory -Force $out | Out-Null
$xml = Join-Path $out 'editmode-affected.xml'
$log = Join-Path $out 'editmode-affected.log'
foreach ($f in @($xml, $log)) { if (Test-Path $f) { Remove-Item $f -Force } }

# Unity's -testFilter is a regex over each test's full name (Namespace.Fixture.Test).
$filter = '\.(' + ($fixtures -join '|') + ')\.'
$started = Get-Date
Write-Host '[editmode] Running them in batch mode (this compiles every assembly too)...'
$code = Invoke-Unity $Unity @('-batchmode', '-projectPath', $ProjectPath, '-runTests', '-testPlatform', 'EditMode', '-testFilter', $filter, '-testResults', $xml, '-logFile', $log)
if ($code -eq $script:LicenceLapsed) { Write-Host '[editmode] Unity exited 198: the licence lapsed. Refresh it in Unity Hub.'; exit 198 }
$t = Read-TestResult $xml $started $code
Write-Host "[editmode] $($t.Message)"
if ($t.Ok) { exit 0 }
if (Test-Path $xml) {
    [xml]$doc = Get-Content -Raw $xml
    foreach ($case in $doc.SelectNodes("//test-case[@result='Failed']")) { Write-Host "  failed: $($case.fullname)" }
    exit 1
}
$compile = Read-CompileResult $log $started $code
foreach ($line in $compile.Errors) { Write-Host "  $line" }
if ($compile.Code -eq 1) { exit 1 }
exit 3
