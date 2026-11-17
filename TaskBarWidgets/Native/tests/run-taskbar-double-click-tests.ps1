$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $nativeRoot 'Bin\Tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio 2022 C++ tools are required.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$source = Join-Path $PSScriptRoot 'TaskbarDoubleClickTests.cpp'
$executable = Join-Path $output 'TaskbarDoubleClickTests.exe'
$object = Join-Path $output 'TaskbarDoubleClickTests.obj'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /Fe:"{1}" /Fo:"{2}" "{3}" user32.lib && "{1}"' -f $vcvars, $executable, $object, $source
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Taskbar double-click regression tests failed ($LASTEXITCODE)." }
Write-Host 'Taskbar double-click compatibility, timing, coordinates and application path checks passed.'
