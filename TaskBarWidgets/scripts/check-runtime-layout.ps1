param([Parameter(Mandatory = $true)][string]$Directory)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath (Join-Path $Directory 'Runtime')) { throw 'Lightweight packages must not contain a private Runtime directory.' }
$languages = @(Get-ChildItem -LiteralPath (Join-Path $Directory 'language') -File | Select-Object -ExpandProperty Name | Sort-Object)
if (($languages -join ',') -ne 'English.ini,Simplified_Chinese.ini') { throw 'Unexpected native language resources.' }
foreach ($component in @('Media','UI')) {
    $config = Get-Content -LiteralPath (Join-Path $Directory "$component/TaskBarWidgets.$component.runtimeconfig.json") -Raw | ConvertFrom-Json
    $frameworks = @($config.runtimeOptions.frameworks) + @($config.runtimeOptions.framework)
    $frameworks = @($frameworks | Where-Object { $null -ne $_ })
    if (-not ($frameworks | Where-Object name -EQ 'Microsoft.NETCore.App')) { throw "$component does not declare shared .NET." }
    if ($component -eq 'Media' -and -not ($frameworks | Where-Object name -EQ 'Microsoft.WindowsDesktop.App')) { throw 'Media must declare the desktop framework.' }
    foreach ($framework in $frameworks) {
        if ($framework.version -ne '8.0.0') { throw "$component must request .NET 8 with compatible patch updates." }
    }
    if ($config.runtimeOptions.rollForward -ne 'LatestPatch') { throw "$component must allow .NET 8 patch updates." }
    foreach ($file in @('coreclr.dll','System.Private.CoreLib.dll','hostfxr.dll','DirectML.dll','onnxruntime.dll','Wpf.Ui.dll','Microsoft.ui.xaml.dll','Microsoft.WindowsAppRuntime.dll')) {
        if (Test-Path -LiteralPath (Join-Path $Directory "$component/$file")) { throw "Unexpected bundled shared runtime or unused dependency: $component/$file" }
    }
    $areaPath = [IO.Path]::GetFullPath((Join-Path $Directory $component)).TrimEnd('\')
    foreach ($resource in Get-ChildItem -LiteralPath $areaPath -Recurse -File | Where-Object { ($_.Name -like '*.resources.dll' -or $_.Name -like '*.mui') -and $_.DirectoryName -ne $areaPath }) {
        if ($resource.Directory.Name -notmatch '^(en(?:-.+)?|zh|zh-Hans|zh-CN|zh-SG)$') { throw "Unexpected translation: $($resource.Name)" }
    }
}
foreach ($file in @('TaskBarWidgets.Updater.exe','Media/TaskBarWidgets.Media.dll','UI/TaskBarWidgets.UI.dll','UI/TaskBarWidgets.UI.pri','UI/App.xbf','UI/Microsoft.WindowsAppRuntime.Bootstrap.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Directory $file) -PathType Leaf)) { throw "Missing required application component: $file" }
}
Write-Host 'Shared .NET / Windows App Runtime package layout verified; no bundled runtime.'
