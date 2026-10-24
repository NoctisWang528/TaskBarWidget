[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$directory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
$executable = Join-Path $directory 'TaskBarWidgets.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing packaged executable: $executable" }

# The test opens the packaged executable as a data file and exercises the seeding rules
# against a scratch directory. It never starts the product. MSBuild resolves the VC
# toolset, so the test does not depend on a vcvars environment.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio 2022 C++ tools are required.' }
$msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
$project = Join-Path $productRoot 'Native\tests\DefaultSeedingTests.vcxproj'
$log = Join-Path (Join-Path $productRoot 'artifacts\logs') 'default-seeding-tests.log'
& $msbuild $project /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo *> $log
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 30 | Write-Host; throw "The factory default seeding test could not be built ($LASTEXITCODE)." }
$testExecutable = Join-Path $productRoot 'Native\Bin\x64\Release\DefaultSeedingTests.exe'
if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) { throw "Missing test executable: $testExecutable" }

$work = Join-Path $productRoot ('artifacts\seeding-test\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    & $testExecutable $executable $work
    if ($LASTEXITCODE -ne 0) { throw "Factory default seeding tests failed ($LASTEXITCODE)." }
    $defaults = [ordered]@{
        'app.default.ini' = 'Native\TrafficMonitor\defaults\config.default.ini'
        'battery.default.ini' = 'Native\TrafficMonitor\defaults\Battery.dll.default.ini'
        'power.default.ini' = 'Native\TrafficMonitor\defaults\PowerMonPlugin.dll.default.ini'
        'weather.default.ini' = 'Native\TrafficMonitor\defaults\WeatherPro.default.ini'
    }
    foreach ($name in $defaults.Keys) {
        $packaged = Join-Path (Join-Path $work 'extracted') $name
        if (-not (Test-Path -LiteralPath $packaged -PathType Leaf)) { throw "The packaged default $name was not extracted." }
        $expected = (Get-FileHash -LiteralPath (Join-Path $productRoot $defaults[$name]) -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath $packaged -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "The packaged default $name differs from its source file." }
    }
    Write-Host 'Packaged factory defaults match their sources and never overwrite an existing configuration.'
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
