param()
$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSScriptRoot
$productRoot = Split-Path -Parent $nativeRoot
$output = Join-Path $productRoot 'artifacts/tests/weather-http'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.ATLMFC -property installationPath
if (!$installation) { throw 'Visual Studio 2022 C++ and MFC tools are required.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$response = Join-Path $output 'weather-http.rsp'
$executable = Join-Path $output 'WeatherHttpTransportTests.exe'
$arguments = @('/nologo','/EHsc','/MD','/D_AFXDLL','/DUNICODE','/D_UNICODE','/DTBW_WEATHER_TESTS','/std:c++20','/utf-8',
    ('/Fe:"{0}"' -f $executable),('/Fo:"{0}\\"' -f $output),('/Fd:"{0}"' -f (Join-Path $output 'weather-http.pdb')),
    ('/I"{0}"' -f (Join-Path $nativeRoot 'Builtins')),
    ('"{0}"' -f (Join-Path $PSScriptRoot 'WeatherHttpTransportTests.cpp')),
    ('"{0}"' -f (Join-Path $nativeRoot 'Builtins/Weather/Common.cpp')),
    '/link','/SUBSYSTEM:CONSOLE','winhttp.lib','ws2_32.lib')
[IO.File]::WriteAllText($response, ($arguments -join ' '), [Text.Encoding]::UTF8)
$command = 'call "{0}" >nul && cl @"{1}"' -f $vcvars,$response
Push-Location $output
try {
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw 'Weather HTTP harness build failed.' }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw "Weather HTTP regressions failed ($LASTEXITCODE)." }
} finally { Pop-Location }
