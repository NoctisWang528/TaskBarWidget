[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $productRoot 'artifacts\validation\winui-host'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& dotnet publish (Join-Path $productRoot 'UI.Validation\TaskBarWidgets.UI.Validation.csproj') -c $Configuration -r win-x64 --self-contained true -o $output -p:BuildInParallel=false --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'WinUI validation host publish failed.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ runtime files are required for WinUI validation.' }
$redist = Get-ChildItem -LiteralPath (Join-Path $installation 'VC\Redist\MSVC') -Directory | Where-Object Name -Match '^\d' | Sort-Object Name -Descending | Select-Object -First 1
if (-not $redist) { throw 'Missing Visual C++ runtime files.' }
Get-ChildItem -LiteralPath (Join-Path $redist.FullName 'x64\Microsoft.VC143.CRT') -Filter '*.dll' | Copy-Item -Destination $output -Force
$report = Join-Path $output 'ui-validation.json'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
# This separate host constructs/measures windows without ever activating or showing them.
$process = Start-Process -FilePath (Join-Path $output 'TaskBarWidgets.UI.Validation.exe') -ArgumentList "--result `"$report`"" -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) {
    Stop-Process -Id $process.Id
    throw 'Hidden WinUI validation timed out; its test process was stopped.'
}
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw "WinUI validation failed (exit $($process.ExitCode)); inspect $report if present." }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.success) { throw $result.error }
Write-Host "Hidden WinUI bootstrap/control construction/window geometry checks passed. Report: $report. Settings visual layout remains pending when layoutAvailable is false."
