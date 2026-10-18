[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('All', 'Native', 'Media')][string]$Component = 'All',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$buildZone = [TimeZoneInfo]::FindSystemTimeZoneById('Taipei Standard Time')
$buildStartedAt = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $buildZone)
$buildVersion = $buildStartedAt.ToString('yyMMdd-HHmm', [Globalization.CultureInfo]::InvariantCulture)
$versionNumbers = @([int]$buildStartedAt.ToString('yy', [Globalization.CultureInfo]::InvariantCulture), [int]$buildStartedAt.ToString('MMdd', [Globalization.CultureInfo]::InvariantCulture), [int]$buildStartedAt.ToString('HHmm', [Globalization.CultureInfo]::InvariantCulture), 0)
$fileVersion = $versionNumbers -join '.'
$assemblyVersion = $versionNumbers[0..2] -join '.'
$appDirectory = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $productRoot ("artifacts\builds\" + $buildStartedAt.ToString('yyyyMMdd-HHmmss')) }
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $appDirectory.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output directory must be inside product artifacts.' }
$logDirectory = Join-Path $productRoot 'artifacts\logs'
$installation = $null
if ($OutputDirectory) {
    New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
} else {
    # No -Force: even builds started in the same second must not overwrite each other.
    New-Item -ItemType Directory -Path $appDirectory | Out-Null
}
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
if ((Test-Path -LiteralPath (Join-Path $appDirectory 'Runtime')) -or (Test-Path -LiteralPath (Join-Path $appDirectory 'UI/Microsoft.ui.xaml.dll')) -or (Test-Path -LiteralPath (Join-Path $appDirectory 'Media/coreclr.dll')) -or (Test-Path -LiteralPath (Join-Path $appDirectory 'UI/coreclr.dll'))) {
    throw 'The old runtime layout cannot be updated in place. Build into a new directory.'
}
function Invoke-BuildCommand([string]$Name, [string]$Program, [string[]]$Arguments) {
    $logPath = Join-Path $logDirectory "$Name.log"
    Write-Host "$Name ..."
    & $Program @Arguments *> $logPath
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $logPath -Tail 35 | Write-Host
        throw "$Name failed (exit $LASTEXITCODE). Full log: $logPath"
    }
    Write-Host "$Name passed. Log: $logPath"
}
Push-Location $productRoot
try {
    & (Join-Path $PSScriptRoot 'check-boundaries.ps1')
    if ($Component -in @('All', 'Native')) {
        $versionDirectory = Join-Path $productRoot 'Native\Obj\ProductVersion'
        New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
        $versionHeader = @(
            '#pragma once',
            ('#define TBW_PRODUCT_VERSION_W L"' + $buildVersion + '"'),
            ('#define TBW_PRODUCT_VERSION_A "' + $buildVersion + '"'),
            ('#define TBW_PRODUCT_VERSION_NUMERIC ' + ($versionNumbers -join ','))
        ) -join "`r`n"
        [IO.File]::WriteAllText((Join-Path $versionDirectory 'ProductVersion.generated.h'), $versionHeader + "`r`n", [Text.Encoding]::ASCII)
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 Build Tools is required. See .vsconfig.' }
        $installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.ATLMFC -property installationPath
        if (-not $installation) { throw 'Missing VS 2022 MFC. Run scripts/install-build-components.ps1.' }
        $msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
        Invoke-BuildCommand 'restore-native' 'dotnet' @('restore', 'scripts/NativePackages.csproj', '--verbosity', 'minimal')
        Invoke-BuildCommand 'build-native' $msbuild @('Native/TaskBarWidgets.sln', '/m:1', "/p:Configuration=$Configuration", '/p:Platform=x64', "/p:TaskBarWidgetsBuildVersion=$buildVersion", '/v:minimal', '/nologo')
        $nativeOutput = Join-Path $productRoot "Native\Bin\x64\$Configuration"
        foreach ($file in @('TaskBarWidgets.exe', 'OpenHardwareMonitorApi.dll')) {
            Copy-Item -LiteralPath (Join-Path $nativeOutput $file) -Destination $appDirectory -Force
        }
        Copy-Item -LiteralPath 'Native/OpenHardwareMonitorApi/LibreHardwareMonitorLib.dll' -Destination $appDirectory -Force
        if (Test-Path -LiteralPath 'Native/TrafficMonitor/language') {
            $languageDirectory = Join-Path $appDirectory 'language'
            New-Item -ItemType Directory -Path $languageDirectory -Force | Out-Null
            foreach ($name in @('English.ini','Simplified_Chinese.ini')) {
                Copy-Item -LiteralPath (Join-Path 'Native/TrafficMonitor/language' $name) -Destination $languageDirectory -Force
            }
        }
        # App-local release runtime avoids an installer requirement on the acceptance machine.
        $redistRoot = Join-Path $installation 'VC\Redist\MSVC'
        $redistVersion = Get-ChildItem -LiteralPath $redistRoot -Directory | Where-Object Name -Match '^\d' | Sort-Object Name -Descending | Select-Object -First 1
        if ($Configuration -eq 'Release' -and $redistVersion) {
            foreach ($runtime in @('Microsoft.VC143.CRT', 'Microsoft.VC143.MFC')) {
                $runtimePath = Join-Path $redistVersion.FullName "x64\$runtime"
                if (Test-Path -LiteralPath $runtimePath) {
                    # The Unicode native executable imports mfc140u.dll; the ANSI MFC build is unused.
                    Get-ChildItem -LiteralPath $runtimePath -Filter '*.dll' | Where-Object { $runtime -ne 'Microsoft.VC143.MFC' -or $_.Name -eq 'mfc140u.dll' } | Copy-Item -Destination $appDirectory -Force
                }
            }
        }
    }
    if ($Component -in @('All', 'Media')) {
        $managedVersion = @("-p:Version=$assemblyVersion", "-p:FileVersion=$fileVersion", "-p:InformationalVersion=$buildVersion", '-p:IncludeSourceRevisionInInformationalVersion=false')
        $managedVersion += @('-p:RollForward=LatestPatch')
        Invoke-BuildCommand 'publish-media' 'dotnet' (@('publish', 'Media/TaskBarWidgets.Media.csproj', '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false', '-o', (Join-Path $appDirectory 'Media'), '-p:BuildInParallel=false', '--verbosity', 'minimal') + $managedVersion)
        Invoke-BuildCommand 'publish-ui' 'dotnet' (@('publish', 'UI/TaskBarWidgets.UI.csproj', '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false', '-o', (Join-Path $appDirectory 'UI'), '-p:BuildInParallel=false', '--verbosity', 'minimal') + $managedVersion)
        # WinUI loads native DLLs from its own process directory, not the native owner's directory.
        $uiInstallation = $installation
        if (-not $uiInstallation) {
            $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
            if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ redistributable files are required for WinUI packaging.' }
            $uiInstallation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        }
        if (-not $uiInstallation) { throw 'Visual Studio C++ redistributable files are required for WinUI packaging.' }
        $uiRedist = Get-ChildItem -LiteralPath (Join-Path $uiInstallation 'VC\Redist\MSVC') -Directory | Where-Object Name -Match '^\d' | Sort-Object Name -Descending | Select-Object -First 1
        if (-not $uiRedist) { throw 'Missing Visual C++ redistributable directory for WinUI.' }
        $uiCrt = Join-Path $uiRedist.FullName 'x64\Microsoft.VC143.CRT'
        if (-not (Test-Path -LiteralPath $uiCrt)) { throw "Missing WinUI C++ runtime: $uiCrt" }
        Get-ChildItem -LiteralPath $uiCrt -Filter '*.dll' | Copy-Item -Destination (Join-Path $appDirectory 'UI') -Force
        foreach ($uiFile in @('TaskBarWidgets.UI.exe', 'TaskBarWidgets.UI.pri', 'App.xbf', 'Assets\TaskBarWidgets.ico')) {
            if (-not (Test-Path -LiteralPath (Join-Path $appDirectory "UI\$uiFile") -PathType Leaf)) { throw "Missing packaged WinUI file: $uiFile" }
        }
        $uiGenerated = Join-Path $productRoot "UI\bin\$Configuration\net8.0-windows10.0.22621.0\win-x64"
        foreach ($resource in @('TaskBarWidgets.UI.pri', 'App.xbf')) {
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $uiGenerated $resource) -Algorithm SHA256).Hash
            $packageHash = (Get-FileHash -LiteralPath (Join-Path $appDirectory "UI\$resource") -Algorithm SHA256).Hash
            if ($sourceHash -ne $packageHash) { throw "Packaged WinUI resource differs from the current product build: $resource" }
        }
    }
    Copy-Item -LiteralPath 'licenses' -Destination $appDirectory -Recurse -Force
    Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.md', 'ACCEPTANCE.md' -Destination $appDirectory -Force
    Copy-Item -LiteralPath 'LICENSE', 'NOTICE' -Destination $appDirectory -Force
    if ($Component -eq 'All') {
        & (Join-Path $PSScriptRoot 'check-runtime-layout.ps1') -Directory $appDirectory
        foreach ($required in @('TaskBarWidgets.exe', 'OpenHardwareMonitorApi.dll', 'LibreHardwareMonitorLib.dll', 'Media\TaskBarWidgets.Media.exe', 'UI\TaskBarWidgets.UI.exe', 'UI\TaskBarWidgets.UI.pri', 'UI\App.xbf')) {
            if (-not (Test-Path -LiteralPath (Join-Path $appDirectory $required))) { throw "Missing packaged file: $required" }
        }
    }
    [ordered]@{
        buildStartedAt = $buildStartedAt.ToString('o')
        version = $buildVersion
        fileVersion = $fileVersion
        buildCompletedAt = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $buildZone).ToString('o')
        timeZone = 'Asia/Taipei'
        configuration = $Configuration
        component = $Component
        runtimeDeployment = 'system-shared'
        dotnetFramework = 'Microsoft.WindowsDesktop.App 8.0 (x64), latest installed patch'
        windowsAppRuntime = '2.5.1 (x64) or compatible later 2.x'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $appDirectory 'build-info.json') -Encoding UTF8
    if ($Component -eq 'All') {
        Set-Content -LiteralPath (Join-Path $productRoot 'artifacts\latest-build.txt') -Value $appDirectory -Encoding UTF8
    }
    Write-Host "Build complete: $appDirectory. Desktop acceptance is pending; application was not launched."
}
finally { Pop-Location }
