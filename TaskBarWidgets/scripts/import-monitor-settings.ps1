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
$destinationData = Join-Path $destination 'Data'
if ($IncludeBuiltinSettings -and (Test-Path -LiteralPath (Join-Path $source 'plugins')) -and (Test-Path -LiteralPath (Join-Path $destinationData 'config'))) {
    throw 'Built-in settings already exist in the destination; preserve them or choose a new candidate directory.'
}
$destinationConfig = Join-Path $destinationData 'config\app.ini'
New-Item -ItemType Directory -Path (Split-Path -Parent $destinationConfig) -Force | Out-Null
if (Test-Path -LiteralPath $destinationConfig) {
    $backup = $destinationConfig + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
    Copy-Item -LiteralPath $destinationConfig -Destination $backup
    Write-Host "Existing product configuration backed up: $backup"
}
Copy-Item -LiteralPath $config -Destination $destinationConfig -Force
if ($IncludeBuiltinSettings -and (Test-Path -LiteralPath (Join-Path $source 'plugins'))) {
    $settingsDirectory = Join-Path $destinationData 'config'
    foreach ($pair in @(@('Battery.dll.ini', 'battery.ini'), @('PowerMonPlugin.dll.ini', 'power.ini'), @('WeatherPro.ini', 'weather.ini'))) {
        $sourceSettings = Join-Path (Join-Path $source 'plugins') $pair[0]
        if (Test-Path -LiteralPath $sourceSettings -PathType Leaf) {
            Copy-Item -LiteralPath $sourceSettings -Destination (Join-Path $settingsDirectory $pair[1])
        }
    }
}
Write-Host 'Monitor settings imported into the test output directory. Source files were not changed; no skins or startup registrations were imported.'
