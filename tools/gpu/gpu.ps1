<#
.SYNOPSIS
  GPU lane scheduler: runs a command once the shared GPU has a free slot in its lane.

.DESCRIPTION
  Lanes:
    measure  exclusive; nothing else may hold a slot (benchmarks, timing, review movies).
    render   up to renderSlots at once (default 2), none while a measure run holds its slot
             (-screenshot / -uicapture players, PlayMode on site caches, Cycles renders, Lift Lab captures).
  Waiting runs take numbered tickets and start strictly in ticket order, so a waiting measure run
  blocks render runs queued after it. A holder or waiter whose process (PID plus start time) is
  gone is stale and is removed on the next poll. The old D:\skiAreaDesignChallenge\.gpu-lock folder
  counts as a measure hold while its owner.txt is under 2 hours old or a Unity, player or Blender
  process is running.

  Usage:
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 run -Lane measure|render -Name "<thread>" -- <exe> <args...>
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 status [-Json]
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 clear-stale
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 release -Name "<thread>"

  Exit codes for run: the command's own exit code; 124 when -TimeoutSec ran out while waiting;
  125 for a failed -RequireDesktop check; 126 when the command could not start; 64 for usage errors.
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
function Invoke-Locked([scriptblock]$body) {
    $m = Get-Mutex
    $owned = $false
    try {
        try { $owned = $m.WaitOne(30000) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { throw 'Timed out waiting for the GPU state mutex.' }
        $state = Read-State
        $result = & $body $state
        Write-State $state
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

function Get-RenderSlots {
    if (Test-Path -LiteralPath $ConfigFile) {
        try {
            $c = [System.IO.File]::ReadAllText($ConfigFile) | ConvertFrom-Json
            if ($c.renderSlots -and [int]$c.renderSlots -ge 1) { return [int]$c.renderSlots }
        } catch { Write-Info "config.json is unreadable; using 2 render slots." }
    }
    return 2
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

# Why the head-of-queue entry can't start yet, or '' when it can.
function Get-Blocker($state, $entry, [int]$renderSlots, $legacy) {
    $head = @($state.queue | Sort-Object { [long]$_.ticket })[0]
    if ($head.ticket -ne $entry.ticket) { return "behind ticket $($head.ticket) ($($head.lane), $($head.name))" }
    if ($legacy) { return "old lock $($legacy.path) ($($legacy.owner); $($legacy.reason))" }
    $holders = @($state.holders)
    $measure = @($holders | Where-Object { $_.lane -eq 'measure' })
    if ($measure.Count -gt 0) { return "measure run by $($measure[0].name)" }
    if ($entry.lane -eq 'measure') {
        if ($holders.Count -gt 0) { return "$($holders.Count) render run(s): $(($holders | ForEach-Object { $_.name }) -join ', ')" }
        return ''
    }
    if ($holders.Count -ge $renderSlots) { return "$renderSlots of $renderSlots render slots in use: $(($holders | ForEach-Object { $_.name }) -join ', ')" }
    return ''
}

function Remove-Mine([long]$ticket) {
    Invoke-Locked {
        param($state)
        $state.holders = @($state.holders | Where-Object { [long]$_.ticket -ne $ticket })
        $state.queue = @($state.queue | Where-Object { [long]$_.ticket -ne $ticket })
    } | Out-Null
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
    $ticket = 0
    $child = $null
    $finished = $false
    try {
        $ticket = Invoke-Locked {
            param($state)
            [void](Remove-Stale $state)
            $t = [long]$state.nextTicket
            $state.nextTicket = $t + 1
            $state.queue = @($state.queue) + [pscustomobject]@{
                ticket = $t; lane = $Lane; name = $Name; pid = $PID; pidStart = $selfStart
                childPid = 0; childStart = ''; command = $commandText
                queued = [DateTime]::UtcNow.ToString('o'); started = ''
            }
            return $t
        }
        Write-Info "ticket $ticket ($Lane, $Name)"

        $deadline = if ($TimeoutSec -gt 0) { [DateTime]::UtcNow.AddSeconds($TimeoutSec) } else { [DateTime]::MaxValue }
        $lastBlocker = $null
        while ($true) {
            $legacy = Get-LegacyHold
            $blocker = Invoke-Locked {
                param($state)
                [void](Remove-Stale $state)
                $me = @($state.queue | Where-Object { [long]$_.ticket -eq $ticket })
                if ($me.Count -eq 0) { return 'RELEASED' }
                $b = Get-Blocker $state $me[0] $renderSlots $legacy
                if ($b -eq '') {
                    $me[0].started = [DateTime]::UtcNow.ToString('o')
                    $state.queue = @($state.queue | Where-Object { [long]$_.ticket -ne $ticket })
                    $state.holders = @($state.holders) + $me[0]
                }
                return $b
            }
            if ($blocker -eq 'RELEASED') { Write-Info "ticket $ticket was released by hand while waiting."; return 124 }
            if ($blocker -eq '') { break }
            if ($blocker -ne $lastBlocker) { Write-Info "waiting: $blocker"; $lastBlocker = $blocker }
            if ([DateTime]::UtcNow -ge $deadline) { Write-Info "gave up after $TimeoutSec s."; return 124 }
            Start-Sleep -Milliseconds ([int]($PollSec * 1000))
        }

        if (-not (Test-Desktop)) { return 125 }
        Write-Info "started ticket $ticket ($Lane, $Name): $commandText"
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = $argString
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = (Get-Location).ProviderPath
        try {
            $child = [System.Diagnostics.Process]::Start($psi)
        } catch {
            Write-Info "could not start $exe : $($_.Exception.Message)"
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

        # Short waits keep Ctrl+C responsive.
        while (-not $child.WaitForExit(250)) { }
        $child.WaitForExit()
        $finished = $true
        $code = $child.ExitCode
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
        if ($ticket -gt 0) { Remove-Mine $ticket }
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
    $rows = @()
    foreach ($e in @($state.holders)) { $rows += [pscustomobject]@{ kind = 'holder'; entry = $e; stale = -not (Test-EntryAlive $e) } }
    foreach ($e in @($state.queue | Sort-Object { [long]$_.ticket })) { $rows += [pscustomobject]@{ kind = 'waiting'; entry = $e; stale = -not (Test-EntryAlive $e) } }
    if ($Json) {
        [pscustomobject]@{
            stateDir    = $StateDir
            renderSlots = $slots
            nextTicket  = $state.nextTicket
            legacy      = $legacy
            holders     = @($rows | Where-Object { $_.kind -eq 'holder' } | ForEach-Object { $_.entry | Add-Member -NotePropertyName stale -NotePropertyValue $_.stale -PassThru -Force })
            queue       = @($rows | Where-Object { $_.kind -eq 'waiting' } | ForEach-Object { $_.entry | Add-Member -NotePropertyName stale -NotePropertyValue $_.stale -PassThru -Force })
        } | ConvertTo-Json -Depth 6
        return 0
    }
    "GPU lanes ($StateDir; $slots render slots)"
    if ($legacy) { "Old lock: $($legacy.path) held by '$($legacy.owner)' ($($legacy.reason)); counts as a measure hold." }
    $h = @($rows | Where-Object { $_.kind -eq 'holder' })
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

$code = 64
switch ($Action) {
    'run' { $code = Invoke-Run }
    'status' { $code = Invoke-Status }
    'clear-stale' { $code = Invoke-ClearStale }
    'release' { $code = Invoke-Release }
    default { Write-Info "Unknown action '$Action'. Use run, status, clear-stale or release."; $code = 64 }
}
# Status, clear-stale and release print text lines; the exit code is the last item.
$out = @($code)
if ($out.Count -gt 1) { $out[0..($out.Count - 2)] | ForEach-Object { Write-Output $_ } }
exit ([int]$out[-1])
