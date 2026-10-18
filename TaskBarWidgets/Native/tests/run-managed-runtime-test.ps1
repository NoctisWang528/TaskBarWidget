param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$directory = & (Join-Path $productRoot 'scripts/resolve-app-directory.ps1') -Directory $OutputDirectory
$testOutput = Join-Path $productRoot 'Native/Bin/Tests'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ tools required.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$source = Join-Path $PSScriptRoot 'ManagedRuntimeLaunchTests.cpp'
$exe = Join-Path $testOutput 'ManagedRuntimeLaunchTests.exe'
$obj = Join-Path $testOutput 'ManagedRuntimeLaunchTests.obj'
$include = Join-Path $productRoot '.packages/microsoft.windowsappsdk.runtime/2.5.1/include'
$compatibility = Join-Path $productRoot 'Native/TrafficMonitor/SharedRuntime.compat.manifest'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /I"{5}" /Fe:"{1}" /Fo:"{2}" "{3}" windowsapp.lib /link /MANIFEST:EMBED /MANIFESTINPUT:"{6}" && "{1}" "{4}"' -f $vcvars,$exe,$obj,$source,$directory,$include,$compatibility
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'Native shared runtime verification failed.' }
$result = Get-Content -LiteralPath (Join-Path $directory 'native-runtime-check.json') -Raw | ConvertFrom-Json
. (Join-Path $productRoot 'scripts/shared-runtime-process.ps1')
$prefix = (Get-SystemDotnetRoot).TrimEnd('\') + '\'
if (-not $result.success -or -not $result.runtimeDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Native launch did not select the system shared runtime.' }
