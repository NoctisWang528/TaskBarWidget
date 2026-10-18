[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PersonalSourceDirectory,
    [string]$MediaDataDirectory = (Join-Path $env:APPDATA 'TaskBarWidgets'),
    [string]$BuildDirectory
)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-data.ps1')
$clean = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $BuildDirectory -ParameterName 'BuildDirectory'
$personal = [IO.Path]::GetFullPath($PersonalSourceDirectory)
foreach ($path in @($clean, $personal)) { Assert-UnlinkedPath $path }
$info = Get-Content -LiteralPath (Join-Path $clean 'build-info.json') -Raw | ConvertFrom-Json
if ($info.component -ne 'All' -or $info.configuration -ne 'Release' -or $info.version -notmatch '^\d{6}-\d{4}$') { throw 'A complete successful Release build is required.' }
$delivery = Join-Path $productRoot "artifacts\deliveries\$($info.version)"
if (Test-Path -LiteralPath $delivery) { throw 'Delivery already exists; existing packages are never overwritten.' }
foreach ($required in @('config.ini', 'global_cfg.ini', 'history_traffic.dat')) {
    if (-not (Test-Path -LiteralPath (Join-Path $personal $required) -PathType Leaf)) { throw "Missing personal source data: $required" }
}
# The build variant must remain self-contained after being moved to another machine.
if (-not (Test-PortableDataMode (Join-Path $personal 'global_cfg.ini'))) { throw 'Personal source must use portable_mode=true (or a nonzero integer).' }
New-Item -ItemType Directory -Path $delivery | Out-Null
$release = Join-Path $delivery 'release'
# Release is assembled from the clean build before personal files enter the separate build folder.
& (Join-Path $PSScriptRoot 'package-release.ps1') -OutputDirectory $clean -ReleaseDirectory $release -AllowDirtyLocal
$releaseFolder = Join-Path $release "TaskBarWidgets-$($info.version)-win-x64"
$build = Join-Path $delivery 'build'
Copy-Item -LiteralPath $releaseFolder -Destination $build -Recurse
$snapshots = [Collections.Generic.List[object]]::new()
foreach ($relative in @('config.ini', 'global_cfg.ini', 'history_traffic.dat', 'history_traffic.dat.bak',
    'plugins\Battery.dll.ini', 'plugins\PowerMonPlugin.dll.ini', 'plugins\WeatherPro.ini')) {
    $from = Join-Path $personal $relative
    if (Test-Path -LiteralPath $from -PathType Leaf) {
        Assert-UnlinkedPath $from
        $record = Copy-StableDataFile $from (Join-Path $build $relative)
        $record['path'] = $relative
        $snapshots.Add($record)
    }
}
$mediaSources = @((Join-Path $personal 'Data'), [IO.Path]::GetFullPath($MediaDataDirectory)) | Select-Object -Unique
$sqliteTool = $null
foreach ($mediaSource in $mediaSources) {
    if (-not (Test-Path -LiteralPath $mediaSource -PathType Container)) { continue }
    Assert-UnlinkedPath $mediaSource
    $files = @()
    $settings = Join-Path $mediaSource 'media-settings.json'
    if (Test-Path -LiteralPath $settings -PathType Leaf) { $files += Get-Item -LiteralPath $settings }
    $database = Join-Path $mediaSource 'database'
    if (Test-Path -LiteralPath $database -PathType Container) {
        Assert-UnlinkedPath $database
        $files += @(Get-ChildItem -LiteralPath $database -File | Where-Object Extension -In @('.json', '.db', '.sqlite', '.sqlite3'))
    }
    foreach ($file in $files) {
        Assert-UnlinkedPath $file.FullName
        $relative = [IO.Path]::GetRelativePath($mediaSource, $file.FullName)
        $target = Join-Path $build (Join-Path 'Data' $relative)
        if (Test-Path -LiteralPath $target) { continue } # Existing personal Data takes precedence over shared AppData.
        if ($file.Extension -in @('.db', '.sqlite', '.sqlite3')) {
            if (-not $sqliteTool) {
                $toolProject = Join-Path $PSScriptRoot 'SqliteSnapshot\SqliteSnapshot.csproj'
                $toolLog = Join-Path $delivery 'sqlite-tool-build.log'
                & dotnet build $toolProject -c Release --verbosity quiet *> $toolLog
                if ($LASTEXITCODE -ne 0) { throw "SQLite snapshot helper build failed; see $toolLog" }
                $sqliteTool = Join-Path $PSScriptRoot 'SqliteSnapshot\bin\Release\net8.0\SqliteSnapshot.dll'
            }
            & dotnet $sqliteTool $file.FullName $target
            if ($LASTEXITCODE -ne 0) { throw "SQLite snapshot failed: $($file.Name)" }
            $record = [ordered]@{ name = $file.Name; bytes = (Get-Item -LiteralPath $target).Length;
                sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant(); method = 'sqlite-online-backup-integrity-checked' }
        } else { $record = Copy-StableDataFile $file.FullName $target }
        $record['path'] = Join-Path 'Data' $relative
        $snapshots.Add($record)
    }
}
[ordered]@{ version = $info.version; kind = 'personal-portable'; mediaDataPath = 'Data';
    snapshotCreatedAt = [DateTimeOffset]::UtcNow.ToString('o'); sourceUnmodified = $true; snapshots = $snapshots.ToArray() } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $build 'personal-data-manifest.json') -Encoding utf8
@"
TaskBarWidgets $($info.version) — 个人便携版本

完整移动 build 文件夹后运行 TaskBarWidgets.exe。媒体设置和歌词数据库位于 Data/。
本目录含个人设置、历史和插件凭据，请勿作为公开 Release 上传或分享。
Data/ 未包含 WebView 登录信息、Cookie 或运行日志。源安装目录与共享 AppData 未被覆盖。
公开分发请使用相邻 release/ 内的干净 ZIP；源码状态见 release-manifest.json。
"@ | Set-Content -LiteralPath (Join-Path $build 'README.txt') -Encoding utf8
Write-Host "Personal build: $build"
Write-Host "Clean release folder, ZIP and SHA256: $release"
