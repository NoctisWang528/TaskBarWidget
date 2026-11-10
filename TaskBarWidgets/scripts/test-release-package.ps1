#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$ValidatorDirectory,
    [switch]$Portable,
    [string]$LegacyJobFixture
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
    if ($LegacyJobFixture) {
        # Inspect the released creator without invoking InstallAsync (which would
        # launch the real application). Serialize with its actual anonymous type.
        $creator = $assembly.GetType('TaskBarWidgets.Media.Integration.UpdateInstallBridge', $true).GetMethod('InstallAsync', [Reflection.BindingFlags]'Instance,NonPublic')
        $attribute = $creator.GetCustomAttributesData() | Where-Object { $_.AttributeType.FullName -eq 'System.Runtime.CompilerServices.AsyncStateMachineAttribute' }
        $machine = $attribute.ConstructorArguments[0].Value
        $il = $machine.GetMethod('MoveNext', [Reflection.BindingFlags]'Instance,NonPublic').GetMethodBody().GetILAsByteArray()
        $opcodes = @{}
        foreach ($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) {
            $opcode = $field.GetValue($null); $opcodes[[int]($opcode.Value -band 65535)] = $opcode
        }
        $constructor = $null; $protocol = $null; $protocolOffset = $null
        for ($at = 0; $at -lt $il.Length;) {
            $offset = $at; $code = [int]$il[$at++]
            if ($code -eq 254) { $code = 65024 + [int]$il[$at++] }
            $opcode = $opcodes[$code]
            if ($null -eq $opcode) { throw "Unknown IL opcode at $offset." }
            $length = switch ($opcode.OperandType.ToString()) {
                'InlineNone' { 0 }; 'ShortInlineBrTarget' { 1 }; 'ShortInlineI' { 1 }; 'ShortInlineVar' { 1 }
                'InlineVar' { 2 }; 'InlineI8' { 8 }; 'InlineR' { 8 }
                'InlineSwitch' { 4 + 4 * [BitConverter]::ToInt32($il, $at) }; default { 4 }
            }
            if ($opcode.Name -match '^ldc\.i4\.([0-8])$') { $protocol = [int]$Matches[1]; $protocolOffset = $offset }
            elseif ($opcode.Name -eq 'ldc.i4') { $protocol = [BitConverter]::ToInt32($il, $at); $protocolOffset = $offset }
            elseif ($opcode.Name -eq 'ldc.i4.s') { $protocol = [int]$il[$at]; $protocolOffset = $offset }
            if ($opcode.Name -eq 'newobj') {
                $candidate = $assembly.ManifestModule.ResolveMethod([BitConverter]::ToInt32($il, $at))
                $parameters = $candidate.GetParameters()
                if ($parameters.Count -gt 0 -and $parameters[0].Name -eq 'protocolVersion') { $constructor = $candidate; break }
            }
            $at += $length
        }
        $expected = 'protocolVersion,installRoot,stageRoot,workRoot,dataRoot,version,fileManifestSha256,requirements,nonce,nativePid,mediaPid,uiPid'
        if ($null -eq $constructor -or $protocol -ne 2 -or ($constructor.GetParameters().Name -join ',') -cne $expected) {
            throw 'Released Media job creator differs from the supported protocol-2 fixture; inspect its IL and data migration before testing.'
        }
        $fixture = Get-Content -LiteralPath $LegacyJobFixture -Raw | ConvertFrom-Json
        $arguments = @($constructor.GetParameters() | ForEach-Object {
            if ($_.Name -eq 'protocolVersion') { $protocol }
            else { [Text.Json.JsonSerializer]::Deserialize(($fixture.($_.Name) | ConvertTo-Json -Depth 10 -Compress), [type]$_.ParameterType, [Text.Json.JsonSerializerOptions]::new([Text.Json.JsonSerializerDefaults]::Web)) }
        })
        $oldJob = $constructor.Invoke([object[]]$arguments)
        $jobJson = [Text.Json.JsonSerializer]::Serialize($oldJob, $oldJob.GetType(), [Text.Json.JsonSerializerOptions]::new([Text.Json.JsonSerializerDefaults]::Web))
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($LegacyJobFixture), $jobJson)
        $result['legacyJob'] = @{ protocolVersion = $protocol; protocolIlOffset = $protocolOffset; constructorIlOffset = $offset; fields = $expected; creator = $constructor.DeclaringType.Name; jobPath = [IO.Path]::GetFullPath($LegacyJobFixture) }
        Write-Host "Released Media job creator: protocol $protocol at IL $protocolOffset; fixture serialized with its actual job type."
    }
    $packageType = $assembly.GetType('TaskBarWidgets.Media.Updates.UpdatePackage', $true)
    $metadataType = $assembly.GetType('TaskBarWidgets.Media.Updates.UpdateMetadata', $true)
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $parse = $packageType.GetMethod('ParseJson', $flags).MakeGenericMethod($metadataType)
    $metadataName = if ($Portable) { 'portable-update.json' } else { 'update.json' }
    $metadataBytes = [IO.File]::ReadAllBytes((Join-Path $release $metadataName))
    $metadata = $parse.Invoke($null, [object[]](, $metadataBytes))
    $suffix = if ($Portable) { '-portable' } else { '' }
    if ($metadata.ProtocolVersion -ne 1 -or $metadata.Architecture -ne 'win-x64' -or
        $metadata.Version -notmatch '^\d{6}-\d{4}$' -or
        $metadata.AssetName -cne "TaskBarWidgets-$($metadata.Version)-win-x64$suffix.zip") {
        throw 'Release metadata or ZIP name is inconsistent.'
    }
    $zip = Join-Path $release $metadata.AssetName
    $result['version'] = $metadata.Version
    $result['package'] = $zip
    if ((Get-Item -LiteralPath $zip).Length -ne $metadata.Size) { throw 'ZIP size differs from update.json.' }
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums = @([IO.File]::ReadAllLines((Join-Path $release 'SHA256SUMS.txt')) | Where-Object { $_ })
    if (@($sums | Where-Object { $_ -cnotmatch '^[0-9a-fA-F]{64} [ *][^/\\]+$' }).Count) { throw 'Invalid checksum manifest.' }
    $matching = @($sums | Where-Object { $_.Substring(66) -ieq $metadata.AssetName })
    if ($sha -ne $metadata.Sha256 -or $matching.Count -ne 1 -or $matching[0] -cne "$sha  $($metadata.AssetName)") {
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
