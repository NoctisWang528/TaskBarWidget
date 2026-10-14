[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Alias("IncludePlugins")][switch]$IncludeBuiltinSettings,
    [string]$DestinationDirectory
)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$destination = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $DestinationDirectory -ParameterName 'DestinationDirectory'
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $destination.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Migration destination must be inside product artifacts.' }
$source = [IO.Path]::GetFullPath($SourceDirectory)
if ($source.TrimEnd('\') -eq $destination.TrimEnd('\')) { throw 'Source and destination must differ.' }
$config = Join-Path $source 'config.ini'
if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { throw "Source configuration not found: $config" }
$targetExecutable = Join-Path $destination 'TaskBarWidgets.exe'
if (Test-Path -LiteralPath $targetExecutable) {
    try { $lockCheck = [IO.File]::Open($targetExecutable, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $lockCheck.Dispose() }
    catch { throw 'Destination program is running or locked; exit it or choose a new output directory.' }
}
if ($IncludeBuiltinSettings -and (Test-Path -LiteralPath (Join-Path $source 'plugins')) -and (Test-Path -LiteralPath (Join-Path $destination 'plugins'))) {
    throw 'Built-in settings already exist in the destination; preserve them or choose a new candidate directory.'
}
$destinationConfig = Join-Path $destination 'config.ini'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
if (Test-Path -LiteralPath $destinationConfig) {
    $backup = $destinationConfig + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
    Copy-Item -LiteralPath $destinationConfig -Destination $backup
    Write-Host "Existing product configuration backed up: $backup"
}
Copy-Item -LiteralPath $config -Destination $destinationConfig -Force
# The imported portable copy remains separate from the daily installation.
[IO.File]::WriteAllText((Join-Path $destination 'global_cfg.ini'), "[config]`r`nportable_mode = true`r`n", [Text.UTF8Encoding]::new($false))
if ($IncludeBuiltinSettings -and (Test-Path -LiteralPath (Join-Path $source 'plugins'))) {
    $settingsDirectory = Join-Path $destination 'plugins'
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    foreach ($name in @('Battery.dll.ini', 'PowerMonPlugin.dll.ini', 'WeatherPro.ini')) {
        $sourceSettings = Join-Path (Join-Path $source 'plugins') $name
        if (Test-Path -LiteralPath $sourceSettings -PathType Leaf) {
            Copy-Item -LiteralPath $sourceSettings -Destination (Join-Path $settingsDirectory $name)
        }
    }
}
Write-Host 'Monitor settings imported into the test output directory. Source files were not changed; no skins or startup registrations were imported.'
