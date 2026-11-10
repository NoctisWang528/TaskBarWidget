[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$RunSelfTests
)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Per-output object directory: independent of the main product's shared build trees.
$objectDirectory = Join-Path $output '.peripheral-probe-obj'
New-Item -ItemType Directory -Path $objectDirectory -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'VS 2022 C++ x64 tools are required.' }
$source = Join-Path $productRoot 'Native\PeripheralProbe'
$hidapi = Join-Path $productRoot 'Dependencies\hidapi'
$yyjson = Join-Path $productRoot 'Native\Builtins\utilities\yyjson'
$executable = Join-Path $output 'TaskBarWidgets.PeripheralProbe.exe'
$batch = Join-Path $objectDirectory 'build.cmd'
$commands = @(
    '@echo off',
    ('call "' + (Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat') + '" >nul'),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /c /O2 /Gy /MT /utf-8 /TC /DHID_API_NO_EXPORT_DEFINE /I"' + (Join-Path $hidapi 'hidapi') + '" "' + (Join-Path $hidapi 'windows\hid.c') + '" /Fo"' + (Join-Path $objectDirectory 'hid.obj') + '"'),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /c /O2 /MT /utf-8 /TC "' + (Join-Path $yyjson 'yyjson.c') + '" /Fo"' + (Join-Path $objectDirectory 'yyjson.obj') + '"'),
    'if errorlevel 1 exit /b %errorlevel%'
)
foreach ($unit in @('Protocol.cpp', 'ExtraKeyboard.cpp', 'KeyboardAdvanced.cpp', 'HeadsetController.cpp', 'ExtraMouse.cpp', 'Special.cpp', 'Probe.cpp', 'tests\SelfTests.cpp')) {
    $object = Join-Path $objectDirectory ([IO.Path]::GetFileNameWithoutExtension($unit) + '.obj')
    $commands += ('cl /nologo /c /std:c++17 /O2 /Gy /MT /EHsc /W4 /utf-8 /DUNICODE /D_UNICODE /DHID_API_NO_EXPORT_DEFINE /I"' + (Join-Path $hidapi 'hidapi') + '" "' + (Join-Path $source $unit) + '" /Fo"' + $object + '"')
    $commands += 'if errorlevel 1 exit /b %errorlevel%'
}
$objects = @('hid', 'yyjson', 'Protocol', 'ExtraKeyboard', 'KeyboardAdvanced', 'HeadsetController', 'ExtraMouse', 'Special', 'Probe', 'SelfTests') | ForEach-Object { '"' + (Join-Path $objectDirectory ($_ + '.obj')) + '"' }
$commands += ('link /nologo ' + ($objects -join ' ') + ' /OUT:"' + $executable + '" /SUBSYSTEM:CONSOLE /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /OPT:REF /OPT:ICF /INCREMENTAL:NO /MANIFEST:EMBED /MANIFESTUAC:"level=''asInvoker'' uiAccess=''false''" setupapi.lib hid.lib cfgmgr32.lib ole32.lib advapi32.lib bcrypt.lib ws2_32.lib bthprops.lib')
$commands += 'exit /b %errorlevel%'
[IO.File]::WriteAllLines($batch, $commands, [Text.Encoding]::Default)
$log = Join-Path $objectDirectory 'build.log'
& $env:ComSpec /d /c $batch *> $log
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 80 | Write-Host; throw "Peripheral probe build failed. See $log" }
if ($RunSelfTests) {
    & $executable --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Peripheral probe offline selftests failed.' }
}
Write-Output $executable
