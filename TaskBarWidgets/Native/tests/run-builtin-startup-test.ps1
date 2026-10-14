# Reuses real Release provider objects and resources; no product/UI launch.
$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSScriptRoot
$productRoot = Split-Path -Parent $nativeRoot
$objectsDirectory = Join-Path $nativeRoot 'TrafficMonitor/x64/Release'
$output = Join-Path $nativeRoot 'Bin/Tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$objects = @(Get-ChildItem -LiteralPath $objectsDirectory -Filter 'Builtins-*.obj')
$resource = Join-Path $objectsDirectory 'TrafficMonitor.res'
if ($objects.Count -lt 10 -or -not (Test-Path -LiteralPath $resource)) { throw 'Run a full Release build first.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.ATLMFC -property installationPath
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$executable = Join-Path $output 'BuiltinStartupTests.exe'
$response = Join-Path $output 'builtin-startup.rsp'
$arguments = @('/nologo', '/EHsc', '/MD', '/D_AFXDLL', '/DUNICODE', '/D_UNICODE', '/std:c++20', '/utf-8',
    ('/Fe:"{0}"' -f $executable), ('/Fo:"{0}"' -f (Join-Path $output 'BuiltinStartupTests.obj')),
    ('"{0}"' -f (Join-Path $PSScriptRoot 'BuiltinStartupTests.cpp')))
$arguments += $objects | ForEach-Object { '"{0}"' -f $_.FullName }
$arguments += @(('"{0}"' -f $resource), '/link', '/LTCG', '/SUBSYSTEM:CONSOLE', 'Shlwapi.lib', 'Comctl32.lib', 'windowsapp.lib')
[IO.File]::WriteAllText($response, ($arguments -join " "), [Text.Encoding]::UTF8)
$command = 'call "{0}" >nul && cl @"{1}"' -f $vcvars, $response
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'Built-in startup harness build failed.' }
& $executable
if ($LASTEXITCODE -ne 0) { throw "Production built-in startup/resource regression failed ($LASTEXITCODE)." }
