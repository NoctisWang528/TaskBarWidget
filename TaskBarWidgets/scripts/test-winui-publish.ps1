[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
# A new output directory prevents a previous validation host's PRI from masking an incomplete product publish.
$output = Join-Path $productRoot ('artifacts\validation\publish-resources-' + [Guid]::NewGuid().ToString('N'))
& dotnet publish (Join-Path $productRoot 'UI\TaskBarWidgets.UI.csproj') -c $Configuration -r win-x64 --self-contained false -o $output -p:BuildInParallel=false --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Fresh WinUI publish failed.' }
$generated = Join-Path $productRoot "UI\bin\$Configuration\net8.0-windows10.0.22621.0\win-x64"
foreach ($resource in @('TaskBarWidgets.UI.pri', 'App.xbf')) {
    $published = Join-Path $output $resource
    if (-not (Test-Path -LiteralPath $published -PathType Leaf)) { throw "Missing published WinUI resource: $resource" }
    if ((Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $generated $resource) -Algorithm SHA256).Hash) {
        throw "Published resource differs from the current product build: $resource"
    }
}
Write-Host "Fresh WinUI resource publish passed: $output. No application was launched."
