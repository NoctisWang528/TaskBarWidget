#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$ValidatorDirectory
)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$validator = [IO.Path]::GetFullPath($ValidatorDirectory)
$assemblyPath = Join-Path $validator 'Media/TaskBarWidgets.Media.dll'
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw 'Published Media validator is missing.' }
# Run in a fresh pwsh process when checking another version: assemblies with the
# same identity must not be reused from an earlier validation in this process.
if ([AppDomain]::CurrentDomain.GetAssemblies().GetName().Name -contains 'TaskBarWidgets.Media') {
    throw 'Run package validation in a fresh pwsh process.'
}
$checkRoot = Join-Path $productRoot ('artifacts/package-checks/' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $checkRoot 'stage'
New-Item -ItemType Directory -Path $checkRoot | Out-Null
$result = [ordered]@{ success = $false; validator = $assemblyPath; validatorSha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant(); stage = $stage }
try {
    # Load only the trusted published assembly. Never invoke App/Main, start the
    # normal product, install anything, or execute code from the downloaded ZIP.
    $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
    $packageType = $assembly.GetType('TaskBarWidgets.Media.Updates.UpdatePackage', $true)
    $metadataType = $assembly.GetType('TaskBarWidgets.Media.Updates.UpdateMetadata', $true)
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $parse = $packageType.GetMethod('ParseJson', $flags).MakeGenericMethod($metadataType)
    $metadataBytes = [IO.File]::ReadAllBytes((Join-Path $release 'update.json'))
    $metadata = $parse.Invoke($null, [object[]](, $metadataBytes))
    if ($metadata.ProtocolVersion -ne 1 -or $metadata.Architecture -ne 'win-x64' -or
        $metadata.Version -notmatch '^\d{6}-\d{4}$' -or
        $metadata.AssetName -cne "TaskBarWidgets-$($metadata.Version)-win-x64.zip") {
        throw 'Release metadata or ZIP name is inconsistent.'
    }
    $zip = Join-Path $release $metadata.AssetName
    $result['version'] = $metadata.Version
    $result['package'] = $zip
    if ((Get-Item -LiteralPath $zip).Length -ne $metadata.Size) { throw 'ZIP size differs from update.json.' }
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums = [IO.File]::ReadAllText((Join-Path $release 'SHA256SUMS.txt')).Trim()
    if ($sha -ne $metadata.Sha256 -or $sums -cne "$sha  $($metadata.AssetName)") {
        throw 'ZIP hash differs from update.json or SHA256SUMS.txt.'
    }
    $result['packageSha256'] = $sha
    $token = [Threading.CancellationToken]::None
    # This is the exact download path: ZIP directory checks, path protection,
    # manifest parsing, bounded extraction and every file's size/SHA256.
    $task = $packageType.GetMethod('ExtractAndVerify', $flags).Invoke($null, [object[]]@([string]$zip, [string]$stage, $metadata.PSObject.BaseObject, [Threading.CancellationToken]$token))
    $task.GetAwaiter().GetResult() | Out-Null
    # Exercise the recovery/install preparation path too, against persisted files.
    $task = $packageType.GetMethod('VerifyStage', $flags).Invoke($null, [object[]]@([string]$stage, $metadata.PSObject.BaseObject, [Threading.CancellationToken]$token))
    $task.GetAwaiter().GetResult() | Out-Null
    $manifest = Get-Content -LiteralPath (Join-Path $stage 'app-files.json') -Raw | ConvertFrom-Json
    $result['fileCount'] = $manifest.files.Count
    $result['success'] = $true
    Write-Host "Full ZIP extraction and persisted-stage verification passed: $($metadata.Version), $($manifest.files.Count) files. Validator: $assemblyPath"
}
catch {
    $failure = $_.Exception
    while ($failure.InnerException) { $failure = $failure.InnerException }
    $result['error'] = $failure.Message
    throw
}
finally {
    $report = Join-Path $checkRoot 'result.json'
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding utf8
    Write-Host "Package verification report: $report"
}
