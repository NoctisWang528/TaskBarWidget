[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$directory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
$info = Join-Path $directory 'build-info.json'
if ((Test-Path -LiteralPath $info) -and (Get-Content -LiteralPath $info -Raw | ConvertFrom-Json).configuration -ne $Configuration) {
    throw 'Build configuration differs from the requested validation configuration.'
}
# Validate the shipped executable and its own PRI/XBF, not a second application with a different resource root.
& (Join-Path $PSScriptRoot 'test-ui-templates.ps1') -OutputDirectory $directory
