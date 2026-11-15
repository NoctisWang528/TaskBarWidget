[CmdletBinding()]
param(
    [switch]$Check,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Ref
)
$ErrorActionPreference = 'Stop'
$syncScript = Join-Path $PSScriptRoot 'sync_vigil_watt.py'
$syncArguments = @($syncScript)
if ($Check) { $syncArguments += '--check' }
if ($Ref) { $syncArguments += @('--ref', $Ref.ToLowerInvariant()) }
if (-not $Check -and -not $Ref) { throw 'Specify -Check or -Ref <40-character commit SHA>.' }
& python @syncArguments
if ($LASTEXITCODE -ne 0) { throw "VigilWatt synchronization failed (exit $LASTEXITCODE)." }
