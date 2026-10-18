[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$directory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
. (Join-Path $PSScriptRoot 'shared-runtime-process.ps1')
$report = Join-Path $directory 'media-package-check.json'
$start = New-SharedRuntimeProcess $directory 'Media/TaskBarWidgets.Media.exe' @('--validate-package',$report)
$probe = [Diagnostics.Process]::Start($start)
if (-not $probe.WaitForExit(20000)) { $probe.Kill(); throw 'Isolated media package probe timed out.' }
if ($probe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'Isolated media package probe failed.' }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.success) { throw $result.error }
$prefix = (Get-SystemDotnetRoot).TrimEnd('\') + '\'
if (-not $result.runtimeDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $result.desktopAssembly.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Media did not use the installed shared framework.' }
foreach ($folder in @('Media','UI')) {
    foreach ($forbidden in @('coreclr.dll','hostfxr.dll','System.Private.CoreLib.dll','DirectML.dll','onnxruntime.dll','Wpf.Ui.dll')) {
        if (Test-Path -LiteralPath (Join-Path $directory "$folder/$forbidden")) { throw "Unexpected duplicate/unused dependency: $folder/$forbidden" }
    }
}
if (Get-ChildItem -LiteralPath $directory -Recurse -Filter '*.otf') { throw 'A removed bundled font remains in the package.' }
Write-Host 'Media loaded Core/Desktop from the system shared runtime; headless settings, SQLite and system fonts passed. No normal product/audio was started.'
