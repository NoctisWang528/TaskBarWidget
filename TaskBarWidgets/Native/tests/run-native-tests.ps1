$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $nativeRoot 'Bin\Tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio 2022 C++ tools are required.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$source = Join-Path $PSScriptRoot 'NativeSettingsValidationTests.cpp'
$executable = Join-Path $output 'NativeSettingsValidationTests.exe'
$object = Join-Path $output 'NativeSettingsValidationTests.obj'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /Fe:"{1}" /Fo:"{2}" "{3}" && "{1}"' -f $vcvars, $executable, $object, $source
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Native validation tests failed ($LASTEXITCODE)." }
$source = Join-Path $PSScriptRoot 'NativeMediaTests.cpp'
$executable = Join-Path $output 'NativeMediaTests.exe'
$object = Join-Path $output 'NativeMediaTests.obj'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /Fe:"{1}" /Fo:"{2}" "{3}" && "{1}"' -f $vcvars, $executable, $object, $source
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Native media frame/layout regression tests failed ($LASTEXITCODE)." }
$source = Join-Path $PSScriptRoot 'PluginIniOptionsTests.cpp'
$executable = Join-Path $output 'PluginIniOptionsTests.exe'
$object = Join-Path $output 'PluginIniOptionsTests.obj'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /Fe:"{1}" /Fo:"{2}" "{3}" && "{1}"' -f $vcvars, $executable, $object, $source
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Plugin option preservation regression tests failed ($LASTEXITCODE)." }
$source = Join-Path $PSScriptRoot 'HistoryTrafficFileTests.cpp'
$executable = Join-Path $output 'HistoryTrafficFileTests.exe'
$object = Join-Path $output 'HistoryTrafficFileTests.obj'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /MD /D_AFXDLL /DUNICODE /D_UNICODE /I"{5}" /Fe:"{1}" /Fo:"{2}" "{3}" && "{1}" "{4}"' -f $vcvars, $executable, $object, $source, $output, (Join-Path $nativeRoot 'include')
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Native history file regression tests failed ($LASTEXITCODE)." }
Write-Host 'Native numeric, color, expiry, media-state and 12-week traffic-history regression tests passed.'

$source = Join-Path $PSScriptRoot 'CompositionResizeTests.cpp'
$executable = Join-Path $output 'CompositionResizeTests.exe'
$object = Join-Path $output 'CompositionResizeTests.obj'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /Fe:"{1}" /Fo:"{2}" "{3}" && "{1}"' -f $vcvars, $executable, $object, $source
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Native composition resize regression failed ($LASTEXITCODE)." }

$source = Join-Path $PSScriptRoot 'TaskbarRowLayoutTests.cpp'
$executable = Join-Path $output 'TaskbarRowLayoutTests.exe'
$object = Join-Path $output 'TaskbarRowLayoutTests.obj'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /Fe:"{1}" /Fo:"{2}" "{3}" && "{1}"' -f $vcvars, $executable, $object, $source
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Native taskbar shared-column geometry regression failed ($LASTEXITCODE)." }

$source = Join-Path $PSScriptRoot 'NativeMediaOffscreenTests.cpp'
$painter = Join-Path $nativeRoot 'TrafficMonitor\NativeMediaPainter.cpp'
$executable = Join-Path $output 'NativeMediaOffscreenTests.exe'
$offscreenOutput = Join-Path $output 'Offscreen'
$command = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /W4 /MD /Fe:"{1}" "{2}" "{3}" && "{1}" "{4}"' -f $vcvars, $executable, $source, $painter, $offscreenOutput
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Native media offscreen rendering validation failed ($LASTEXITCODE)." }
Write-Host "Native media offscreen images: $offscreenOutput"

$mediaHost = Get-Content -LiteralPath (Join-Path $nativeRoot 'TrafficMonitor\NativeMediaHost.cpp') -Raw
if ($mediaHost -match 'PeekNamedPipe|ExecuteScript|NavigateToString|CoreWebView2|chrome\.webview') {
    throw 'Native media host still contains WebView2 or idle pipe polling code.'
}
if ($mediaHost -notmatch 'WaitForMultipleObjects' -or $mediaHost -notmatch 'SECURITY_IDENTIFICATION') {
    throw 'Native media pipe must use cancelable waits and current-user pipe security.'
}
$taskbar = Get-Content -LiteralPath (Join-Path $nativeRoot 'TrafficMonitor\TaskBarDlg.cpp') -Raw
if ($taskbar -match 'SetTimer\(1243, 33' -or $taskbar -match 'nIDEvent == 1243') {
    throw 'Taskbar still contains the unconditional 33 ms media tick.'
}
