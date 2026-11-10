param()
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = Join-Path $productRoot 'artifacts/peripheral-provider-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$batch = Join-Path $output 'build.cmd'
$commands = @('@echo off', ('call "' + (Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat') + '" >nul'),
    ('cl /nologo /O2 /MT /c /TC "' + (Join-Path $productRoot 'Native/Builtins/utilities/yyjson/yyjson.c') + '" /Fo"' + (Join-Path $output 'yyjson.obj') + '"'),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /O2 /MT /EHsc /std:c++17 /utf-8 "' + (Join-Path $PSScriptRoot 'PeripheralProviderFixture.cpp') + '" "' + (Join-Path $output 'yyjson.obj') + '" /Fo"' + (Join-Path $output 'fixture.obj') + '" /Fe:"' + (Join-Path $output 'TaskBarWidgets.PeripheralProbe.exe') + '"'),
    'exit /b %errorlevel%')
[IO.File]::WriteAllLines($batch, $commands, [Text.Encoding]::Default)
& $env:ComSpec /d /c $batch
if ($LASTEXITCODE) { throw 'Synthetic peripheral pipe peer did not build.' }
& dotnet run --project (Join-Path $productRoot 'tests/Peripheral.Provider.Tests/Peripheral.Provider.Tests.csproj') -c Release -p:BuildInParallel=false -- $output
if ($LASTEXITCODE) { throw 'Peripheral provider process contract failed.' }
