<#
  Tests for gpu.ps1 that need no GPU: dummy PowerShell children log their start and end times, and the
  test checks the intervals. Uses a scratch state folder, so the live state and the old .gpu-lock are
  never touched. About two minutes.
  Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/test-gpu.ps1
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Gpu = Join-Path $PSScriptRoot 'gpu.ps1'
$Root = Join-Path ([System.IO.Path]::GetTempPath()) ("gpu-lanes-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$StateDir = Join-Path $Root '.gpu'
$Legacy = Join-Path $Root '.gpu-lock'
$Logs = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $Logs | Out-Null
$script:Failures = 0
$script:Launched = @()
$NoProcess = 'no-such-process-gpu-test'

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:Failures++ }
}

function Quote([string]$s) { if ($s -match '[\s"]') { return '"' + ($s -replace '"', '\"') + '"' } return $s }

function Get-GpuArgs([string]$action, [string[]]$extra) {
    $list = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Gpu, $action, '-StateDir', $StateDir,
        '-PollSec', '0.3', '-LegacyProcesses', $NoProcess) + $extra
    return (($list | ForEach-Object { Quote $_ }) -join ' ')
}

# Starts "gpu.ps1 run" in the background; the child sleeps $ms and logs start/end ticks to logs\<name>.txt.
function Start-Run([string]$lane, [string]$name, [int]$ms, [string[]]$extra = @()) {
    $log = Join-Path $Logs "$name.txt"
    $child = "Add-Content -LiteralPath '$log' -Value ('start ' + [DateTime]::UtcNow.Ticks); Start-Sleep -Milliseconds $ms; Add-Content -LiteralPath '$log' -Value ('end ' + [DateTime]::UtcNow.Ticks)"
    $a = Get-GpuArgs 'run' (@('-Lane', $lane, '-Name', $name) + $extra + @('--', 'powershell', '-NoProfile', '-Command', $child))
    $p = Start-Process -FilePath 'powershell' -ArgumentList $a -PassThru -WindowStyle Hidden `
        -RedirectStandardError (Join-Path $Logs "$name.err") -RedirectStandardOutput (Join-Path $Logs "$name.out")
    $script:Launched += $p
    return $p
}

function Invoke-Gpu([string]$action, [string[]]$extra = @()) {
    $a = Get-GpuArgs $action $extra
    $p = Start-Process -FilePath 'powershell' -ArgumentList $a -PassThru -Wait -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $Logs 'last.out') -RedirectStandardError (Join-Path $Logs 'last.err')
    return [pscustomobject]@{ code = $p.ExitCode; out = (Get-Content -Raw (Join-Path $Logs 'last.out')) }
}

function Get-State {
    for ($t = 0; $t -lt 20; $t++) {
        try {
            $f = Join-Path $StateDir 'state.json'
            if (-not (Test-Path $f)) { return [pscustomobject]@{ holders = @(); queue = @() } }
            $s = [System.IO.File]::ReadAllText($f) | ConvertFrom-Json
            return [pscustomobject]@{ holders = @($s.holders | Where-Object { $_ }); queue = @($s.queue | Where-Object { $_ }) }
        } catch { Start-Sleep -Milliseconds 50 }
    }
    throw 'state.json stayed unreadable'
}

function Wait-For([scriptblock]$cond, [string]$what, [int]$sec = 20) {
    $end = [DateTime]::UtcNow.AddSeconds($sec)
    while ([DateTime]::UtcNow -lt $end) {
        if (& $cond (Get-State)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    Write-Host "  timed out waiting for: $what" -ForegroundColor Yellow
    return $false
}

function Wait-Entry([string]$name) { [void](Wait-For { param($s) @(@($s.holders) + @($s.queue) | Where-Object { $_.name -eq $name }).Count -gt 0 } "entry $name") }

function Wait-All([object[]]$procs, [int]$sec = 60) { foreach ($p in $procs) { [void]$p.WaitForExit($sec * 1000) } }

function Get-Span([string]$name) {
    $f = Join-Path $Logs "$name.txt"
    if (-not (Test-Path $f)) { return $null }
    $lines = @(Get-Content $f)
    $s = ($lines | Where-Object { $_ -like 'start *' } | Select-Object -First 1)
    $e = ($lines | Where-Object { $_ -like 'end *' } | Select-Object -First 1)
    if (-not $s -or -not $e) { return $null }
    return [pscustomobject]@{ name = $name; start = [long]($s -split ' ')[1]; end = [long]($e -split ' ')[1] }
}

function Test-Overlap($a, $b) { return ($a.start -lt $b.end) -and ($b.start -lt $a.end) }

function Get-MaxConcurrency($spans) {
    $events = foreach ($s in $spans) { [pscustomobject]@{ t = $s.start; d = 1 }; [pscustomobject]@{ t = $s.end; d = -1 } }
    $n = 0; $max = 0
    foreach ($e in ($events | Sort-Object t, d)) { $n += $e.d; if ($n -gt $max) { $max = $n } }
    return $max
}

try {
    Write-Host "gpu.ps1 tests in $Root"

    Write-Host 'Exit code passes through'
    $r = Invoke-Gpu 'run' @('-Lane', 'render', '-Name', 'exit', '--', 'powershell', '-NoProfile', '-Command', 'exit 7')
    Check ($r.code -eq 7) "exit code 7 came back as $($r.code)"
    $r = Invoke-Gpu 'run' @('-Lane', 'measure', '-Name', 'exit0', '--', 'cmd', '/c', 'exit 0')
    Check ($r.code -eq 0) "exit code 0 came back as $($r.code)"
    $r = Invoke-Gpu 'run' @('-Lane', 'bogus', '-Name', 'x', '--', 'cmd', '/c', 'exit 0')
    Check ($r.code -eq 64) 'a bad lane is a usage error (64)'

    Write-Host 'Render allows 2 at once and a third waits'
    $p = @()
    foreach ($n in 'r1', 'r2', 'r3') { $p += Start-Run 'render' $n 3000; Wait-Entry $n }
    Wait-All $p
    $sp = @('r1', 'r2', 'r3' | ForEach-Object { Get-Span $_ })
    Check (@($sp | Where-Object { $_ }).Count -eq 3) 'all three ran'
    if (@($sp | Where-Object { $_ }).Count -eq 3) {
        Check (Test-Overlap $sp[0] $sp[1]) 'r1 and r2 ran together'
        Check ((Get-MaxConcurrency $sp) -eq 2) 'never more than 2 at once'
        Check ($sp[2].start -ge [Math]::Min($sp[0].end, $sp[1].end)) 'r3 started only after a slot freed'
    }

    Write-Host 'Measure excludes everything, and a waiting measure blocks later renders'
    $p = @()
    $p += Start-Run 'render' 'm-r1' 3000; [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'm-r1' }).Count -gt 0 } 'm-r1 holds')
    $p += Start-Run 'measure' 'm-m' 1500; Wait-Entry 'm-m'
    $p += Start-Run 'render' 'm-r2' 1000; Wait-Entry 'm-r2'
    Wait-All $p
    $r1 = Get-Span 'm-r1'; $m = Get-Span 'm-m'; $r2 = Get-Span 'm-r2'
    Check ($r1 -and $m -and $r2) 'all three ran'
    if ($r1 -and $m -and $r2) {
        Check ($m.start -ge $r1.end) 'measure waited for the render run to finish'
        Check ($r2.start -ge $m.end) 'a render queued behind the measure run waited for it, though a slot was free'
    }

    Write-Host 'Tickets are served in order'
    $p = @()
    $p += Start-Run 'measure' 'o-0' 6000; [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'o-0' }).Count -gt 0 } 'o-0 holds')
    $order = @('o-a', 'o-b', 'o-c', 'o-d')
    $lanes = @('measure', 'render', 'measure', 'render')
    for ($k = 0; $k -lt 4; $k++) { $p += Start-Run $lanes[$k] $order[$k] 300; Wait-Entry $order[$k] }
    $st = Get-State
    $tickets = @(@($st.holders) + @($st.queue) | Where-Object { $_.name -ne 'o-0' } | Sort-Object { [long]$_.ticket } | ForEach-Object { $_.name })
    Check (($tickets -join ',') -eq ($order -join ',')) "tickets issued in arrival order ($($tickets -join ','))"
    Wait-All $p
    $spans = @($order | ForEach-Object { Get-Span $_ } | Where-Object { $_ })
    $started = @($spans | Sort-Object start | ForEach-Object { $_.name })
    Check (($started -join ',') -eq ($order -join ',')) "started in ticket order ($($started -join ','))"

    Write-Host 'A killed holder is cleaned up'
    $k1 = Start-Run 'render' 'kill-1' 60000
    [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'kill-1' -and $_.childPid }).Count -gt 0 } 'kill-1 child started')
    $h = @((Get-State).holders | Where-Object { $_.name -eq 'kill-1' })[0]
    Stop-Process -Id $k1.Id -Force
    Start-Sleep -Milliseconds 500
    $st = (Invoke-Gpu 'status' @('-Json')).out | ConvertFrom-Json
    $row = @($st.holders | Where-Object { $_.name -eq 'kill-1' })
    Check ($row.Count -eq 1 -and -not $row[0].stale) 'with only the script killed, the still-running child keeps the slot'
    Stop-Process -Id ([int]$h.childPid) -Force
    Start-Sleep -Milliseconds 300
    $st = (Invoke-Gpu 'status' @('-Json')).out | ConvertFrom-Json
    $row = @($st.holders | Where-Object { $_.name -eq 'kill-1' })
    Check ($row.Count -eq 1 -and $row[0].stale) 'status shows the dead holder as stale'
    $r = Invoke-Gpu 'clear-stale'
    Check ($r.out -match 'Removed stale #\d+ render kill-1') 'clear-stale removes it'
    Check (@((Get-State).holders).Count -eq 0) 'no holders left'

    $k2 = Start-Run 'measure' 'kill-2' 60000
    [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'kill-2' -and $_.childPid }).Count -gt 0 } 'kill-2 child started')
    & taskkill.exe /T /F /PID $k2.Id 2>$null | Out-Null
    $t0 = [DateTime]::UtcNow
    $r = Invoke-Gpu 'run' @('-Lane', 'measure', '-Name', 'after-kill', '-TimeoutSec', '15', '--', 'cmd', '/c', 'exit 0')
    Check ($r.code -eq 0) "a measure run starts after a killed measure holder (exit $($r.code), $([int]([DateTime]::UtcNow - $t0).TotalSeconds) s)"

    Write-Host 'Release by name'
    $rel = Start-Run 'render' 'rel' 60000
    [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'rel' }).Count -gt 0 } 'rel holds')
    $r = Invoke-Gpu 'release' @('-Name', 'rel')
    Check ($r.out -match "Released #\d+ render rel") 'release reports the slot'
    Check (@((Get-State).holders | Where-Object { $_.name -eq 'rel' }).Count -eq 0) 'the slot is gone'
    & taskkill.exe /T /F /PID $rel.Id 2>$null | Out-Null

    Write-Host 'Timeout while waiting'
    $hold = Start-Run 'measure' 'hold' 6000
    [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'hold' }).Count -gt 0 } 'hold holds')
    $r = Invoke-Gpu 'run' @('-Lane', 'render', '-Name', 'impatient', '-TimeoutSec', '2', '--', 'cmd', '/c', 'exit 0')
    Check ($r.code -eq 124) "a render run gives up with 124 while a measure run holds ($($r.code))"
    Check (@((Get-State).queue | Where-Object { $_.name -eq 'impatient' }).Count -eq 0) 'the timed-out ticket left the queue'
    Wait-All @($hold)

    Write-Host 'Old .gpu-lock folder counts as a measure hold'
    New-Item -ItemType Directory -Path $Legacy | Out-Null
    Set-Content -Path (Join-Path $Legacy 'owner.txt') -Value "old-thread $(Get-Date -Format o)"
    $r = Invoke-Gpu 'run' @('-Lane', 'render', '-Name', 'legacy-1', '-TimeoutSec', '2', '--', 'cmd', '/c', 'exit 0')
    Check ($r.code -eq 124) 'a recent owner.txt blocks runs'
    $st = Invoke-Gpu 'status'
    Check ($st.out -match 'Old lock') 'status reports the old lock'
    (Get-Item (Join-Path $Legacy 'owner.txt')).LastWriteTime = (Get-Date).AddHours(-3)
    $a = Get-GpuArgs 'run' @('-Lane', 'render', '-Name', 'legacy-2', '-TimeoutSec', '2', '-LegacyProcesses', 'powershell', '--', 'cmd', '/c', 'exit 0')
    $p = Start-Process powershell -ArgumentList $a -PassThru -Wait -WindowStyle Hidden
    Check ($p.ExitCode -eq 124) 'an old owner.txt still blocks while a listed process is running'
    $r = Invoke-Gpu 'run' @('-Lane', 'render', '-Name', 'legacy-3', '-TimeoutSec', '5', '--', 'cmd', '/c', 'exit 0')
    Check ($r.code -eq 0) 'an old owner.txt with none of the processes running is ignored'
    Check (Test-Path $Legacy) 'the old lock folder is left alone'
} finally {
    foreach ($p in $script:Launched) { if (-not $p.HasExited) { & taskkill.exe /T /F /PID $p.Id 2>$null | Out-Null } }
    Start-Sleep -Milliseconds 300
    Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
}

if ($script:Failures -gt 0) { Write-Host "$($script:Failures) check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'All gpu.ps1 checks passed'
exit 0
