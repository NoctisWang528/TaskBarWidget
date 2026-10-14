[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PreviousBuild,
    [string]$NewBuild,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$productRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $productRoot 'artifacts'
$builds = Join-Path $artifacts 'builds'
if (-not $NewBuild) { $NewBuild = (Get-Content -LiteralPath (Join-Path $artifacts 'latest-build.txt') -Raw).Trim() }

function Get-SafeBuild([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $directory = Get-Item -LiteralPath $full
    if (-not $directory.PSIsContainer -or $directory.Parent.FullName -ine $builds -or $directory.Name -notmatch '^\d{8}-\d{6}$') {
        throw 'Only timestamped directories directly inside TaskBarWidgets/artifacts/builds can be retired.'
    }
    $walk = $directory
    while ($null -ne $walk -and $walk.FullName.Length -ge $productRoot.Length) {
        if ($walk.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Build paths must not contain links or junctions.' }
        $walk = $walk.Parent
    }
    $info = Get-Content -LiteralPath (Join-Path $full 'build-info.json') -Raw | ConvertFrom-Json
    if ($info.component -ne 'All' -or $info.configuration -ne 'Release' -or -not $info.buildCompletedAt) {
        throw 'Both builds must have successful full Release build metadata.'
    }
    foreach ($relative in @('TaskBarWidgets.exe', 'OpenHardwareMonitorApi.dll', 'LibreHardwareMonitorLib.dll', 'Media\TaskBarWidgets.Media.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $full $relative) -PathType Leaf)) { throw "Incomplete build: $full" }
    }
    [pscustomobject]@{ Path = $full; StartedAt = [DateTimeOffset]::Parse($info.buildStartedAt) }
}

function Get-SafeTree([string]$Directory) {
    $prefix = $Directory.TrimEnd('\') + '\'
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    while ($pending.Count) {
        foreach ($entry in ([IO.DirectoryInfo]::new($pending.Pop())).GetFileSystemInfos()) {
            if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Build contents contain an unsafe path or link.' }
            if ($entry.Attributes -band [IO.FileAttributes]::Directory) { $pending.Push($entry.FullName) }
            else { $entry }
        }
    }
}

function Assert-NoBuildProcesses([string[]]$Directories) {
    foreach ($process in Get-CimInstance Win32_Process) {
        if ($process.Name -in @('TaskBarWidgets.exe', 'TaskBarWidgets.Media.exe') -and -not $process.ExecutablePath) {
            throw 'A product process cannot be inspected; cleanup is refused.'
        }
        foreach ($directory in $Directories) {
            $prefix = $directory.TrimEnd('\') + '\'
            if (($process.ExecutablePath -and $process.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) -or
                ($process.CommandLine -and $process.CommandLine.IndexOf($directory, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
                throw "Build is in use by process $($process.Name) (PID $($process.ProcessId)); cleanup is refused."
            }
        }
    }
}

function Assert-Unlocked([string[]]$Paths) {
    foreach ($path in $Paths) {
        try {
            $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $stream.Dispose()
        }
        catch { throw "A build file is locked or read-only; cleanup is refused: $path" }
    }
}

$previous = Get-SafeBuild $PreviousBuild
$next = Get-SafeBuild $NewBuild
if ($previous.Path -ieq $next.Path -or $previous.StartedAt -ge $next.StartedAt) { throw 'PreviousBuild must precede a different new build.' }
$eligible = foreach ($directory in Get-ChildItem -LiteralPath $builds -Directory) {
    if ($directory.Name -notmatch '^\d{8}-\d{6}$') { continue }
    $metadata = Join-Path $directory.FullName 'build-info.json'
    if (-not (Test-Path -LiteralPath $metadata -PathType Leaf)) { continue }
    try { $candidate = Get-SafeBuild $directory.FullName }
    catch { continue }
    if ($candidate.StartedAt -lt $next.StartedAt) { $candidate }
}
$immediate = $eligible | Sort-Object StartedAt -Descending | Select-Object -First 1
if (-not $immediate -or $immediate.Path -ine $previous.Path) { throw 'PreviousBuild is not the immediate previous successful full Release build.' }
$oldFiles = @(Get-SafeTree $previous.Path)
$null = @(Get-SafeTree $next.Path)
Assert-NoBuildProcesses @($previous.Path, $next.Path)
Assert-Unlocked @($oldFiles.FullName)

$userData = @('config.ini', 'global_cfg.ini', 'history_traffic.dat', 'history_traffic.dat.bak',
    'plugins\Battery.dll.ini', 'plugins\PowerMonPlugin.dll.ini', 'plugins\WeatherPro.ini')
$records = foreach ($relative in $userData + @('build-info.json', 'error.log', 'builtin-startup-check.log')) {
    $source = Join-Path $previous.Path $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $destination = Join-Path $next.Path $relative
    $migrate = $relative -in $userData
    $exists = Test-Path -LiteralPath $destination -PathType Leaf
    if ($migrate -and $exists -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) {
        throw "User data differs in the new build; review/merge before retirement: $relative"
    }
    if ($migrate -and $exists) { Assert-Unlocked @($destination) }
    [pscustomobject]@{ RelativePath = $relative; SHA256 = $hash; Migrate = $migrate; AlreadyPresent = $exists }
}
$backupRoot = Join-Path $artifacts 'backups'
if (Test-Path -LiteralPath $backupRoot) {
    if ((Get-Item -LiteralPath $backupRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Backup directory must not be a link.' }
}
$backup = Join-Path $backupRoot ('retired-' + (Split-Path $previous.Path -Leaf) + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$plan = [ordered]@{ Mode = $(if ($Execute) { 'Execute' } else { 'Plan' }); PreviousBuild = $previous.Path; NewBuild = $next.Path; BackupDirectory = $backup; Files = @($records) }
if (-not $Execute) { $plan | ConvertTo-Json -Depth 4; return }

# The caller runs -Execute only after validating the new build. No processes are stopped.
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($record in $records) {
    $source = Join-Path $previous.Path $record.RelativePath
    $saved = Join-Path $backup $record.RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $saved
    if ((Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash -ne $record.SHA256) { throw 'Backup readback failed; previous build was retained.' }
    if ($record.Migrate -and -not $record.AlreadyPresent) {
        $destination = Join-Path $next.Path $record.RelativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        [IO.File]::Copy($saved, $destination, $false) # Never overwrite a file created since the plan.
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $record.SHA256) { throw 'Migration readback failed; previous build was retained.' }
    }
}
foreach ($record in $records) {
    if ((Get-FileHash -LiteralPath (Join-Path $previous.Path $record.RelativePath) -Algorithm SHA256).Hash -ne $record.SHA256) {
        throw 'Previous build data changed during backup; previous build was retained.'
    }
    if ($record.Migrate -and (Get-FileHash -LiteralPath (Join-Path $next.Path $record.RelativePath) -Algorithm SHA256).Hash -ne $record.SHA256) {
        throw 'New build data changed during migration; previous build was retained.'
    }
}
$plan | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'retirement.json') -Encoding UTF8
Assert-NoBuildProcesses @($previous.Path, $next.Path)
Assert-Unlocked @($oldFiles.FullName)
# Revalidate the final absolute recursive-delete target, including every child.
$validated = Get-SafeBuild $previous.Path
if ($validated.Path -ine $previous.Path -or $validated.Path -ieq $next.Path) { throw 'Cleanup target changed; previous build was retained.' }
$null = @(Get-SafeTree $validated.Path)
Remove-Item -LiteralPath $validated.Path -Recurse -Force
Write-Host "Previous build retired. User data backup: $backup"
