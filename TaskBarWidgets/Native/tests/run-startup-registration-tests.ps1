$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $nativeRoot 'Bin\Tests\Startup'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio 2022 C++ tools are required.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$source = Join-Path $PSScriptRoot 'StartupRegistrationTests.cpp'
$executable = Join-Path $output 'StartupRegistrationTests.exe'
$object = Join-Path $output 'StartupRegistrationTests.obj'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /MD /D_AFXDLL /DUNICODE /D_UNICODE /Fe:"{1}" /Fo:"{2}" "{3}" && "{1}"' -f $vcvars, $executable, $object, $source
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Startup task definition regressions failed ($LASTEXITCODE)." }
