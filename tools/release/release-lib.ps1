# Shared helpers for tools/release (task P2-10): release.ps1, compile-check.ps1 and test-release.ps1 dot-source this.
# Windows PowerShell 5.1: no ?:, ?? or && here.

$script:ReleaseTagGlob = 'v[0-9]*.[0-9]*.[0-9]*'
$script:ProductFile = 'SkiAreaDesignChallenge'
$script:LicenceLapsed = 198
# Windows' own bsdtar (it writes zips); a Git or MSYS tar earlier on PATH can't.
$script:Tar = Join-Path $env:SystemRoot 'System32\tar.exe'

# True when the Unity editor holds this project open (it keeps Temp/UnityLockfile locked).
function Test-EditorOpen([string]$project) {
    $lock = Join-Path $project 'Temp\UnityLockfile'
    if (-not (Test-Path $lock)) { return $false }
    try {
        $stream = [System.IO.File]::Open($lock, 'Open', 'ReadWrite', 'None')
        $stream.Close()
        return $false
    } catch { return $true }
}

# Runs Unity and waits; the exit code. Arguments with spaces are quoted.
function Invoke-Unity([string]$exe, [string[]]$arguments) {
    $quoted = $arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
    $p = Start-Process -FilePath $exe -ArgumentList ($quoted -join ' ') -Wait -PassThru -NoNewWindow
    return $p.ExitCode
}

# What a batch-mode compile run's log says: Code 0/1/3/198, a Message and the error lines.
function Read-CompileResult([string]$log, [datetime]$started, [int]$code) {
    if ($code -eq $script:LicenceLapsed) {
        return @{ Code = 198; Message = 'Unity exited 198: the licence lapsed. Refresh it in Unity Hub, then run this again.'; Errors = @() }
    }
    if (-not (Test-Path $log) -or (Get-Item $log).LastWriteTime -lt $started) {
        return @{ Code = 3; Message = "Unity exited $code without writing a fresh log ($log)."; Errors = @() }
    }
    $errors = @(Select-String -Path $log -Pattern 'error CS\d{4}' | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique)
    if ($errors.Count -gt 0 -or $code -ne 0) {
        $why = 'Compile errors'
        if ($errors.Count -eq 0) { $why = "Unity exited $code (see $log)" }
        return @{ Code = 1; Message = "$why."; Errors = $errors }
    }
    return @{ Code = 0; Message = 'Every assembly compiled.'; Errors = @() }
}

# What a -runTests XML says: Ok, Message, Total, Failed. A file older than the run never counts (a stale XML once looked like a pass).
function Read-TestResult([string]$xml, [datetime]$started, [int]$code) {
    if ($code -eq $script:LicenceLapsed) {
        return @{ Ok = $false; Message = 'Unity exited 198: the licence lapsed. Refresh it in Unity Hub, then run this again.'; Total = 0; Failed = 0 }
    }
    if (-not (Test-Path $xml) -or (Get-Item $xml).LastWriteTime -lt $started) {
        return @{ Ok = $false; Message = "No fresh results at $xml (Unity exited $code; a compile error stops the tests)."; Total = 0; Failed = 0 }
    }
    [xml]$doc = Get-Content -Raw $xml
    $run = $doc.'test-run'
    $total = [int]$run.total
    $failed = [int]$run.failed
    $ok = ($failed -eq 0) -and ($total -gt 0) -and ($run.result -like 'Passed*')
    return @{ Ok = $ok; Message = "$($total - $failed)/$total passed"; Total = $total; Failed = $failed }
}

# Files Git LFS left as pointer text (git lfs ls-files marks them '-').
function Get-LfsPointers([string]$root) {
    $ErrorActionPreference = 'Continue'   # PS 5.1 under Stop turns a native command's stderr into an error, 2>$null or not
    $lines = & git -C $root lfs ls-files 2>$null
    return @($lines | Where-Object { $_ -match '^[0-9a-f]+ - ' } | ForEach-Object { ($_ -split ' - ', 2)[1] })
}

# The release tag at HEAD (v0.2.0), or $null.
function Get-ReleaseTag([string]$root) {
    $ErrorActionPreference = 'Continue'   # "No names found" on stderr is the normal answer for an untagged commit
    $tag = & git -C $root describe --tags --exact-match --match $script:ReleaseTagGlob HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $tag) { return $null }
    return $tag.Trim()
}

function Test-ReleaseVersion([string]$version) { return ($version -match '^\d+\.\d+\.\d+$') }

# The folder and zip names: SkiAreaDesignChallenge-0.2.0 and SkiAreaDesignChallenge-0.2.0-win64.zip.
function Get-ReleaseName([string]$version) { return "$($script:ProductFile)-$version" }
function Get-ZipName([string]$version) { return "$(Get-ReleaseName $version)-win64.zip" }

# What's wrong with a release zip's layout (empty when it's sound): one top folder named for the version holding the
# exe, its _Data with the bundled demo and a cache, README.txt and NOTICES.txt, and nothing Unity marks not to ship.
function Test-ZipLayout([string]$zip, [string]$version) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $problems = New-Object System.Collections.Generic.List[string]
    $top = Get-ReleaseName $version
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
    } finally { $archive.Dispose() }
    $outside = @($names | Where-Object { -not $_.StartsWith("$top/") })
    if ($outside.Count -gt 0) { $problems.Add("entries outside $top/: $($outside[0])") }
    foreach ($need in @("$top/$($script:ProductFile).exe", "$top/UnityPlayer.dll", "$top/README.txt", "$top/NOTICES.txt")) {
        if ($names -notcontains $need) { $problems.Add("missing $need") }
    }
    $demo = "$top/$($script:ProductFile)_Data/StreamingAssets/Demo/"
    if (-not ($names | Where-Object { $_.StartsWith($demo) -and $_.EndsWith('/manifest.json') })) { $problems.Add('no bundled demo package') }
    if (-not ($names | Where-Object { $_.StartsWith($demo) -and $_ -match '/cache-v\d+/' })) { $problems.Add('no prebuilt demo cache') }
    if ($names | Where-Object { $_ -match 'DoNotShip' }) { $problems.Add('ships a DoNotShip folder') }
    return , $problems.ToArray()
}
