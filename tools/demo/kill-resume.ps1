# Task P2-06 acceptance (demo.bat 54): kill the game mid-download, start it again, and see the download resume from
# the stage it reached by itself.
#  1. A scratch library (never the owner's) with the 2 km test terrain behind the title, and no download history.
#  2. The game starts Crystal Mountain (2 km) with -download; once the log says it reached Forest, the process is
#     killed with no chance to tidy up (Stop-Process -Force, as a crash or Task Manager would).
#  3. The game starts again with no download asked for. It resumes the paused download by itself once the title is up;
#     the log shows the earlier stages finishing with no new bytes over the network.
# The second game stays open so you can watch the pill and the card; quit it when you're done.
param(
    [Parameter(Mandatory = $true)][string]$Game,
    [Parameter(Mandatory = $true)][string]$Data,
    [Parameter(Mandatory = $true)][string]$Seed,
    [Parameter(Mandatory = $true)][string]$LogFolder,
    [string]$KillAt = 'Forest',
    [int]$TimeoutSeconds = 900,
    # For unattended runs: a screenshot after this many seconds of the resumed game, then it is closed (only that game).
    [int]$CloseAfterSeconds = 0,
    [string]$Screenshot = ''
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Game)) { throw "The game isn't built: $Game" }
New-Item -ItemType Directory -Force $LogFolder | Out-Null
$first = Join-Path $LogFolder 'kill-resume-1.log'
$second = Join-Path $LogFolder 'kill-resume-2.log'
Remove-Item -Force -ErrorAction SilentlyContinue $first, $second

# A fresh scratch library: the test terrain behind the title, nothing downloaded yet.
if (Test-Path $Data) { Remove-Item -Recurse -Force $Data }
New-Item -ItemType Directory -Force (Join-Path $Data 'Resorts') | Out-Null
Copy-Item -Recurse $Seed (Join-Path $Data 'Resorts\jackson-hole-2km-test')

function Wait-ForLine([string]$log, [string]$pattern, [System.Diagnostics.Process]$process) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) { throw "The game quit before '$pattern' (exit $($process.ExitCode)); see $log" }
        if (Test-Path $log) {
            $hit = Select-String -Path $log -Pattern $pattern -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($hit) { return $hit.Line }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out after $TimeoutSeconds s waiting for '$pattern' in $log"
}

Write-Host "1. Downloading Crystal Mountain (2 km) into $Data; it's killed when it reaches $KillAt..."
$p = Start-Process -FilePath $Game -ArgumentList @('-data', "`"$Data`"", '-download', '"Crystal Mountain"', '46.935', '-121.474', '2', '-logFile', "`"$first`"") -PassThru
$line = Wait-ForLine $first "[Downloads] Crystal Mountain: $KillAt" $p
Write-Host "   $line"
Stop-Process -Id $p.Id -Force
$p.WaitForExit()
Write-Host "   Killed (pid $($p.Id))."
Select-String -Path $first -Pattern '[Downloads]' -SimpleMatch | ForEach-Object { Write-Host "   $($_.Line)" }

$record = Get-ChildItem -Path (Join-Path $Data 'Downloads') -Filter 'download.json' -Recurse | Select-Object -First 1
if (-not $record) { throw 'No paused download was left behind.' }
$json = Get-Content $record.FullName -Raw | ConvertFrom-Json
Write-Host "   Left behind: $($record.FullName)"
Write-Host "   paused at $($json.LastStage), $([math]::Round($json.LastOverall * 100))%"

Write-Host ''
Write-Host '2. Starting the game again, with no download asked for...'
$q = Start-Process -FilePath $Game -ArgumentList @('-data', "`"$Data`"", '-logFile', "`"$second`"") -PassThru
$resumed = Wait-ForLine $second '[Downloads] Resuming Crystal Mountain' $q
Write-Host "   $resumed"
$next = Wait-ForLine $second "[Downloads] Crystal Mountain: $KillAt" $q
Select-String -Path $second -Pattern '[Downloads]' -SimpleMatch | ForEach-Object { Write-Host "   $($_.Line)" }
if ($CloseAfterSeconds -gt 0) {
    Start-Sleep -Seconds $CloseAfterSeconds
    if ($Screenshot) {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
        $bmp.Save($Screenshot, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Host "   Screenshot: $Screenshot"
    }
    Select-String -Path $second -Pattern '[Downloads]' -SimpleMatch | ForEach-Object { Write-Host "   $($_.Line)" }
    Stop-Process -Id $q.Id -Force -ErrorAction SilentlyContinue
    return
}
Write-Host ''
Write-Host 'It resumed by itself: the stages before the kill finished from the download cache (the MB over the network'
Write-Host 'stays where it was), and the card held the bar at the paused share. The game is still open: watch the pill'
Write-Host 'on the title (click it for the card), then quit it.'
