<#
.SYNOPSIS
  Self-test of the release helpers (task P2-10): results that aren't fresh never pass, licence lapses are named, zip
  names follow the version and a zip's layout is checked. No Unity, no GPU; run it with
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/release/test-release.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-lib.ps1')
$failures = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:failures++ }
}
function Parses([string]$file) {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$errors) | Out-Null
    return ($errors.Count -eq 0)
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("mp-release-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
try {
    Write-Host 'Scripts parse'
    foreach ($f in 'release.ps1', 'compile-check.ps1', 'release-lib.ps1') { Check (Parses (Join-Path $PSScriptRoot $f)) $f }

    Write-Host 'Invoke-Unity waits for its process only'
    # A stand-in for Unity that leaves a child running (as Unity leaves VBCSCompiler) and exits 5.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = Invoke-Unity 'powershell' @('-NoProfile', '-Command', "Start-Process powershell -ArgumentList '-NoProfile -Command Start-Sleep 20' -WindowStyle Hidden; exit 5")
    Check ($code -eq 5) "the exit code comes back ($code)"
    Check ($sw.Elapsed.TotalSeconds -lt 12) "returned in $([int]$sw.Elapsed.TotalSeconds) s, without waiting for the leftover child (20 s)"

    Write-Host 'Versions and names'
    Check (Test-ReleaseVersion '0.2.0') '0.2.0 is a release'
    Check (-not (Test-ReleaseVersion '0.2.0+dev.7.abc1234')) 'a preview is not'
    Check (-not (Test-ReleaseVersion '0.2.0-alpha')) 'plain v0.x.y tags only'
    Check ((Get-ZipName '0.2.0') -eq 'SkiAreaDesignChallenge-0.2.0-win64.zip') 'zip name'

    Write-Host 'Git helpers'
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $threw = $false
    try { $null = Get-ReleaseTag $repo; $null = Get-LfsPointers $repo } catch { $threw = $true }
    Check (-not $threw) 'an untagged commit gives no tag, not an error'

    Write-Host 'Test results'
    $xml = Join-Path $scratch 'editmode.xml'
    Set-Content $xml '<test-run result="Passed" total="12" passed="12" failed="0"></test-run>'
    $before = (Get-Date).AddMinutes(-1)
    Check ((Read-TestResult $xml $before 0).Ok) 'a fresh passing run passes'
    Check (-not (Read-TestResult $xml (Get-Date).AddMinutes(1) 0).Ok) 'a stale file never passes'
    Check ((Read-TestResult $xml $before 198).Message -match 'licence') 'exit 198 names the licence'
    Set-Content $xml '<test-run result="Skipped:Ignored" total="12" passed="10" failed="0" skipped="2"></test-run>'
    Check ((Read-TestResult $xml $before 0).Ok) 'ignored tests with no failures pass'
    Set-Content $xml '<test-run result="Failed(Child)" total="12" passed="11" failed="1"></test-run>'
    Check (-not (Read-TestResult $xml $before 2).Ok) 'a failure fails'
    Set-Content $xml '<test-run result="Passed" total="0" passed="0" failed="0"></test-run>'
    Check (-not (Read-TestResult $xml $before 0).Ok) 'no tests run is not a pass'
    Check (-not (Read-TestResult (Join-Path $scratch 'missing.xml') $before 1).Ok) 'no results file fails'

    Write-Host 'Compile logs'
    $log = Join-Path $scratch 'compile.log'
    Set-Content $log "Assets\Foo.cs(3,1): error CS0103: The name 'x' does not exist`nAssets\Foo.cs(3,1): error CS0103: The name 'x' does not exist"
    $r = Read-CompileResult $log $before 1
    Check ($r.Code -eq 1 -and $r.Errors.Count -eq 1) 'errors are listed once'
    Set-Content $log 'Exiting batchmode successfully now!'
    Check ((Read-CompileResult $log $before 0).Code -eq 0) 'a clean log passes'
    Check ((Read-CompileResult $log (Get-Date).AddMinutes(1) 0).Code -eq 3) 'a stale log never passes'
    Check ((Read-CompileResult $log $before 198).Code -eq 198) 'exit 198 is the licence'

    Write-Host 'Zip layout'
    $top = Join-Path $scratch 'stage\SkiAreaDesignChallenge-0.2.0'
    $demo = Join-Path $top 'SkiAreaDesignChallenge_Data\StreamingAssets\Demo\abc'
    New-Item -ItemType Directory -Force (Join-Path $demo 'cache-v12') | Out-Null
    foreach ($f in 'SkiAreaDesignChallenge.exe', 'UnityPlayer.dll', 'README.txt', 'NOTICES.txt') { Set-Content (Join-Path $top $f) 'x' }
    Set-Content (Join-Path $demo 'manifest.json') '{}'
    Set-Content (Join-Path $demo 'cache-v12\cache.json') '{}'
    $zip = Join-Path $scratch 'good.zip'
    & $script:Tar -a -c -f $zip -C (Join-Path $scratch 'stage') 'SkiAreaDesignChallenge-0.2.0'
    Check ((Test-ZipLayout $zip '0.2.0').Count -eq 0) 'a sound zip passes'
    Check ((Test-ZipLayout $zip '0.2.1').Count -gt 0) 'the top folder must match the version'
    New-Item -ItemType Directory -Force (Join-Path $top 'Ski Area Design Challenge_BurstDebugInformation_DoNotShip') | Out-Null
    Set-Content (Join-Path $top 'Ski Area Design Challenge_BurstDebugInformation_DoNotShip\x.txt') 'x'
    Remove-Item (Join-Path $demo 'cache-v12') -Recurse
    $bad = Join-Path $scratch 'bad.zip'
    & $script:Tar -a -c -f $bad -C (Join-Path $scratch 'stage') 'SkiAreaDesignChallenge-0.2.0'
    $problems = Test-ZipLayout $bad '0.2.0'
    Check (($problems -join ';') -match 'DoNotShip') 'DoNotShip is refused'
    Check (($problems -join ';') -match 'cache') 'a demo without its cache is refused'

    Write-Host 'Symbols (task P3-14)'
    $player = Join-Path $scratch 'Player'
    $bee = Join-Path $scratch 'proj\Library\Bee\PlayerScriptAssemblies'
    New-Item -ItemType Directory -Force (Join-Path $player 'SkiAreaDesignChallenge_Data\Managed'), (Join-Path $player 'Ski Area Design Challenge_BurstDebugInformation_DoNotShip'), $bee | Out-Null
    Set-Content (Join-Path $player 'SkiAreaDesignChallenge.exe') 'exe'
    Set-Content (Join-Path $player 'Ski Area Design Challenge_BurstDebugInformation_DoNotShip\lib_burst_generated.pdb') 'burst'
    foreach ($n in 'MountainPlanner.App', 'MountainPlanner.UI') {
        Set-Content (Join-Path $player "SkiAreaDesignChallenge_Data\Managed\$n.dll") $n
        Set-Content (Join-Path $bee "$n.pdb") "pdb $n"
    }
    Set-Content (Join-Path $bee 'MountainPlanner.App.dll') 'MountainPlanner.App'
    Set-Content (Join-Path $bee 'MountainPlanner.UI.dll') 'a different build'
    $kept = Save-Symbols $player (Join-Path $scratch 'proj') (Join-Path $scratch 'Unity\Editor\Unity.exe') '0.3.0' (Join-Path $scratch 'symbols')
    $sym = Join-Path $scratch 'symbols\0.3.0'
    Check (Test-Path (Join-Path $sym 'native\SkiAreaDesignChallenge.exe')) 'the shipped exe is kept'
    Check (Test-Path (Join-Path $sym 'burst\lib_burst_generated.pdb')) 'the Burst debug information is kept'
    Check (Test-Path (Join-Path $sym 'managed\MountainPlanner.App.pdb')) 'a PDB that matches its shipped DLL is kept'
    Check (-not (Test-Path (Join-Path $sym 'managed\MountainPlanner.UI.pdb'))) "a PDB from another build isn't"
    Check ($kept.ManagedPdbs -eq 1 -and $kept.ManagedDlls -eq 2) 'the PDBs are counted'
    Check ((Get-Content -Raw (Join-Path $sym 'symbols.json')) -match '"version":\s*"0.3.0"') 'symbols.json names the version'
} finally {
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}
if ($failures -gt 0) { Write-Host "$failures failed" -ForegroundColor Red; exit 1 }
Write-Host 'All release helper checks passed.'
exit 0
