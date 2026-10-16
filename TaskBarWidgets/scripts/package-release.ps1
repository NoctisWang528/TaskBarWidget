[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $productRoot
$source = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
$info = Get-Content -LiteralPath (Join-Path $source 'build-info.json') -Raw | ConvertFrom-Json
if ($info.component -ne 'All' -or $info.configuration -ne 'Release' -or $info.version -notmatch '^\d{6}-\d{4}$') {
    throw 'A complete versioned Release build is required.'
}
$version = $info.version
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the release source commit.' }
if (& git -C $repoRoot status --porcelain --untracked-files=normal) { throw 'Commit the intended source changes before packaging a release.' }
$releaseRoot = Join-Path $productRoot "artifacts\releases\$version"
if (Test-Path -LiteralPath $releaseRoot) { throw 'Release staging already exists; inspect it before packaging again.' }
$packageName = "TaskBarWidgets-$version-win-x64"
$privateIni = @('WeatherPro.ini', 'Battery.dll.ini', 'PowerMonPlugin.dll.ini')
$stage = Join-Path $releaseRoot $packageName
New-Item -ItemType Directory -Path $stage | Out-Null
# Only runtime roots are selected. Diagnostics and acceptance screenshots remain local.
$files = @('TaskBarWidgets.exe','build-info.json','THIRD-PARTY-NOTICES.md')
$files += @(Get-ChildItem -LiteralPath $source -File -Filter '*.dll' | Select-Object -ExpandProperty Name)
foreach ($relative in $files) { Copy-Item -LiteralPath (Join-Path $source $relative) -Destination (Join-Path $stage $relative) }
foreach ($directory in @('Media','UI','language','licenses')) {
    $from = Join-Path $source $directory
    if (-not (Test-Path -LiteralPath $from -PathType Container)) { throw "Missing runtime directory: $directory" }
    foreach ($file in Get-ChildItem -LiteralPath $from -File -Recurse) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked package inputs are not allowed.' }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ($file.Name -in $privateIni -or $file.Extension -in @('.log','.pdb','.dmp','.mdmp','.etl','.bak') -or $file.Name -match '\.log($|\.)' -or
            $file.Name -match '^(config\.ini|global_cfg\.ini|history_traffic\.|media-settings\.json|ui-.*check\.json|template-probe\.json)' -or
            $relative -match '(^|\\)(WebView2|User Data|Cache|logs|backups|ui-layout-check|ui-heatmap-check)(\\|$)') { continue }
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'NOTICE') -Destination (Join-Path $stage 'NOTICE')
@"
TaskBarWidgets $version — Windows 11 x64

完整解压后运行 TaskBarWidgets.exe。不要单独运行 Media/UI 内的辅助程序。
需要管理员权限和 Microsoft Edge WebView2 Runtime；请先退出旧版或其他任务栏监测工具。
此包不包含个人配置，首次启动使用默认设置。升级前请自行备份配置与流量历史。

源码、用法、许可证与来源：https://github.com/NoctisWang528/TaskBarWidget/tree/$version
本项目使用了 TrafficMonitor、TaskbarLyrics、AF-Media-Bar 的部分代码。
详见 LICENSE、THIRD-PARTY-NOTICES.md 和 licenses/。
"@ | Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding utf8
$manifest = [ordered]@{ version = $version; sourceCommit = $commit; sourceUrl = "https://github.com/NoctisWang528/TaskBarWidget/tree/$version"; architecture = 'win-x64' }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8
$forbidden = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object {
    $_.Name -in $privateIni -or $_.Name -match '^(config\.ini|global_cfg\.ini|history_traffic\..*|media-settings\.json|ChineseConverter\.dll)$' -or $_.Name -match '\.log($|\.)' -or $_.Extension -in @('.log','.pdb','.dmp','.mdmp','.db','.sqlite')
})
if ($forbidden.Count) { throw 'Personal data, diagnostics or a removed dependency was found in the release package.' }
$zip = Join-Path $releaseRoot ($packageName + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$sha  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii
Write-Host "ZIP: $zip"
Write-Host "SHA256: $sha"
