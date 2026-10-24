[CmdletBinding()]
param([string]$OutputDirectory, [string]$ReleaseDirectory, [switch]$AllowDirtyLocal, [string]$CompatibilityDirectory)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $productRoot
$source = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
& (Join-Path $PSScriptRoot 'check-runtime-layout.ps1') -Directory $source
$info = Get-Content -LiteralPath (Join-Path $source 'build-info.json') -Raw | ConvertFrom-Json
if ($info.component -ne 'All' -or $info.configuration -ne 'Release' -or $info.version -notmatch '^\d{6}-\d{4}$') {
    throw 'A complete versioned Release build is required.'
}
$version = $info.version
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the release source commit.' }
$sourceChanges = @(& git -c core.quotepath=false -C $repoRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect package source state.' }
if ($sourceChanges.Count -and -not $AllowDirtyLocal) { throw 'Commit the intended source changes before packaging a release, or explicitly request a local dirty delivery.' }
$releaseRoot = if ($ReleaseDirectory) { [IO.Path]::GetFullPath($ReleaseDirectory) } else { Join-Path $productRoot "artifacts\releases\$version" }
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $releaseRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release staging must be inside product artifacts.' }
if (Test-Path -LiteralPath $releaseRoot) { throw 'Release staging already exists; inspect it before packaging again.' }
$packageName = "TaskBarWidgets-$version-win-x64"
$stage = Join-Path $releaseRoot $packageName
New-Item -ItemType Directory -Path $stage | Out-Null
# Only runtime roots are selected. Diagnostics and acceptance screenshots remain local.
$files = @('TaskBarWidgets.exe','TaskBarWidgets.Updater.exe','build-info.json','THIRD-PARTY-NOTICES.md')
$files += @(Get-ChildItem -LiteralPath $source -File -Filter '*.dll' | Select-Object -ExpandProperty Name)
foreach ($relative in $files) { Copy-Item -LiteralPath (Join-Path $source $relative) -Destination (Join-Path $stage $relative) }
foreach ($directory in @('Media','UI','language','licenses')) {
    $from = Join-Path $source $directory
    if (-not (Test-Path -LiteralPath $from -PathType Container)) { throw "Missing runtime directory: $directory" }
    foreach ($file in Get-ChildItem -LiteralPath $from -File -Recurse) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked package inputs are not allowed.' }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ($file.Extension -in @('.log','.pdb','.dmp','.mdmp','.etl','.bak') -or $file.Name -match '\.log($|\.)' -or
            $file.Name -match '^(.*-check\.json|template-probe\.json)' -or
            $relative -match '(^|\\)(Data|data|database|.*\.WebView2|WebView2|EBWebView|User Data|Cache|cache|logs|Logs|backups|migration|secrets)(\\|$)' -or
            $file.Extension -in @('.db','.sqlite','.sqlite3') -or $file.Name -match '(-wal|-shm)$') { continue }
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'NOTICE') -Destination (Join-Path $stage 'NOTICE')
& (Join-Path $PSScriptRoot 'check-runtime-layout.ps1') -Directory $stage
@"
TaskBarWidgets $version — Windows 11 x64

完整解压后运行 TaskBarWidgets.exe。不要单独运行 Media/UI 内的辅助程序。
本包使用系统共享运行库，需安装：
- .NET 8 Desktop Runtime x64（安装最新 8.0 补丁；无需 SDK）：https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0
- Windows App Runtime 2.5.1 x64 或兼容的更新 2.x：https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads
启动时检查依赖，缺失或无法加载时显示下载入口，不会自动安装。环境就绪后重新启动程序。
需要管理员权限；媒体与歌词卡片由原生渲染，不再需要 WebView2 Runtime。请先退出旧版或其他任务栏监测工具。
此发行 ZIP 不包含个人配置；更新会保留已有数据，首次启动使用默认设置。运行后的目录可能含个人配置、记录和凭据，分享前请检查。升级前请自行备份配置与流量历史。

源码、用法、许可证与来源：https://github.com/NoctisWang528/TaskBarWidget
本地构建的来源状态详见 release-manifest.json；工作区变更不代表已发布到远端。
本项目使用了 TrafficMonitor、TaskbarLyrics、AF-Media-Bar 的部分代码。
详见 LICENSE、THIRD-PARTY-NOTICES.md 和 licenses/。
"@ | Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding utf8
$sourceFiles = @(& git -c core.quotepath=false -C $repoRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate package source state.' }
$sourceHashes = @($sourceFiles | Sort-Object -Unique | ForEach-Object {
    $path = Join-Path $repoRoot $_
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
})
$manifest = [ordered]@{
    version = $version; sourceState = $(if ($sourceChanges.Count) { 'working-tree' } else { 'commit' })
    baselineCommit = $commit; sourceCommit = $(if ($sourceChanges.Count) { $null } else { $commit })
    sourceUrl = $(if ($sourceChanges.Count) { $null } else { "https://github.com/NoctisWang528/TaskBarWidget/tree/$commit" })
    localOnly = [bool]$AllowDirtyLocal; sourceRecordedAtPackaging = [DateTimeOffset]::UtcNow.ToString('o')
    workingTreeStatus = $sourceChanges; sourceFiles = $sourceHashes; architecture = 'win-x64'
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8
$forbidden = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object {
    [IO.Path]::GetRelativePath($stage, $_.FullName) -match '^(Data|data|secrets|migration|backups|logs|Logs)(\\|$)' -or
    $_.Name -match '^(ChineseConverter\.dll|config\.ini|global_cfg\.ini|history_traffic\..*|media-settings\.json)$' -or
    $_.Name -match '\.log($|\.)|(-wal|-shm)$' -or
    $_.Extension -in @('.log','.pdb','.dmp','.mdmp','.db','.sqlite','.sqlite3')
})
if ($forbidden.Count) { throw 'Personal data, diagnostics or a removed dependency was found in the release package.' }
$programFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\','/'); bytes = $_.Length;
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ protocolVersion = 1; version = $version; architecture = 'win-x64'; files = $programFiles } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'app-files.json') -Encoding utf8
$inventoryHash = (Get-FileHash -LiteralPath (Join-Path $stage 'app-files.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$zip = Join-Path $releaseRoot ($packageName + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$sha  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii
[ordered]@{ protocolVersion = 1; version = $version; architecture = 'win-x64'; assetName = [IO.Path]::GetFileName($zip);
    size = (Get-Item -LiteralPath $zip).Length; sha256 = $sha; fileManifestSha256 = $inventoryHash;
    requirements = [ordered]@{ dotnetDesktop = '8.0.0'; windowsAppRuntime = '2.5.1.0'; windowsBuild = 22000 } } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseRoot 'update.json') -Encoding utf8
Write-Host "ZIP: $zip"
Write-Host "SHA256: $sha"
# Never declare a package ready based on hashes alone. Use the actual updater
# implementation in isolated pwsh processes, optionally including an older build.
$validatorScript = Join-Path $PSScriptRoot 'test-release-package.ps1'
foreach ($validator in @($source, $CompatibilityDirectory) | Where-Object { $_ } | Select-Object -Unique) {
    & pwsh -NoProfile -File $validatorScript -ReleaseDirectory $releaseRoot -ValidatorDirectory $validator
    if ($LASTEXITCODE -ne 0) { throw 'The complete ZIP failed updater validation; do not publish this package.' }
}
