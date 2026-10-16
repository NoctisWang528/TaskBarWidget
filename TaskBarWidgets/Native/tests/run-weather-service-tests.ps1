param([switch]$LiveNanjing)
$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSScriptRoot
$productRoot = Split-Path -Parent $nativeRoot
$output = Join-Path $productRoot 'artifacts/tests/weather-native'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.ATLMFC -property installationPath
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$objectsDirectory = Join-Path $nativeRoot 'TrafficMonitor/x64/Release'
$resource = Join-Path $objectsDirectory 'TrafficMonitor.res'
$json = Join-Path $objectsDirectory 'Builtins-yyjson.obj'
if (!(Test-Path -LiteralPath $resource) -or !(Test-Path -LiteralPath $json)) { throw 'Run a full native Release build first for the embedded resources and yyjson object.' }
$response = Join-Path $output 'weather.rsp'
$executable = Join-Path $output 'WeatherServiceTests.exe'
$arguments = @('/nologo','/EHsc','/MD','/D_AFXDLL','/DUNICODE','/D_UNICODE','/DTBW_WEATHER_TESTS','/std:c++20','/utf-8',('/Fe:"{0}"' -f $executable),('/Fo:"{0}\\"' -f $output),('/I"{0}"' -f (Join-Path $nativeRoot 'Builtins')),('/I"{0}"' -f (Join-Path $nativeRoot 'Builtins/include')),('"{0}"' -f (Join-Path $PSScriptRoot 'WeatherServiceTests.cpp')))
$arguments += @('Common.cpp','DataManager.cpp','DataApiHefengWeather.cpp','DataApiWeatherComCnSpider.cpp','WeatherService.cpp') | ForEach-Object { '"{0}"' -f (Join-Path $nativeRoot "Builtins/Weather/$_") }
$arguments += @(('"{0}"' -f $json),('"{0}"' -f $resource),'/link','/LTCG','/SUBSYSTEM:CONSOLE','Shlwapi.lib','Comctl32.lib','windowsapp.lib')
[IO.File]::WriteAllText($response, ($arguments -join ' '), [Text.Encoding]::UTF8)
$command = 'call "{0}" >nul && cl @"{1}"' -f $vcvars,$response
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'Weather console harness build failed.' }
$fixture = Join-Path $output ("fixture-" + [Guid]::NewGuid().ToString('N'))
if ($LiveNanjing) { & $executable $fixture --live-nanjing } else { & $executable $fixture }
if ($LASTEXITCODE -ne 0) { throw "Weather provider regressions failed ($LASTEXITCODE)." }
