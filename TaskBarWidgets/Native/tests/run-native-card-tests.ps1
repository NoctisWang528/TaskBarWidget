param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [ValidateSet('Regression', 'Measure', 'SurfaceIsolation')][string]$Mode = 'Regression'
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($BuildDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $source 'build-info.json'))) { throw 'A complete product build is required.' }
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$entry = Get-Content -LiteralPath (Join-Path $productRoot 'Native/TrafficMonitor/NativeMediaHost.cpp') -Raw
$panel = [regex]::Match($entry, '(?s)void Panel\(unsigned long long gesture = 0\)\s*\{(?<body>.*?)\n    \}')
if (-not $panel.Success -or $panel.Groups['body'].Value -notmatch 'NativeMediaHost::ToggleCards\(' -or
    $panel.Groups['body'].Value -match 'Command\(L"togglePanel"\)') {
    throw 'The real taskbar Panel entry must dispatch directly to native cards, not the removed WinUI card route.'
}
$media = Get-Content -LiteralPath (Join-Path $productRoot 'Media/App.xaml.cs') -Raw
if ($media.Contains('_ui.SendAsync("togglePanel"')) { throw 'A card entry still starts the retired WinUI card path.' }
Write-Host 'Taskbar/tray card entry dispatch guards passed.'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$msbuild = Join-Path $installation 'MSBuild/Current/Bin/MSBuild.exe'
& $msbuild (Join-Path $PSScriptRoot 'NativeCardsTests.vcxproj') /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /nologo
if ($LASTEXITCODE) { throw 'Native card probe build failed.' }
$probe = Join-Path $source 'NativeCardsProbe.exe'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Bin/Tests/Cards/NativeCardsProbe.exe') -Destination $probe -Force
$launch = @{
    FilePath = $probe
    WorkingDirectory = $source
    WindowStyle = 'Hidden'
    PassThru = $true
    RedirectStandardOutput = Join-Path $source "native-card-$Mode.log"
    RedirectStandardError = Join-Path $source "native-card-$Mode-error.log"
}
if ($Mode -eq 'Measure') {
    $measurements = Join-Path $productRoot 'artifacts/measurements'
    New-Item -ItemType Directory -Path $measurements -Force | Out-Null
    $measurement = Join-Path $measurements ("cards-" + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.json')
    $launch.ArgumentList = @('--measure-cards', ('"{0}"' -f $measurement))
    Write-Host "Isolated synthetic card measurements: $measurement"
} elseif ($Mode -eq 'SurfaceIsolation') {
    $launch.ArgumentList = @('--surface-isolation')
}
$process = Start-Process @launch
if (-not $process.WaitForExit(45000)) { Stop-Process -Id $process.Id; throw 'Isolated card probe did not exit in 45 seconds.' }
$process.Refresh()
Get-Content -LiteralPath $launch.RedirectStandardOutput
Get-Content -LiteralPath $launch.RedirectStandardError
if ($process.ExitCode) { throw "Native card probe failed ($($process.ExitCode))." }
Remove-Item -LiteralPath $probe
