# Task P3-07: the style tile's captures. One game player per picture, each through the GPU's render lane, with the style
# tile built by command (-styletile): the benchmark's views plus close views of the trail and the lift, in winter and
# summer, by day and at night. Pictures land in -Out as <view>_<season>_<time>.png.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/style-tile/captures.ps1 -Game Builds\Windows\SkiAreaDesignChallenge.exe -Out test-results\style-tile
param(
  [Parameter(Mandatory = $true)][string]$Game,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Owner = "p3-07",
  [string[]]$Only = @()
)
$ErrorActionPreference = "Continue"
$gpu = Join-Path $PSScriptRoot "..\gpu\gpu.ps1"
New-Item -ItemType Directory -Force $Out | Out-Null

# x, z (metres from the site's centre), distance, yaw, pitch: the -view format. The first three are the benchmark's
# (MountainViewer.Review.cs); the rest look at the style tile itself.
$views = [ordered]@{
  "overview"  = "NaN,0,0,200,24"
  "slope"     = "-300,700,1200,200,18"
  "forest"    = "350,-150,250,200,30"
  "trail"     = "250,-600,140,300,24"
  "trailnear" = "200,-520,45,320,14"
  "loading"   = "625,-821,45,312,10"
  "liftline"  = "434,-649,90,222,14"
  "top"       = "132,-378,60,42,20"
}
$conditions = [ordered]@{
  "winter_day"   = @()
  "winter_night" = @("-time", "21:30")
  "summer_day"   = @("-season", "summer")
  "summer_night" = @("-season", "summer", "-time", "22:30")
}

foreach ($v in $views.Keys) {
  if ($Only.Count -gt 0 -and $Only -notcontains $v) { continue }
  foreach ($c in $conditions.Keys) {
    $name = "${v}_$c"
    $view = $views[$v]
    $viewArgs = @()
    if ($view -notlike "NaN*") { $viewArgs = @("-view", $view) } else { $viewArgs = @("-view", "0,0,5500,200,24") }
    $extra = $conditions[$c]
    powershell -NoProfile -ExecutionPolicy Bypass -File $gpu run -Lane render -Name $Owner -- $Game -benchres 1920x1080 -notitle -styletile @viewArgs @extra -nohud -screenshot (Join-Path $Out "$name.png") -logFile (Join-Path $Out "$name.log")
    "$name exit $LASTEXITCODE"
  }
}
