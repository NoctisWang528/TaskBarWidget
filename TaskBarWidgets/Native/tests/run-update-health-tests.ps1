[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testOutput = Join-Path $productRoot 'Native\Bin\Tests'
$objects = Join-Path $productRoot 'Native\Obj\UpdateHealthTests'
$evidence = Join-Path $productRoot ('artifacts\update-health-tests\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput,$objects,$evidence -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'VS 2022 x64 C++ tools are required.' }
$exe = Join-Path $testOutput 'UpdateHealthTests.exe'
$source = Join-Path $PSScriptRoot 'UpdateHealthTests.cpp'
$object = Join-Path $objects 'UpdateHealthTests.obj'
$batch = Join-Path $objects 'build.cmd'
$commands = @('@echo off',
    ('call "' + (Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat') + '" >nul'),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl /nologo /EHsc /std:c++20 /MT /W4 /utf-8 /DUNICODE /D_UNICODE /Fe"' + $exe + '" /Fo"' + $object + '" "' + $source + '" windowsapp.lib shell32.lib user32.lib ole32.lib bcrypt.lib /link /MANIFEST:EMBED /MANIFESTUAC:"level=''asInvoker'' uiAccess=''false''"'),
    'exit /b %errorlevel%')
[IO.File]::WriteAllLines($batch,$commands,[Text.Encoding]::Default)
$log = Join-Path $objects 'build.log'
& $env:ComSpec /d /c $batch *> $log
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 40 | Write-Host; throw "Update health test build failed: $log" }
$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$manifestTool = Get-ChildItem -LiteralPath $sdkBin -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64\mt.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $manifestTool) { throw 'Windows SDK manifest inspection tool is required.' }
$manifestPath = Join-Path $evidence 'fixture.manifest'
& $manifestTool -nologo "-inputresource:$exe;#1" "-out:$manifestPath"
if ($LASTEXITCODE -ne 0) { throw 'Native handshake fixture manifest missing.' }
$manifestXml = [xml](Get-Content -LiteralPath $manifestPath -Raw)
if ($manifestXml.SelectSingleNode("//*[local-name()='requestedExecutionLevel']").level -ne 'asInvoker') { throw 'Native handshake fixture must never request elevation.' }
$install = [IO.Path]::GetFullPath((Join-Path $evidence 'install-é-ı')).TrimEnd('\')
$key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($install.ToUpperInvariant()))).ToLowerInvariant()
& $exe $evidence $key
if ($LASTEXITCODE -ne 0) { throw "Update health tests failed. Evidence: $evidence" }
Write-Host "Native update health checks passed. Evidence: $evidence"
