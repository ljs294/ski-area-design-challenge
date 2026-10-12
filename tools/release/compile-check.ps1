<#
.SYNOPSIS
  Compiles every Unity assembly (App, UI, Presentation, World, Editor and the tests) in batch mode and fails on any C#
  error (task P2-10). CI's dotnet build only covers the engine-free assemblies, so an error elsewhere passes CI; this
  catches it locally. Run by tools/release/release.ps1 and the opt-in pre-push hook (tools/git-hooks/pre-push).

.DESCRIPTION
  Exit codes: 0 compiled; 1 compile errors (listed); 2 the project is open in the editor (close it first);
  3 Unity not found or the log never appeared; 198 the Unity licence lapsed (refresh it in Unity Hub).
  The log must be written by this run: a stale log never counts as a pass.
#>
param(
    [string]$ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe',
    [string]$LogFile = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-lib.ps1')

if (-not (Test-Path $Unity)) { Write-Host "[compile] Unity 6000.3.25f1 was not found at $Unity"; exit 3 }
if (Test-EditorOpen $ProjectPath) { Write-Host "[compile] The project is open in the Unity editor: close it first."; exit 2 }
if (-not $LogFile) { $LogFile = Join-Path $ProjectPath 'test-results\compile-check.log' }
New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
if (Test-Path $LogFile) { Remove-Item $LogFile -Force }

$started = Get-Date
Write-Host "[compile] Compiling every assembly in batch mode (a minute or two; much longer on a fresh checkout)..."
$code = Invoke-Unity $Unity @('-batchmode', '-nographics', '-quit', '-projectPath', $ProjectPath, '-logFile', $LogFile)
$result = Read-CompileResult $LogFile $started $code
Write-Host "[compile] $($result.Message)"
foreach ($line in $result.Errors) { Write-Host "  $line" }
exit $result.Code
