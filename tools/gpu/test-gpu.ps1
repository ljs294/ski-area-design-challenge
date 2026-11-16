<#
  Tests for gpu.ps1 that need no GPU: dummy PowerShell children log their start and end times, and the
  test checks the intervals. Uses a scratch state folder, so the live state and the old .gpu-lock are
  never touched. About three minutes.
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
    # No real player, editor or game (another thread's run, the owner's play) may block these runs: the GPU-heavy list
    # names a process that never runs unless a check passes its own -HeavyProcesses (a later option wins).
    $list = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Gpu, $action, '-StateDir', $StateDir,
        '-PollSec', '0.3', '-LegacyProcesses', $NoProcess, '-HeavyProcesses', $NoProcess, '-WatchSec', '0.3') + $extra
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

    Write-Host 'The command is told its lane (MP_GPU_LANE)'
    $r = Invoke-Gpu 'run' @('-Lane', 'render', '-Name', 'lane-r', '--', 'powershell', '-NoProfile', '-Command', 'if ($env:MP_GPU_LANE -eq ''render'') { exit 0 } else { exit 3 }')
    Check ($r.code -eq 0) "a render run sees MP_GPU_LANE=render (exit $($r.code))"
    $r = Invoke-Gpu 'run' @('-Lane', 'measure', '-Name', 'lane-m', '--', 'powershell', '-NoProfile', '-Command', 'if ($env:MP_GPU_LANE -eq ''measure'') { exit 0 } else { exit 3 }')
    Check ($r.code -eq 0) "a measure run sees MP_GPU_LANE=measure (exit $($r.code))"

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

    Write-Host 'A -Priority ticket goes to the front of the waiting queue (task 4.01)'
    $p = @()
    $p += Start-Run 'measure' 'p-0' 4000; [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'p-0' }).Count -gt 0 } 'p-0 holds')
    $p += Start-Run 'render' 'p-a' 300; Wait-Entry 'p-a'
    $p += Start-Run 'measure' 'p-b' 300; Wait-Entry 'p-b'
    $p += Start-Run 'render' 'p-rel' 300 @('-Priority'); Wait-Entry 'p-rel'
    Wait-All $p
    $zero = Get-Span 'p-0'; $rel = Get-Span 'p-rel'
    $later = @(@('p-a', 'p-b') | ForEach-Object { Get-Span $_ } | Where-Object { $_ })
    Check ($zero -and $rel -and $later.Count -eq 2) 'all four ran'
    if ($zero -and $rel -and $later.Count -eq 2) {
        Check ($rel.start -ge $zero.end) 'the priority ticket never pre-empts the run holding the GPU'
        Check (@($later | Where-Object { $_.start -lt $rel.start }).Count -eq 0) 'the priority ticket started before the two queued ahead of it'
    }

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

    Write-Host 'Ctrl+C stops the command and frees the slot'
    # Its own (hidden) console, as a run in a terminal window has; a helper attaches to that console and sends the
    # keyboard's Ctrl+C to everything on it (task P3-01; the hand check #76 left).
    # Windows hands "Ctrl+C ignored" down to child processes, and agent shells start with it set (a terminal doesn't):
    # the run is started through a wrapper that clears it first, as a terminal window would have it.
    $console32 = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Console32 {
  [DllImport("kernel32.dll")] public static extern bool FreeConsole();
  [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint pid);
  [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr h, bool add);
  [DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
}
"@

'@
    $terminal = Join-Path $Root 'as-terminal.ps1'
    Set-Content -Path $terminal -Encoding UTF8 -Value ($console32 + @'
[Console32]::SetConsoleCtrlHandler([IntPtr]::Zero, $false) | Out-Null
$rest = @($args | Select-Object -Skip 1)
& $args[0] @rest
exit $LASTEXITCODE
'@)
    $a = (Get-GpuArgs 'run' @('-Lane', 'measure', '-Name', 'ctrl-c', '--', 'ping.exe', '-n', '120', '127.0.0.1')) -replace '-File ', ('-File ' + (Quote $terminal) + ' ')
    $cc = Start-Process -FilePath 'powershell' -ArgumentList $a -PassThru -WindowStyle Hidden
    $script:Launched += $cc
    [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'ctrl-c' -and $_.childPid }).Count -gt 0 } 'ctrl-c child started')
    $ccChild = [int]@((Get-State).holders | Where-Object { $_.name -eq 'ctrl-c' })[0].childPid
    $sender = Join-Path $Root 'send-ctrl-c.ps1'
    Set-Content -Path $sender -Encoding UTF8 -Value ($console32 + @'
[Console32]::FreeConsole() | Out-Null
if (-not [Console32]::AttachConsole([uint32]$args[0])) { exit 3 }
[Console32]::SetConsoleCtrlHandler([IntPtr]::Zero, $true) | Out-Null   # the helper itself ignores it
if (-not [Console32]::GenerateConsoleCtrlEvent(0, 0)) { exit 4 }
Start-Sleep -Milliseconds 500
exit 0
'@)
    $send = Start-Process powershell -PassThru -Wait -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $sender, $cc.Id)
    Check ($send.ExitCode -eq 0) "Ctrl+C sent to the run's console (helper exit $($send.ExitCode))"
    $ccDone = $cc.WaitForExit(10000)
    Check $ccDone 'the run ends on Ctrl+C'
    Check (-not (Get-Process -Id $ccChild -ErrorAction SilentlyContinue)) 'its command stopped too'
    Check (@((Get-State).holders | Where-Object { $_.name -eq 'ctrl-c' }).Count -eq 0) 'and its slot is free'
    # A run that missed its Ctrl+C would hold the measure slot and fail every check after it.
    if (-not $ccDone) { & taskkill.exe /T /F /PID $cc.Id 2>$null | Out-Null; [void]$cc.WaitForExit(5000) }

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
    Remove-Item -Recurse -Force $Legacy

    # ---- Task 0.5: history, time limit, GPU-heavy processes outside the queue ----

    Write-Host 'Every ticket leaves one history line (task 0.5)'
    $hist = @(((Invoke-Gpu 'history' @('-Json')).out | ConvertFrom-Json).records)
    $byName = @{}
    foreach ($h in $hist) { if (-not $byName.ContainsKey($h.name)) { $byName[$h.name] = @() }; $byName[$h.name] += $h }
    $one = { param($n) if ($byName.ContainsKey($n)) { $byName[$n][0] } else { $null } }
    Check ((& $one 'exit') -and (& $one 'exit').outcome -eq 'failed' -and (& $one 'exit').exit -eq 7) 'a failed run: outcome failed, exit 7'
    Check ((& $one 'exit0') -and (& $one 'exit0').outcome -eq 'ok' -and (& $one 'exit0').started) 'a good run: outcome ok, with its start time'
    Check ((& $one 'impatient') -and (& $one 'impatient').outcome -eq 'gave-up-waiting' -and -not (& $one 'impatient').started) 'a run that gave up waiting: never started'
    Check ((& $one 'kill-1') -and (& $one 'kill-1').outcome -eq 'stale') 'a killed run: written as stale when cleaned up'
    Check ((& $one 'ctrl-c') -and (& $one 'ctrl-c').outcome -eq 'interrupted') "Ctrl+C: outcome interrupted ($(if (& $one 'ctrl-c') { (& $one 'ctrl-c').outcome } else { 'no line' }))"
    $dupes = @($hist | Group-Object ticket | Where-Object { $_.Count -gt 1 })
    Check ($dupes.Count -eq 0) "one line per ticket ($($dupes.Count) tickets with more)"
    Check ($hist.Count -ge 20) "the runs so far are all there ($($hist.Count) lines)"
    $r = Invoke-Gpu 'history'
    Check ($r.code -eq 0 -and $r.out -match 'Overlaps: none') 'the real runs so far show no overlaps'
    $r = Invoke-Gpu 'history' @('-Name', 'exit0', '-Since', '1h')
    Check ($r.out -match 'exit0' -and $r.out -notmatch ' impatient ') 'history -Name -Since filters'
    $r = Invoke-Gpu 'history' @('-Since', 'yesterday-ish')
    Check ($r.code -eq 64) 'a bad -Since is a usage error'

    Write-Host 'history finds overlaps in a written history'
    $odir = Join-Path $Root 'overlap'
    New-Item -ItemType Directory -Force $odir | Out-Null
    $line = { param($t, $lane, $s, $e, $unq) ([pscustomobject]@{ ticket = $t; lane = $lane; name = "n$t"; command = 'x'; pid = 1; priority = $false; limitMin = 60
                queued = "2026-11-01T10:00:00Z"; started = "2026-11-01T$($s)Z"; ended = "2026-11-01T$($e)Z"; exit = 0; outcome = 'ok'; unqueued = @($unq) } | ConvertTo-Json -Compress) }
    Set-Content -Path (Join-Path $odir 'history.jsonl') -Encoding Ascii -Value @(
        (& $line 1 'measure' '10:00:00' '10:10:00' @()),
        (& $line 2 'render' '10:05:00' '10:06:00' @()),
        (& $line 3 'render' '11:00:00' '11:10:00' @()),
        (& $line 4 'render' '11:01:00' '11:09:00' @()),
        (& $line 5 'render' '11:02:00' '11:03:00' @()),
        (& $line 6 'measure' '12:00:00' '12:10:00' @('SkiAreaDesignChallenge (pid 9)')))
    $a = (Get-GpuArgs 'history' @('-Json')) -replace [regex]::Escape((Quote $StateDir)), (Quote $odir)
    $p = Start-Process powershell -ArgumentList $a -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Logs 'ov.out')
    $ov = @((Get-Content -Raw (Join-Path $Logs 'ov.out') | ConvertFrom-Json).overlaps)
    Check (@($ov | Where-Object { $_.kind -eq 'measure-overlap' -and ($_.tickets -join ',') -eq '1,2' }).Count -eq 1) 'a render run inside a measure run is flagged'
    Check (@($ov | Where-Object { $_.kind -eq 'render-over-slots' }).Count -eq 1) 'three render runs at once (2 slots) are flagged'
    Check (@($ov | Where-Object { $_.kind -eq 'contaminated' -and $_.tickets[0] -eq 6 }).Count -eq 1) 'a measure run that saw a player outside the queue is flagged'
    Check ($ov.Count -eq 3) "nothing else is flagged ($($ov.Count))"

    Write-Host 'The time limit kills the run and its children (exit 124)'
    $limitExe = Join-Path $Root 'gpulimittest.exe'
    Copy-Item (Join-Path $env:SystemRoot 'System32\PING.EXE') $limitExe
    $t0 = [DateTime]::UtcNow
    $r = Invoke-Gpu 'run' @('-Lane', 'render', '-Name', 'limit', '-LimitMin', '0.05', '--', 'cmd', '/c', $limitExe, '-n', '60', '127.0.0.1')
    $took = ([DateTime]::UtcNow - $t0).TotalSeconds
    Check ($r.code -eq 124) "a run past -LimitMin 0.05 exits 124 ($($r.code))"
    Check ($took -lt 30) "it was stopped after the limit ($([int]$took) s)"
    Start-Sleep -Milliseconds 300
    Check (@(Get-Process -Name 'gpulimittest' -ErrorAction SilentlyContinue).Count -eq 0) 'its grandchild is gone too'
    $h = @(((Invoke-Gpu 'history' @('-Json', '-Name', 'limit')).out | ConvertFrom-Json).records)
    Check ($h.Count -eq 1 -and $h[0].outcome -eq 'time-limit') 'history says time-limit'
    $r = Invoke-Gpu 'run' @('-Lane', 'measure', '-Name', 'nolimit', '-LimitMin', '0', '--', 'cmd', '/c', 'exit 0')
    Check ($r.code -eq 0) '-LimitMin 0 runs without a limit'

    Write-Host 'A measure run waits for a GPU-heavy process outside the queue; render runs do not'
    $heavyExe = Join-Path $Root 'gpuheavytest.exe'
    Copy-Item (Join-Path $env:SystemRoot 'System32\PING.EXE') $heavyExe
    $heavy = @('-HeavyProcesses', 'gpuheavytest,gpuheadless')
    $play = Start-Process $heavyExe -ArgumentList '-n 30 127.0.0.1' -PassThru -WindowStyle Hidden
    $script:Launched += $play
    $r = Invoke-Gpu 'run' (@('-Lane', 'measure', '-Name', 'u-wait', '-TimeoutSec', '2') + $heavy + @('--', 'cmd', '/c', 'exit 0'))
    Check ($r.code -eq 124) "a measure run waits while one runs ($($r.code))"
    Check ((Get-Content -Raw (Join-Path $Logs 'last.err')) -match 'outside the queue: gpuheavytest') 'and says what it waits for'
    $r = Invoke-Gpu 'status' $heavy
    Check ($r.out -match 'Outside the queue: gpuheavytest') 'status lists it'
    $r = Invoke-Gpu 'run' (@('-Lane', 'render', '-Name', 'u-render', '-TimeoutSec', '5') + $heavy + @('--', 'cmd', '/c', 'exit 0'))
    Check ($r.code -eq 0) "a render run doesn't wait ($($r.code))"

    Write-Host 'Render runs pass a measure run that waits only for play outside the queue'
    $p = @()
    $p += Start-Run 'measure' 'u-m' 300 $heavy; Wait-Entry 'u-m'
    $p += Start-Run 'render' 'u-r' 300 $heavy
    [void]$p[1].WaitForExit(20000)
    Check ((Get-Span 'u-r') -and -not (Get-Span 'u-m')) 'the render run finished while the measure run still waits'
    & taskkill.exe /F /PID $play.Id 2>$null | Out-Null
    [void]$p[0].WaitForExit(20000)
    Check ([bool](Get-Span 'u-m')) 'the measure run starts once the process quits'

    Write-Host 'Headless runs and a queued run''s own children never count'
    $headless = Start-Process (Copy-Item (Join-Path $env:SystemRoot 'System32\cmd.exe') (Join-Path $Root 'gpuheadless.exe') -PassThru).FullName `
        -ArgumentList '/c ping -n 30 127.0.0.1 & rem -nographics' -PassThru -WindowStyle Hidden
    $script:Launched += $headless
    $r = Invoke-Gpu 'run' (@('-Lane', 'measure', '-Name', 'u-headless', '-TimeoutSec', '5') + $heavy + @('--', 'cmd', '/c', 'exit 0'))
    Check ($r.code -eq 0) "a -nographics process doesn't block a measure run ($($r.code))"
    & taskkill.exe /F /PID $headless.Id 2>$null | Out-Null
    $r = Invoke-Gpu 'run' (@('-Lane', 'measure', '-Name', 'u-own') + $heavy + @('--', 'cmd', '/c', $heavyExe, '-n', '3', '127.0.0.1'))
    $h = @(((Invoke-Gpu 'history' @('-Json', '-Name', 'u-own')).out | ConvertFrom-Json).records)
    Check ($r.code -eq 0 -and $h.Count -eq 1 -and @($h[0].unqueued).Count -eq 0) 'a measure run''s own player is not outside the queue'

    Write-Host 'A player that starts during a measure run marks it contaminated'
    $c = Start-Run 'measure' 'u-contam' 4000 $heavy
    [void](Wait-For { param($s) @($s.holders | Where-Object { $_.name -eq 'u-contam' -and $_.childPid }).Count -gt 0 } 'u-contam holds')
    $late = Start-Process $heavyExe -ArgumentList '-n 3 127.0.0.1' -PassThru -WindowStyle Hidden
    $script:Launched += $late
    Wait-All @($c)
    $h = @(((Invoke-Gpu 'history' @('-Json', '-Name', 'u-contam')).out | ConvertFrom-Json).records)
    Check ($h.Count -eq 1 -and @($h[0].unqueued | Where-Object { $_ -like 'gpuheavytest*' }).Count -ge 1) 'its history line names the player'
    Check ($h.Count -eq 1 -and $h[0].exit -eq 0) 'the run itself still finished normally'
    $r = Invoke-Gpu 'history' @('-Name', 'u-contam')
    Check ($r.out -match 'CONTAMINATED' -and $r.out -match 'contaminated: measure #') 'history shows it as contaminated'
} finally {
    foreach ($p in $script:Launched) { if (-not $p.HasExited) { try { & taskkill.exe /T /F /PID $p.Id 2>$null | Out-Null } catch { } } }   # it may exit meanwhile
    Start-Sleep -Milliseconds 300
    Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
}

if ($script:Failures -gt 0) { Write-Host "$($script:Failures) check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'All gpu.ps1 checks passed'
exit 0
