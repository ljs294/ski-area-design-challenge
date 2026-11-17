<#
.SYNOPSIS
  GPU lane scheduler: runs a command once the shared GPU has a free slot in its lane.

.DESCRIPTION
  Lanes:
    measure  exclusive; nothing else may hold a slot (benchmarks, timing, review movies).
    render   up to renderSlots at once (default 2), none while a measure run holds its slot
             (-screenshot / -uicapture players, PlayMode on site caches, Cycles renders, Lift Lab captures).
  Waiting runs take numbered tickets and start strictly in ticket order, so a waiting measure run
  blocks render runs queued after it. A -Priority ticket (the release smoke test) goes to the front of
  the waiting queue, never ahead of a run that already holds its slot. A holder or waiter whose process (PID plus start time) is
  gone is stale and is removed on the next poll. The old D:\skiAreaDesignChallenge\.gpu-lock folder
  counts as a measure hold while its owner.txt is under 2 hours old or a Unity, player or Blender
  process is running.
  Task 0.5: every ticket appends one line to <state>\history.jsonl when it ends (read it with "history");
  a run is killed (child tree, exit 124) after its lane's time limit (measure 60 min, render 120 min,
  -LimitMin to override, 0 = none); a measure run waits while a GPU-heavy process (a player, the Unity
  editor, the owner's games) runs outside the queue, and one that appears mid-run is recorded as contamination.
  While such a process runs, waiting measure tickets don't hold up render tickets behind them.

  Usage:
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 run -Lane measure|render -Name "<thread>" [-Priority] [-LimitMin N] -- <exe> <args...>
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 status [-Json]
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 history [-Since 24h|7d|<ISO time>] [-Name "<thread>"] [-Json]
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 clear-stale
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 release -Name "<thread>"

  Exit codes for run: the command's own exit code; 124 when -TimeoutSec ran out while waiting or the run hit
  its time limit; 125 for a failed -RequireDesktop check; 126 when the command could not start; 64 for usage errors.
#>
# No param block: Windows PowerShell 5.1 can't bind "--" with -File, so the arguments are parsed by hand.
# Everything after "--" is the command, passed through untouched.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Action = 'status'
$Lane = ''
$Name = ''
$StateDir = ''               # MP_GPU_STATE overrides the default; tests pass a scratch folder
$LegacyLock = ''             # the old lock folder; defaults to .gpu-lock next to the state folder
$LegacyProcesses = @('Unity', 'SkiAreaDesignChallenge', 'PickerLab', 'LiftLab', 'blender')
$LegacyMaxAgeMinutes = 120
$TimeoutSec = 0              # give up waiting after this many seconds (0 waits forever)
$PollSec = 2.0
$RequireDesktop = ''         # e.g. 1920x1080: fail (or warn with -DesktopWarnOnly) on a smaller desktop
$DesktopWarnOnly = $false
$Json = $false
$Priority = $false           # -Priority: start ahead of every waiting ticket (task 4.01, the release smoke test)
$LimitMin = -1.0             # -LimitMin: kill the run after this many minutes (0 = no limit; -1 = the lane's default)
$HeavyProcesses = $null      # -HeavyProcesses a,b: replaces the GPU-heavy process list (tests); config.json otherwise
$WatchSec = 5.0              # how often a running ticket looks for unqueued GPU-heavy processes
$Since = ''                  # history: 24h, 7d, 90m or an ISO time
$Command = @()

$rest = @($args)
$i = 0
$positional = $false
while ($i -lt $rest.Count) {
    $a = [string]$rest[$i]
    if ($a -eq '--') { $Command = @($rest | Select-Object -Skip ($i + 1)); break }
    $flag = if ($a -match '^-[A-Za-z]') { $a.Substring(1).ToLowerInvariant() } else { '' }
    if ($flag -eq '') {
        if ($positional) { [Console]::Error.WriteLine("[gpu] Unexpected argument '$a'; put the command after --."); exit 64 }
        $Action = $a.ToLowerInvariant(); $positional = $true; $i++; continue
    }
    if ($flag -eq 'json') { $Json = $true; $i++; continue }
    if ($flag -eq 'priority') { $Priority = $true; $i++; continue }
    if ($flag -eq 'desktopwarnonly') { $DesktopWarnOnly = $true; $i++; continue }
    if (($i + 1) -ge $rest.Count) { [Console]::Error.WriteLine("[gpu] $a needs a value."); exit 64 }
    $next = [string]$rest[$i + 1]
    switch ($flag) {
        'lane' { $Lane = $next.ToLowerInvariant() }
        'name' { $Name = $next }
        'statedir' { $StateDir = $next }
        'legacylock' { $LegacyLock = $next }
        'legacyprocesses' { $LegacyProcesses = @($next -split ',' | Where-Object { $_ }) }
        'legacymaxageminutes' { $LegacyMaxAgeMinutes = [int]$next }
        'timeoutsec' { $TimeoutSec = [int]$next }
        'pollsec' { $PollSec = [double]$next }
        'requiredesktop' { $RequireDesktop = $next }
        'limitmin' { $LimitMin = [double]::Parse($next, [System.Globalization.CultureInfo]::InvariantCulture) }
        'heavyprocesses' { $HeavyProcesses = @($next -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
        'watchsec' { $WatchSec = [double]::Parse($next, [System.Globalization.CultureInfo]::InvariantCulture) }
        'since' { $Since = $next }
        default { [Console]::Error.WriteLine("[gpu] Unknown option $a; put the command after --."); exit 64 }
    }
    $i += 2
}

$DefaultStateDir = 'D:\skiAreaDesignChallenge\.gpu'
$DefaultMutexName = 'MountainPlannerGpu'

if (-not $StateDir) {
    if ($env:MP_GPU_STATE) { $StateDir = $env:MP_GPU_STATE } else { $StateDir = $DefaultStateDir }
}
$StateDir = [System.IO.Path]::GetFullPath($StateDir)
if (-not $LegacyLock) { $LegacyLock = Join-Path (Split-Path $StateDir -Parent) '.gpu-lock' }
$StateFile = Join-Path $StateDir 'state.json'
$ConfigFile = Join-Path $StateDir 'config.json'
$HistoryFile = Join-Path $StateDir 'history.jsonl'
$HistoryMaxBytes = 5MB
# Built-in lane limits (minutes) and GPU-heavy process names; config.json may override them (see the README).
$DefaultLimitMin = @{ measure = 60.0; render = 120.0 }
$DefaultHeavyProcesses = @('SkiAreaDesignChallenge', 'PickerLab', 'LiftLab', 'Unity', 'blender')
# A listed process with one of these arguments is headless (EditMode tests, -nographics runs, blender -b): not GPU-heavy.
$HeadlessPattern = '(?i)(^|\s)(-batchmode|-nographics|-b|--background)(?=\s|$)'

function Write-Info([string]$text) { [Console]::Error.WriteLine("[gpu] $text") }

# ---- Mutex guarding the state file ---------------------------------------------------------

function Get-MutexName {
    if ($StateDir.TrimEnd('\') -ieq $DefaultStateDir) { return $DefaultMutexName }
    $sha = [System.Security.Cryptography.SHA1]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($StateDir.ToLowerInvariant()))
    $hex = -join ($bytes[0..5] | ForEach-Object { $_.ToString('x2') })
    return "$DefaultMutexName-$hex"
}

$script:Mutex = $null
function Get-Mutex {
    if ($script:Mutex) { return $script:Mutex }
    $base = Get-MutexName
    try {
        $script:Mutex = New-Object System.Threading.Mutex($false, "Global\$base")
    } catch {
        $script:Mutex = New-Object System.Threading.Mutex($false, "Local\$base")
    }
    return $script:Mutex
}

# Runs $body with the state loaded, saves the state it returns (if any) and gives back $body's output.
function Invoke-Locked([scriptblock]$body, [switch]$ReadOnly) {
    $m = Get-Mutex
    $owned = $false
    try {
        try { $owned = $m.WaitOne(30000) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { throw 'Timed out waiting for the GPU state mutex.' }
        $state = Read-State
        $result = & $body $state
        if (-not $ReadOnly) { Write-State $state }
        return $result
    } finally {
        if ($owned) { $m.ReleaseMutex() }
    }
}

# ---- State ----------------------------------------------------------------------------------

function New-State { return [pscustomobject]@{ nextTicket = 1; holders = @(); queue = @() } }

function Read-State {
    if (-not (Test-Path -LiteralPath $StateFile)) { return New-State }
    try {
        $raw = [System.IO.File]::ReadAllText($StateFile)
        $s = $raw | ConvertFrom-Json
        return [pscustomobject]@{
            nextTicket = [long]$s.nextTicket
            holders    = @($s.holders | Where-Object { $_ })
            queue      = @($s.queue | Where-Object { $_ })
        }
    } catch {
        $bad = "$StateFile.bad-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
        Copy-Item -LiteralPath $StateFile -Destination $bad -Force
        Write-Info "state.json was unreadable; kept a copy at $bad and started fresh."
        return New-State
    }
}

function Write-State($state) {
    if (-not (Test-Path -LiteralPath $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
    $doc = [pscustomobject]@{
        nextTicket = $state.nextTicket
        holders    = @($state.holders)
        queue      = @($state.queue)
    }
    $text = ConvertTo-Json -InputObject $doc -Depth 6
    $tmp = "$StateFile.tmp"
    [System.IO.File]::WriteAllText($tmp, $text, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $StateFile) {
        [System.IO.File]::Replace($tmp, $StateFile, [NullString]::Value)
    } else {
        [System.IO.File]::Move($tmp, $StateFile)
    }
}

$script:Config = $null
function Get-Config {
    if ($script:Config) { return $script:Config }
    $c = $null
    if (Test-Path -LiteralPath $ConfigFile) {
        try { $c = [System.IO.File]::ReadAllText($ConfigFile) | ConvertFrom-Json } catch { Write-Info 'config.json is unreadable; using the defaults.' }
    }
    $has = { param($name) $c -and $c.PSObject.Properties[$name] -and $null -ne $c.$name }
    $slots = 2
    if ((& $has 'renderSlots') -and [int]$c.renderSlots -ge 1) { $slots = [int]$c.renderSlots }
    $limits = @{ measure = $DefaultLimitMin.measure; render = $DefaultLimitMin.render }
    if (& $has 'limitMin') {
        foreach ($l in 'measure', 'render') {
            if ($c.limitMin.PSObject.Properties[$l] -and $null -ne $c.limitMin.$l) { $limits[$l] = [double]$c.limitMin.$l }
        }
    }
    $heavy = $DefaultHeavyProcesses
    if (& $has 'heavyProcesses') { $heavy = @($c.heavyProcesses) }
    if (& $has 'extraHeavyProcesses') { $heavy = @($heavy) + @($c.extraHeavyProcesses) }
    if ($null -ne $HeavyProcesses) { $heavy = $HeavyProcesses }
    # Names are process names without .exe, as Get-Process shows them.
    $heavy = @($heavy | Where-Object { $_ } | ForEach-Object { ([string]$_) -replace '(?i)\.exe$', '' } | Sort-Object -Unique)
    $script:Config = [pscustomobject]@{ renderSlots = $slots; limitMin = $limits; heavy = $heavy }
    return $script:Config
}

function Get-RenderSlots { return (Get-Config).renderSlots }

# ---- History (task 0.5) ---------------------------------------------------------------------

# Appends one ticket's line. Call it only while holding the mutex (inside an Invoke-Locked body).
function Add-History($record) {
    try {
        if (-not (Test-Path -LiteralPath $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
        if ((Test-Path -LiteralPath $HistoryFile) -and (Get-Item -LiteralPath $HistoryFile).Length -gt $HistoryMaxBytes) {
            $old = Join-Path $StateDir "history-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).jsonl"
            [System.IO.File]::Move($HistoryFile, $old)
        }
        $line = (ConvertTo-Json -InputObject $record -Depth 4 -Compress) + "`n"
        [System.IO.File]::AppendAllText($HistoryFile, $line, (New-Object System.Text.UTF8Encoding($false)))
    } catch {
        Write-Info "could not write history: $($_.Exception.Message)"
    }
}

function New-HistoryRecord($entry, [string]$outcome, $exitCode, [string[]]$unqueued) {
    $cmd = [string]$entry.command
    if ($cmd.Length -gt 300) { $cmd = $cmd.Substring(0, 300) + '...' }
    $started = if ($entry.PSObject.Properties['started']) { [string]$entry.started } else { '' }
    $limit = if ($entry.PSObject.Properties['limitMin']) { $entry.limitMin } else { $null }
    $prio = if ($entry.PSObject.Properties['priority']) { [bool]$entry.priority } else { $false }
    return [pscustomobject]@{
        ticket = [long]$entry.ticket; lane = [string]$entry.lane; name = [string]$entry.name; command = $cmd
        pid = [int]$entry.pid; priority = $prio; limitMin = $limit
        queued = [string]$entry.queued; started = $started; ended = [DateTime]::UtcNow.ToString('o')
        exit = $exitCode; outcome = $outcome; unqueued = @($unqueued | Where-Object { $_ })
    }
}

# ---- GPU-heavy processes outside the queue (task 0.5) ---------------------------------------

# A snapshot of the processes, or $null when none of the GPU-heavy names runs (the cheap common case).
function Get-ProcessSnapshot {
    $names = @((Get-Config).heavy)
    if ($names.Count -eq 0) { return $null }
    $found = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) { return $null }
    try {
        return @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine, CreationDate -ErrorAction Stop)
    } catch {
        # Without CIM, every listed process counts: better a measure run waits than reads slow.
        return @($found | ForEach-Object { [pscustomobject]@{ ProcessId = $_.Id; ParentProcessId = 0; Name = "$($_.ProcessName).exe"; CommandLine = $null; CreationDate = $null } })
    }
}

# "Name (pid N)" for each listed process that is neither headless nor inside a queued run's process tree.
function Get-Unqueued($snapshot, $holders) {
    if (-not $snapshot) { return @() }
    $names = @((Get-Config).heavy)
    $kids = @{}
    $byPid = @{}
    foreach ($p in $snapshot) {
        $byPid[[int]$p.ProcessId] = $p
        $pp = [int]$p.ParentProcessId
        if (-not $kids.ContainsKey($pp)) { $kids[$pp] = New-Object System.Collections.ArrayList }
        [void]$kids[$pp].Add($p)
    }
    $inQueue = @{}
    $todo = New-Object System.Collections.Queue
    foreach ($h in @($holders)) {
        foreach ($root in @([int]$h.pid, $(if ($h.PSObject.Properties['childPid']) { [int]$h.childPid } else { 0 }))) {
            if ($root -gt 0 -and -not $inQueue.ContainsKey($root)) { $inQueue[$root] = $true; $todo.Enqueue($root) }
        }
    }
    while ($todo.Count -gt 0) {
        $id = [int]$todo.Dequeue()
        if (-not $kids.ContainsKey($id)) { continue }
        $parent = $byPid[$id]
        foreach ($k in $kids[$id]) {
            # A child born before its "parent" points at a reused process ID, not a real parent.
            if ($parent -and $parent.CreationDate -and $k.CreationDate -and $k.CreationDate -lt $parent.CreationDate) { continue }
            $kid = [int]$k.ProcessId
            if (-not $inQueue.ContainsKey($kid)) { $inQueue[$kid] = $true; $todo.Enqueue($kid) }
        }
    }
    $out = @()
    foreach ($p in $snapshot) {
        $n = ([string]$p.Name) -replace '(?i)\.exe$', ''
        if (-not ($names -contains $n)) { continue }
        if ($inQueue.ContainsKey([int]$p.ProcessId)) { continue }
        if ($p.CommandLine -and ([string]$p.CommandLine -match $HeadlessPattern)) { continue }
        $out += "$n (pid $($p.ProcessId))"
    }
    return @($out | Sort-Object -Unique)
}

# ---- Processes ------------------------------------------------------------------------------

# Start time in UTC ticks as a string, or '' when the process is gone; '?' when it is alive but unreadable.
function Get-ProcessStart([int]$processId) {
    if ($processId -le 0) { return '' }
    try { $p = [System.Diagnostics.Process]::GetProcessById($processId) } catch { return '' }
    try {
        if ($p.HasExited) { return '' }
        return [string]$p.StartTime.ToUniversalTime().Ticks
    } catch { return '?' }
    finally { $p.Dispose() }
}

function Test-SameProcess([int]$processId, [string]$start) {
    if ($processId -le 0) { return $false }
    $now = Get-ProcessStart $processId
    if ($now -eq '') { return $false }
    if ($now -eq '?' -or $start -eq '?' -or -not $start) { return $true }
    return $now -eq $start
}

function Test-EntryAlive($entry) {
    if (Test-SameProcess ([int]$entry.pid) ([string]$entry.pidStart)) { return $true }
    if ($entry.PSObject.Properties['childPid'] -and $entry.childPid) {
        if (Test-SameProcess ([int]$entry.childPid) ([string]$entry.childStart)) { return $true }
    }
    return $false
}

function Remove-Stale($state) {
    $removed = @()
    $removed += @($state.holders | Where-Object { -not (Test-EntryAlive $_) })
    $removed += @($state.queue | Where-Object { -not (Test-EntryAlive $_) })
    if ($removed.Count -gt 0) {
        $state.holders = @($state.holders | Where-Object { Test-EntryAlive $_ })
        $state.queue = @($state.queue | Where-Object { Test-EntryAlive $_ })
        # Its script died without writing its own history line (killed); "ended" is when this was noticed.
        foreach ($e in $removed) { Add-History (New-HistoryRecord $e 'stale' $null @()) }
    }
    return $removed
}

# ---- The old .gpu-lock folder ---------------------------------------------------------------

function Get-LegacyHold {
    if (-not (Test-Path -LiteralPath $LegacyLock -PathType Container)) { return $null }
    $ownerFile = Join-Path $LegacyLock 'owner.txt'
    $owner = ''
    if (Test-Path -LiteralPath $ownerFile) {
        $stamp = (Get-Item -LiteralPath $ownerFile).LastWriteTimeUtc
        try { $owner = ([System.IO.File]::ReadAllText($ownerFile)).Trim() -replace '\s+', ' ' } catch { $owner = '' }
    } else {
        $stamp = (Get-Item -LiteralPath $LegacyLock).LastWriteTimeUtc
    }
    $age = [DateTime]::UtcNow - $stamp
    $recent = $age.TotalMinutes -lt $LegacyMaxAgeMinutes
    $running = @()
    if (-not $recent) {
        $running = @(Get-Process -Name $LegacyProcesses -ErrorAction SilentlyContinue | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)
    }
    if (-not $recent -and $running.Count -eq 0) { return $null }
    $why = if ($recent) { "owner.txt is $([int]$age.TotalMinutes) min old" } else { "running: $($running -join ', ')" }
    return [pscustomobject]@{ path = $LegacyLock; owner = $owner; reason = $why }
}

# ---- Desktop check --------------------------------------------------------------------------

function Test-Desktop {
    if (-not $RequireDesktop) { return $true }
    if ($RequireDesktop -notmatch '^(\d+)x(\d+)$') { throw "-RequireDesktop wants WIDTHxHEIGHT, for example 1920x1080." }
    $w = [int]$Matches[1]; $h = [int]$Matches[2]
    # Without DPI awareness a scaled display reports a virtualized, smaller size.
    if (-not ('GpuLanesDpi' -as [type])) {
        Add-Type -Name GpuLanesDpi -Namespace '' -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
    }
    [void][GpuLanesDpi]::SetProcessDPIAware()
    Add-Type -AssemblyName System.Windows.Forms
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $remote = [System.Windows.Forms.SystemInformation]::TerminalServerSession
    if ($b.Width -ge $w -and $b.Height -ge $h) { return $true }
    $msg = "The desktop is $($b.Width)x$($b.Height) (remote session: $remote), smaller than $RequireDesktop. Players misbehave on the 640x480 remote desktop; reconnect at full size."
    if ($DesktopWarnOnly) { Write-Info "warning: $msg"; return $true }
    Write-Info $msg
    return $false
}

# ---- Command line quoting -------------------------------------------------------------------

function ConvertTo-ArgString([string[]]$items) {
    $parts = foreach ($a in $items) {
        if ($a -eq '') { '""'; continue }
        if ($a -notmatch '[\s"]') { $a; continue }
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append('"')
        $slashes = 0
        foreach ($ch in $a.ToCharArray()) {
            if ($ch -eq '\') { $slashes++; continue }
            if ($ch -eq '"') { [void]$sb.Append('\' * ($slashes * 2 + 1)); [void]$sb.Append('"'); $slashes = 0; continue }
            if ($slashes) { [void]$sb.Append('\' * $slashes); $slashes = 0 }
            [void]$sb.Append($ch)
        }
        if ($slashes) { [void]$sb.Append('\' * ($slashes * 2)) }
        [void]$sb.Append('"')
        $sb.ToString()
    }
    return ($parts -join ' ')
}

# ---- Scheduling -----------------------------------------------------------------------------

# The waiting queue in start order: -Priority tickets first, then by ticket number.
function Get-Ordered($queue) {
    return @($queue | Sort-Object @{ Expression = { if ($_.PSObject.Properties['priority'] -and $_.priority) { 0 } else { 1 } } }, @{ Expression = { [long]$_.ticket } })
}

# Why the head-of-queue entry can't start yet, or '' when it can.
# $unqueued lists the GPU-heavy processes running outside the queue: a measure run waits for them, and while any runs,
# waiting measure tickets don't hold up the render tickets behind them (task 0.5; the owner's play never waits).
function Get-Blocker($state, $entry, [int]$renderSlots, $legacy, [string[]]$unqueued = @()) {
    $unqueued = @($unqueued | Where-Object { $_ })
    foreach ($q in @(Get-Ordered $state.queue)) {
        if ([long]$q.ticket -eq [long]$entry.ticket) { break }
        if ($entry.lane -eq 'render' -and $q.lane -eq 'measure' -and $unqueued.Count -gt 0) { continue }
        return "behind ticket $($q.ticket) ($($q.lane), $($q.name))"
    }
    if ($legacy) { return "old lock $($legacy.path) ($($legacy.owner); $($legacy.reason))" }
    $holders = @($state.holders)
    $measure = @($holders | Where-Object { $_.lane -eq 'measure' })
    if ($measure.Count -gt 0) { return "measure run by $($measure[0].name)" }
    if ($entry.lane -eq 'measure') {
        if ($holders.Count -gt 0) { return "$($holders.Count) render run(s): $(($holders | ForEach-Object { $_.name }) -join ', ')" }
        if ($unqueued.Count -gt 0) { return "GPU process(es) outside the queue: $($unqueued -join ', ')" }
        return ''
    }
    if ($holders.Count -ge $renderSlots) { return "$renderSlots of $renderSlots render slots in use: $(($holders | ForEach-Object { $_.name }) -join ', ')" }
    return ''
}

# Drops the ticket and writes its history line in the same locked step, so "ended" always precedes the next start.
# $mine is the ticket's own copy of its entry, used when the entry was already released by hand.
function Remove-Mine([long]$ticket, $mine, [string]$outcome, $exitCode, [string[]]$unqueued) {
    Invoke-Locked {
        param($state)
        $entry = @(@($state.holders) + @($state.queue) | Where-Object { $_ -and [long]$_.ticket -eq $ticket })
        $e = if ($entry.Count -gt 0) { $entry[0] } else { $mine }
        $state.holders = @($state.holders | Where-Object { [long]$_.ticket -ne $ticket })
        $state.queue = @($state.queue | Where-Object { [long]$_.ticket -ne $ticket })
        if ($e) { Add-History (New-HistoryRecord $e $outcome $exitCode $unqueued) }
    } | Out-Null
}

# Holders without the lock (for the mid-run watch); an empty list if state.json is being replaced right now.
function Get-HoldersQuick {
    try { return @(Invoke-Locked { param($s) return @($s.holders) } -ReadOnly) } catch { return @() }
}

function Invoke-Run {
    if ($Lane -notin @('measure', 'render')) { Write-Info 'run needs -Lane measure or -Lane render.'; return 64 }
    if (-not $Name) { Write-Info 'run needs -Name "<thread>".'; return 64 }
    $cmd = @($Command)
    if ($cmd.Count -eq 0 -or -not $cmd[0]) { Write-Info 'run needs a command after --.'; return 64 }
    if (-not (Test-Desktop)) { return 125 }

    $exe = $cmd[0]
    $argString = ConvertTo-ArgString @($cmd | Select-Object -Skip 1)
    $commandText = (ConvertTo-ArgString @($exe)) + $(if ($argString) { " $argString" } else { '' })
    $self = [System.Diagnostics.Process]::GetCurrentProcess()
    $selfStart = [string]$self.StartTime.ToUniversalTime().Ticks
    $renderSlots = Get-RenderSlots
    # Task 0.5: a hard limit on the run itself (waiting has -TimeoutSec), so a hung player can't hold the GPU for hours.
    $limit = if ($LimitMin -ge 0) { $LimitMin } else { [double](Get-Config).limitMin[$Lane] }
    $ticket = 0
    $mine = $null
    $child = $null
    $finished = $false
    $outcome = 'interrupted'       # Ctrl+C or an error unless set otherwise below
    $exitCode = $null
    $seen = @()                    # GPU-heavy processes outside the queue seen while holding the slot
    try {
        $ticket = Invoke-Locked {
            param($state)
            [void](Remove-Stale $state)
            $t = [long]$state.nextTicket
            $state.nextTicket = $t + 1
            $state.queue = @($state.queue) + [pscustomobject]@{
                ticket = $t; lane = $Lane; name = $Name; pid = $PID; pidStart = $selfStart
                childPid = 0; childStart = ''; command = $commandText
                queued = [DateTime]::UtcNow.ToString('o'); started = ''; priority = [bool]$Priority; limitMin = $limit
            }
            return $t
        }
        $mine = [pscustomobject]@{
            ticket = $ticket; lane = $Lane; name = $Name; pid = $PID; command = $commandText
            queued = [DateTime]::UtcNow.ToString('o'); started = ''; priority = [bool]$Priority; limitMin = $limit
        }
        Write-Info "ticket $ticket ($Lane, $Name)$(if ($Priority) { ', priority' } else { '' })"

        $deadline = if ($TimeoutSec -gt 0) { [DateTime]::UtcNow.AddSeconds($TimeoutSec) } else { [DateTime]::MaxValue }
        $lastBlocker = $null
        while ($true) {
            $legacy = Get-LegacyHold
            $snapshot = Get-ProcessSnapshot
            $blocker = Invoke-Locked {
                param($state)
                [void](Remove-Stale $state)
                $me = @($state.queue | Where-Object { [long]$_.ticket -eq $ticket })
                if ($me.Count -eq 0) { return 'RELEASED' }
                $unq = @(Get-Unqueued $snapshot $state.holders)
                $b = Get-Blocker $state $me[0] $renderSlots $legacy $unq
                if ($b -eq '') {
                    $me[0].started = [DateTime]::UtcNow.ToString('o')
                    $state.queue = @($state.queue | Where-Object { [long]$_.ticket -ne $ticket })
                    $state.holders = @($state.holders) + $me[0]
                    $mine.queued = $me[0].queued
                    $mine.started = $me[0].started
                }
                return $b
            }
            if ($blocker -eq 'RELEASED') { Write-Info "ticket $ticket was released by hand while waiting."; $outcome = 'released'; return 124 }
            if ($blocker -eq '') { break }
            if ($blocker -ne $lastBlocker) { Write-Info "waiting: $blocker"; $lastBlocker = $blocker }
            if ([DateTime]::UtcNow -ge $deadline) { Write-Info "gave up after $TimeoutSec s."; $outcome = 'gave-up-waiting'; return 124 }
            Start-Sleep -Milliseconds ([int]($PollSec * 1000))
        }

        if (-not (Test-Desktop)) { $outcome = 'desktop-check'; return 125 }
        $limitText = if ($limit -gt 0) { "; limit $limit min" } else { '; no time limit' }
        Write-Info "started ticket $ticket ($Lane, $Name$limitText): $commandText"
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = $argString
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = (Get-Location).ProviderPath
        # The command can tell whether it has the GPU alone (measure) or shares it (render): timing tests judge a
        # player's budget only when alone.
        $psi.EnvironmentVariables['MP_GPU_LANE'] = $Lane
        try {
            $child = [System.Diagnostics.Process]::Start($psi)
        } catch {
            Write-Info "could not start $exe : $($_.Exception.Message)"
            $outcome = 'could-not-start'
            return 126
        }
        $null = $child.Handle  # keeps the exit code readable after exit
        $childStart = ''
        try { $childStart = [string]$child.StartTime.ToUniversalTime().Ticks } catch { $childStart = '?' }
        Invoke-Locked {
            param($state)
            foreach ($h in @($state.holders)) {
                if ([long]$h.ticket -eq $ticket) { $h.childPid = $child.Id; $h.childStart = $childStart }
            }
        } | Out-Null

        $runStart = [DateTime]::UtcNow
        $runEnd = if ($limit -gt 0) { $runStart.AddMinutes($limit) } else { [DateTime]::MaxValue }
        $nextWatch = $runStart
        $timedOut = $false
        # Short waits keep Ctrl+C responsive.
        while (-not $child.WaitForExit(250)) {
            $now = [DateTime]::UtcNow
            if ($now -ge $runEnd) { $timedOut = $true; break }
            if ($now -ge $nextWatch) {
                $nextWatch = $now.AddSeconds($WatchSec)
                $snapshot = Get-ProcessSnapshot
                if ($snapshot) {
                    $unq = @(Get-Unqueued $snapshot (Get-HoldersQuick))
                    $new = @($unq | Where-Object { $seen -notcontains $_ })
                    if ($new.Count -gt 0) {
                        $seen = @($seen) + $new
                        $what = if ($Lane -eq 'measure') { 'measure run contaminated' } else { 'seen during the run' }
                        Write-Info "warning: $what by GPU process(es) outside the queue: $($new -join ', ')"
                    }
                }
            }
        }
        if ($timedOut) {
            Write-Info "ticket $ticket hit its $limit-minute limit; stopping $exe (process $($child.Id)) and its children."
            & taskkill.exe /T /F /PID $child.Id 2>$null | Out-Null
            [void]$child.WaitForExit(15000)
            $finished = $true
            $outcome = 'time-limit'
            $exitCode = 124
            return 124
        }
        $child.WaitForExit()
        $finished = $true
        $code = $child.ExitCode
        $exitCode = $code
        # Ctrl+C in the run's console usually stops the command first: it exits with STATUS_CONTROL_C_EXIT (0xC000013A).
        $outcome = if ($code -eq 0) { 'ok' } elseif ($code -eq -1073741510) { 'interrupted' } else { 'failed' }
        if ($seen.Count -gt 0 -and $Lane -eq 'measure') { Write-Info "note: this measure run shared the GPU with $($seen -join ', '); its timings are suspect." }
        Write-Info "finished ticket $ticket with exit code $code"
        return $code
    } finally {
        if ($child -and -not $finished) {
            try {
                if (-not $child.HasExited) {
                    Write-Info "stopping $exe (process $($child.Id)) because the run was interrupted."
                    & taskkill.exe /T /F /PID $child.Id 2>$null | Out-Null
                }
            } catch { }
        }
        if ($ticket -gt 0) { Remove-Mine $ticket $mine $outcome $exitCode $seen }
    }
}

# ---- Other subcommands ----------------------------------------------------------------------

function Format-Entry($e, [string]$tag) {
    $when = if ($e.started) { "since $($e.started)" } else { "queued $($e.queued)" }
    $child = if ($e.PSObject.Properties['childPid'] -and $e.childPid) { ", child $($e.childPid)" } else { '' }
    return "  #$($e.ticket) $($e.lane.PadRight(7)) $($e.name)  (pid $($e.pid)$child, $when)$tag`n      $($e.command)"
}

function Invoke-Status {
    $legacy = Get-LegacyHold
    $state = Invoke-Locked { param($s) return $s }
    $slots = Get-RenderSlots
    $unqueued = @(Get-Unqueued (Get-ProcessSnapshot) $state.holders)
    $rows = @()
    foreach ($e in @($state.holders)) { $rows += [pscustomobject]@{ kind = 'holder'; entry = $e; stale = -not (Test-EntryAlive $e) } }
    foreach ($e in @(Get-Ordered $state.queue)) { $rows += [pscustomobject]@{ kind = 'waiting'; entry = $e; stale = -not (Test-EntryAlive $e) } }
    if ($Json) {
        [pscustomobject]@{
            stateDir    = $StateDir
            renderSlots = $slots
            nextTicket  = $state.nextTicket
            legacy      = $legacy
            unqueued    = $unqueued
            holders    = @($rows | Where-Object { $_.kind -eq 'holder' } | ForEach-Object { $_.entry | Add-Member -NotePropertyName stale -NotePropertyValue $_.stale -PassThru -Force })
            queue       = @($rows | Where-Object { $_.kind -eq 'waiting' } | ForEach-Object { $_.entry | Add-Member -NotePropertyName stale -NotePropertyValue $_.stale -PassThru -Force })
        } | ConvertTo-Json -Depth 6
        return 0
    }
    "GPU lanes ($StateDir; $slots render slots)"
    if ($legacy) { "Old lock: $($legacy.path) held by '$($legacy.owner)' ($($legacy.reason)); counts as a measure hold." }
    if ($unqueued.Count -gt 0) { "Outside the queue: $($unqueued -join ', '); measure runs wait." } else { 'Outside the queue: no GPU-heavy process' }
    $h =@($rows | Where-Object { $_.kind -eq 'holder' })
    $q = @($rows | Where-Object { $_.kind -eq 'waiting' })
    if ($h.Count -eq 0) { 'Holders: none' } else { 'Holders:'; foreach ($r in $h) { Format-Entry $r.entry $(if ($r.stale) { '  [STALE]' } else { '' }) } }
    if ($q.Count -eq 0) { 'Queue: empty' } else { 'Queue:'; foreach ($r in $q) { Format-Entry $r.entry $(if ($r.stale) { '  [STALE]' } else { '' }) } }
    return 0
}

function Invoke-ClearStale {
    $removed = Invoke-Locked { param($s) return @(Remove-Stale $s) }
    $removed = @($removed | Where-Object { $_ })
    if ($removed.Count -eq 0) { 'No stale entries.' } else { foreach ($e in $removed) { "Removed stale #$($e.ticket) $($e.lane) $($e.name) (pid $($e.pid))" } }
    return 0
}

function Invoke-Release {
    if (-not $Name) { Write-Info 'release needs -Name "<thread>".'; return 64 }
    $removed = Invoke-Locked {
        param($s)
        $gone = @(@($s.holders) + @($s.queue) | Where-Object { $_ -and $_.name -eq $Name })
        $s.holders = @($s.holders | Where-Object { $_.name -ne $Name })
        $s.queue = @($s.queue | Where-Object { $_.name -ne $Name })
        return $gone
    }
    $removed = @($removed | Where-Object { $_ })
    if ($removed.Count -eq 0) { "Nothing held or queued by '$Name'." } else { foreach ($e in $removed) { "Released #$($e.ticket) $($e.lane) $($e.name) (pid $($e.pid)); its process keeps running." } }
    return 0
}

function ConvertTo-Utc([string]$text) {
    if (-not $text) { return $null }
    try { return [DateTime]::Parse($text, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() } catch { return $null }
}

# An ISO string or a real $null (an empty $() would serialize as {}).
function Format-Utc($when) {
    if ($when) { return $when.ToString('o') }
    return $null
}

function Read-History {
    $files = @(Get-ChildItem -LiteralPath $StateDir -Filter 'history*.jsonl' -File -ErrorAction SilentlyContinue | Sort-Object { if ($_.Name -eq 'history.jsonl') { 1 } else { 0 } }, Name)
    $records = @()
    foreach ($f in $files) {
        foreach ($line in [System.IO.File]::ReadAllLines($f.FullName)) {
            if (-not $line.Trim()) { continue }
            try { $r = $line | ConvertFrom-Json } catch { continue }
            $records += [pscustomobject]@{
                ticket = [long]$r.ticket; lane = [string]$r.lane; name = [string]$r.name; command = [string]$r.command
                outcome = [string]$r.outcome; exit = $r.exit; unqueued = @($r.unqueued | Where-Object { $_ })
                queued = ConvertTo-Utc $r.queued; started = ConvertTo-Utc $r.started; ended = ConvertTo-Utc $r.ended
            }
        }
    }
    return $records
}

# Rule breaks in the held spans: a measure run sharing the GPU with any ticket, more render runs than slots, and
# measure runs that saw a GPU-heavy process outside the queue.
function Find-Overlaps($records, [int]$slots) {
    $held = @($records | Where-Object { $_.started -and $_.ended } | Sort-Object started)
    $found = @()
    for ($i = 0; $i -lt $held.Count; $i++) {
        $a = $held[$i]
        for ($j = $i + 1; $j -lt $held.Count; $j++) {
            $b = $held[$j]
            if ($b.started -ge $a.ended) { break }
            if ($a.lane -ne 'measure' -and $b.lane -ne 'measure') { continue }
            $sec = ([Math]::Min($a.ended.Ticks, $b.ended.Ticks) - $b.started.Ticks) / 1e7
            $found += [pscustomobject]@{ kind = 'measure-overlap'; tickets = @($a.ticket, $b.ticket); at = $b.started.ToString('o'); seconds = [Math]::Round($sec, 1)
                text = "#$($a.ticket) ($($a.lane), $($a.name)) and #$($b.ticket) ($($b.lane), $($b.name)) held the GPU together for $([Math]::Round($sec, 1)) s" }
        }
    }
    $events = foreach ($r in @($held | Where-Object { $_.lane -eq 'render' })) {
        [pscustomobject]@{ t = $r.started.Ticks; d = 1; r = $r }; [pscustomobject]@{ t = $r.ended.Ticks; d = -1; r = $r }
    }
    $open = @{}
    $flagged = $false
    foreach ($e in @($events | Sort-Object t, d)) {
        if ($e.d -gt 0) { $open[$e.r.ticket] = $e.r } else { $open.Remove($e.r.ticket); $flagged = $false; continue }
        if ($open.Count -gt $slots -and -not $flagged) {
            $flagged = $true
            $ids = @($open.Keys | Sort-Object)
            $found += [pscustomobject]@{ kind = 'render-over-slots'; tickets = $ids; at = $e.r.started.ToString('o'); seconds = $null
                text = "$($open.Count) render runs at once (slots: $slots): #$($ids -join ', #')" }
        }
    }
    foreach ($r in @($held | Where-Object { $_.lane -eq 'measure' -and $_.unqueued.Count -gt 0 })) {
        $found += [pscustomobject]@{ kind = 'contaminated'; tickets = @($r.ticket); at = $r.started.ToString('o'); seconds = $null
            text = "measure #$($r.ticket) ($($r.name)) ran while outside the queue: $($r.unqueued -join ', ')" }
    }
    return $found
}

function Invoke-History {
    $cutoff = [DateTime]::MinValue
    if ($Since) {
        if ($Since -match '^(\d+(?:\.\d+)?)([mhd])$') {
            $n = [double]$Matches[1]
            $span = switch ($Matches[2]) { 'm' { [TimeSpan]::FromMinutes($n) } 'h' { [TimeSpan]::FromHours($n) } 'd' { [TimeSpan]::FromDays($n) } }
            $cutoff = [DateTime]::UtcNow - $span
        } else {
            $cutoff = ConvertTo-Utc $Since
            if (-not $cutoff) { Write-Info "-Since wants 90m, 24h, 7d or an ISO time, not '$Since'."; return 64 }
        }
    }
    $all = @(Read-History | Where-Object { $_.ended -and $_.ended -ge $cutoff } | Sort-Object { if ($_.started) { $_.started } else { $_.ended } }, ticket)
    $overlaps = @(Find-Overlaps $all (Get-RenderSlots))
    $rows = $all
    if ($Name) {
        $rows = @($all | Where-Object { $_.name -eq $Name })
        $mineIds = @($rows | ForEach-Object { $_.ticket })
        $overlaps = @($overlaps | Where-Object { @($_.tickets | Where-Object { $mineIds -contains $_ }).Count -gt 0 })
    }
    if ($Json) {
        [pscustomobject]@{
            stateDir = $StateDir
            since    = $(if ($Since) { $cutoff.ToString('o') } else { $null })
            records  = @($rows | ForEach-Object {
                    [pscustomobject]@{ ticket = $_.ticket; lane = $_.lane; name = $_.name; command = $_.command; outcome = $_.outcome; exit = $_.exit
                        unqueued = $_.unqueued; queued = (Format-Utc $_.queued); started = (Format-Utc $_.started); ended = (Format-Utc $_.ended) }
                })
            overlaps = $overlaps
        } | ConvertTo-Json -Depth 6
        return 0
    }
    $fmt = 'MM-dd HH:mm:ss'
    "GPU history ($StateDir$(if ($Since) { ", since $($cutoff.ToLocalTime().ToString('yyyy-MM-dd HH:mm'))" })$(if ($Name) { ", $Name" }); local times)"
    if ($rows.Count -eq 0) { '  No runs recorded.' }
    foreach ($r in $rows) {
        $start = if ($r.started) { $r.started.ToLocalTime().ToString($fmt) } else { 'never started ' }
        $ran = if ($r.started) { [TimeSpan]::FromSeconds([Math]::Round(($r.ended - $r.started).TotalSeconds)).ToString() } else { '-' }
        $waited = if ($r.queued -and $r.started) { [int]($r.started - $r.queued).TotalSeconds } elseif ($r.queued) { [int]($r.ended - $r.queued).TotalSeconds } else { 0 }
        $code = if ($null -ne $r.exit) { "exit $($r.exit)" } else { 'exit -' }
        $tag = if ($r.unqueued.Count -gt 0) { $(if ($r.lane -eq 'measure') { "  CONTAMINATED: $($r.unqueued -join ', ')" } else { "  (outside the queue: $($r.unqueued -join ', '))" }) } else { '' }
        "  #$($r.ticket.ToString().PadRight(5)) $start  ran $($ran.PadLeft(8))  waited $($waited.ToString().PadLeft(5)) s  $($r.lane.PadRight(7)) $($r.name.PadRight(14)) $($code.PadRight(8)) $($r.outcome)$tag"
    }
    if ($overlaps.Count -eq 0) { 'Overlaps: none' } else { "Overlaps: $($overlaps.Count)"; foreach ($o in $overlaps) { "  $($o.kind): $($o.text)" } }
    return 0
}

$code = 64
switch ($Action) {
    'run' { $code = Invoke-Run }
    'status' { $code = Invoke-Status }
    'history' { $code = Invoke-History }
    'clear-stale' { $code = Invoke-ClearStale }
    'release' { $code = Invoke-Release }
    default { Write-Info "Unknown action '$Action'. Use run, status, history, clear-stale or release."; $code = 64 }
}
# Status, history, clear-stale and release print text lines; the exit code is the last item.
$out = @($code)
if ($out.Count -gt 1) { $out[0..($out.Count - 2)] | ForEach-Object { Write-Output $_ } }
exit ([int]$out[-1])
