[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$installerRoot = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installerRoot 'vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio 2022 Build Tools first, using TaskBarWidgets/.vsconfig.' }
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -property installationPath
if (-not $installation) { throw 'Visual Studio 2022 Build Tools was not found.' }
$config = Join-Path $productRoot '.vsconfig'
$arguments = 'modify --installPath "{0}" --config "{1}" --passive --norestart' -f $installation, $config
# Windows will request administrator confirmation. No automatic restart is requested.
$process = Start-Process -FilePath (Join-Path $installerRoot 'setup.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -notin @(0, 3010)) { throw "Visual Studio component installation failed: $($process.ExitCode)" }
if ($process.ExitCode -eq 3010) { Write-Warning 'Installation completed; Windows reports a restart is needed before building.' }
