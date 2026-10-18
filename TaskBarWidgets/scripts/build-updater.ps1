[CmdletBinding()]
param([string]$OutputDirectory, [switch]$TestMode)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $productRoot 'Native\Bin\x64\Release' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$objectDirectory = Join-Path $productRoot ('Native\Obj\Updater' + $(if ($TestMode) { '-Test' } else { '' }))
New-Item -ItemType Directory -Path $output, $objectDirectory -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'VS 2022 C++ x64 tools are required.' }
$sdkInclude = Join-Path $productRoot '.packages\microsoft.windowsappsdk.runtime\2.5.1\include'
if (-not (Test-Path -LiteralPath (Join-Path $sdkInclude 'WindowsAppSDK-VersionInfo.h'))) { throw 'Restore scripts/NativePackages.csproj before building the updater.' }
$name = if ($TestMode) { 'TaskBarWidgets.Updater.Test.exe' } else { 'TaskBarWidgets.Updater.exe' }
$executable = Join-Path $output $name
$define = if ($TestMode) { '/DTBW_UPDATER_TEST' } else { '' }
$manifestUac = if ($TestMode) { '/MANIFESTUAC:"level=''asInvoker'' uiAccess=''false''"' } else { '/MANIFESTUAC:"level=''requireAdministrator'' uiAccess=''false''"' }
$batch = Join-Path $objectDirectory 'build.cmd'
$yyjson = Join-Path $productRoot 'Native\Builtins\utilities\yyjson\yyjson.c'
$source = Join-Path $productRoot 'Native\Updater\Updater.cpp'
$object = Join-Path $objectDirectory 'yyjson.obj'
$mainObject = Join-Path $objectDirectory 'Updater.obj'
$commands = @(
    '@echo off',
    ('call "' + (Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat') + '" >nul'),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /c /O2 /MT /utf-8 /TC "' + $yyjson + '" /Fo"' + $object + '"'),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /std:c++17 /O2 /MT /EHsc /utf-8 /DUNICODE /D_UNICODE ' + $define + ' /I"' + $sdkInclude + '" /Fo"' + $mainObject + '" "' + $source + '" "' + $object + '" /Fe"' + $executable + '" /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /MANIFEST:EMBED ' + $manifestUac + ' shell32.lib user32.lib advapi32.lib ole32.lib bcrypt.lib'),
    'exit /b %errorlevel%'
)
if ($TestMode) {
    $fakeSource = Join-Path $productRoot 'Native\Updater\FakeHost.cpp'
    $fakeObject = Join-Path $objectDirectory 'FakeHost.obj'
    $fakeExe = Join-Path $output 'TaskBarWidgets.Updater.FakeHost.exe'
    $commands = $commands[0..($commands.Count - 2)] + @(
        'if errorlevel 1 exit /b %errorlevel%',
        ('cl /nologo /std:c++17 /O2 /MT /EHsc /utf-8 /DUNICODE /D_UNICODE /Fo"' + $fakeObject + '" "' + $fakeSource + '" "' + $object + '" /Fe"' + $fakeExe + '" /link /SUBSYSTEM:WINDOWS /MANIFEST:EMBED ' + $manifestUac + ' shell32.lib user32.lib'),
        'exit /b %errorlevel%'
    )
}
[IO.File]::WriteAllLines($batch, $commands, [Text.Encoding]::Default)
$log = Join-Path $objectDirectory 'build.log'
& $env:ComSpec /d /c $batch *> $log
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 60 | Write-Host; throw "Updater build failed. See $log" }
Write-Output $executable
