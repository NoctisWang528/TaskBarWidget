[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PortableDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$productRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactPrefix = (Join-Path $productRoot 'artifacts').TrimEnd('\') + '\'

function Get-UnlinkedLocalPath([string]$Path) {
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    if ($full -notmatch '^[A-Za-z]:\\' -or $full.Substring(2).Contains(':')) { throw 'Use a local drive path without alternate streams.' }
    $cursor = $full
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked inputs or output paths are not allowed.' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}
function ConvertTo-NsisString([string]$Value) {
    if ($Value.Contains("`r") -or $Value.Contains("`n") -or $Value.Contains('"')) { throw 'Unsupported quote or line break in an installer path.' }
    return $Value.Replace('$', '$$')
}
function Assert-PackagePath([string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains('\') -or
        $Relative -match '(^/|[:*?"<>|\x00-\x1f]|(^|/)\.{1,2}(/|$)|/$)') { throw 'Unsafe package inventory path.' }
    if ($Relative -match '(?i)(^|/)(Data|secrets|credentials|auth|User Data|Cache|Logs|backups|migration|\.git|\.packages|TestResults)(/|$)' -or
        $Relative -match '(?i)(^|/)(\.env[^/]*|auth\.json|config\.ini|global_cfg\.ini|media-settings\.json|weather-appkey\.bin|history_traffic\.[^/]*)$' -or
        $Relative -match '(?i)(\.log($|\.)|\.(pdb|dmp|mdmp|etl|db|sqlite|sqlite3|pem|pfx|key|bak)$|(-wal|-shm)$)') {
        throw ('Personal data or diagnostics are not permitted: ' + $Relative)
    }
}

$portable = Get-UnlinkedLocalPath $PortableDirectory
$output = Get-UnlinkedLocalPath $OutputDirectory
$compiler = Get-UnlinkedLocalPath $CompilerPath
if (-not $output.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output must remain under product artifacts.' }
if (Test-Path -LiteralPath $output) { throw 'Installer output directory already exists; no files were overwritten.' }
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'NSIS makensis.exe was not found.' }
$inventoryPath = Join-Path $portable 'app-files.json'
$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$info = Get-Content -LiteralPath (Join-Path $portable 'build-info.json') -Raw | ConvertFrom-Json
if ($inventory.protocolVersion -ne 1 -or $inventory.architecture -ne 'win-x64' -or
    $info.configuration -ne 'Release' -or $info.component -ne 'All' -or
    $info.version -ne $inventory.version -or $info.version -notmatch '^\d{6}-\d{4}$' -or
    $info.fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'A complete, version-matched Windows x64 Release package is required.' }
$expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($record in $inventory.files) {
    $relative = [string]$record.path
    Assert-PackagePath $relative
    if ($relative -eq 'app-files.json' -or $expected.ContainsKey($relative) -or $record.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$record.bytes -lt 0) {
        throw 'Duplicate or invalid package inventory record.'
    }
    $full = Get-UnlinkedLocalPath (Join-Path $portable $relative)
    if (-not $full.StartsWith($portable.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escaped its root.' }
    $file = Get-Item -LiteralPath $full -Force
    if ($file.PSIsContainer -or $file.Length -ne [long]$record.bytes -or
        (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne $record.sha256) { throw ('Package file failed verification: ' + $relative) }
    $expected.Add($relative, [ordered]@{ path = $relative; bytes = $file.Length; sha256 = ([string]$record.sha256).ToLowerInvariant() })
}
foreach ($required in @('TaskBarWidgets.exe','TaskBarWidgets.Updater.exe','TaskBarWidgets.PeripheralProbe.exe','Media/TaskBarWidgets.Media.dll','UI/TaskBarWidgets.UI.dll','LICENSE','THIRD-PARTY-NOTICES.md','README.txt')) {
    if (-not $expected.ContainsKey($required)) { throw ('Required package input is missing: ' + $required) }
}
$inventoryFile = Get-Item -LiteralPath $inventoryPath -Force
$expected.Add('app-files.json', [ordered]@{ path = 'app-files.json'; bytes = $inventoryFile.Length; sha256 = (Get-FileHash -LiteralPath $inventoryPath -Algorithm SHA256).Hash.ToLowerInvariant() })
$actual = @(Get-ChildItem -LiteralPath $portable -Recurse -File -Force)
if ($actual.Count -ne $expected.Count) { throw 'The package contains missing or extra files.' }
foreach ($file in $actual) {
    $relative = [IO.Path]::GetRelativePath($portable, $file.FullName).Replace('\','/')
    Assert-PackagePath $relative
    if (-not $expected.ContainsKey($relative)) { throw ('Unexpected file in package: ' + $relative) }
    $null = Get-UnlinkedLocalPath $file.FullName
}
# The shared checker accepts both runtimeconfig.framework and .frameworks.
# Its optional-property reads predate strict mode; keep that scope local.
& { Set-StrictMode -Off; & (Join-Path $PSScriptRoot 'check-runtime-layout.ps1') -Directory $portable }

New-Item -ItemType Directory -Path $output | Out-Null
$setupPath = Join-Path $output ("TaskBarWidgets-$($info.version)-win-x64-setup.exe")
$includePath = Join-Path $output 'package-files.nsh'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('; -*- coding: utf-8 -*-')
$lines.Add('; Generated after file inventory validation. Do not edit.')
$lines.Add('!define PRODUCT_VERSION "' + $info.version + '"')
$lines.Add('!define FILE_VERSION "' + $info.fileVersion + '"')
$lines.Add('!define SETUP_FILE "' + (ConvertTo-NsisString $setupPath) + '"')
$lines.Add('!define PACKAGE_LICENSE "' + (ConvertTo-NsisString (Join-Path $portable 'LICENSE')) + '"')
$lines.Add('!define PRODUCT_ICON "' + (ConvertTo-NsisString (Join-Path $productRoot 'assets/app-icon.ico')) + '"')
$totalBytes = ($actual | Measure-Object Length -Sum).Sum
$lines.Add('!define INSTALLED_KB ' + [math]::Ceiling($totalBytes / 1KB))
$lines.Add('!macro InstallProgramFiles')
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in @($expected.Keys | Sort-Object)) {
    $windowsPath = $relative.Replace('/','\')
    $parent = [IO.Path]::GetDirectoryName($windowsPath)
    $suffix = if ($parent) { '\' + (ConvertTo-NsisString $parent) } else { '' }
    $lines.Add('  SetOutPath "$INSTDIR' + $suffix + '"')
    $lines.Add('  File "' + (ConvertTo-NsisString (Join-Path $portable $windowsPath)) + '"')
    while ($parent) { $null = $directories.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent) }
}
$lines.Add('!macroend')
$lines.Add('!macro CheckUninstallPayload')
foreach ($directory in @($directories | Sort-Object { $_.Length })) {
    $lines.Add('  StrCpy $0 "$INSTDIR\' + (ConvertTo-NsisString $directory) + '"')
    $lines.Add('  Call un.CheckPayloadPath')
}
foreach ($relative in @($expected.Keys | Sort-Object)) {
    $lines.Add('  StrCpy $0 "$INSTDIR\' + (ConvertTo-NsisString $relative.Replace('/','\')) + '"')
    $lines.Add('  Call un.CheckPayloadPath')
}
$lines.Add('!macroend')
$lines.Add('!macro UninstallProgramFiles')
foreach ($relative in @($expected.Keys | Sort-Object)) { $lines.Add('  Delete "$INSTDIR\' + (ConvertTo-NsisString $relative.Replace('/','\')) + '"') }
foreach ($directory in @($directories | Sort-Object { $_.Length } -Descending)) { $lines.Add('  RMDir "$INSTDIR\' + (ConvertTo-NsisString $directory) + '"') }
$lines.Add('!macroend')
[IO.File]::WriteAllLines($includePath, $lines, [Text.UTF8Encoding]::new($true))
$log = Join-Path $output 'compile.log'
& $compiler /INPUTCHARSET UTF8 /NOCONFIG /V3 ("/DPACKAGE_INCLUDE=$includePath") (Join-Path $productRoot 'installer/TaskBarWidgets.nsi') *> $log
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    Get-Content -LiteralPath $log -Tail 30 | Write-Host
    throw 'NSIS installer compilation failed.'
}
$setupFile = Get-Item -LiteralPath $setupPath
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    version = $info.version; architecture = 'win-x64'; artifact = $setupFile.Name; bytes = $setupFile.Length; sha256 = $setupHash
    personalDataIncluded = $false; payloadFiles = $expected.Count; sourceInventorySha256 = $expected['app-files.json'].sha256
    files = @($expected.Values | Sort-Object { $_.path })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'installer-manifest.json') -Encoding utf8
($setupHash + '  ' + $setupFile.Name) | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Installer compiled from $($expected.Count) verified program files: $setupPath"
Write-Host 'Setup was not run. Verify extracted payload hashes before sharing.'
