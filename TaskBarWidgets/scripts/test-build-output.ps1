$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts')).TrimEnd('\') + '\'
$testRoot = Join-Path $artifactPrefix ('test-build-output-' + [Guid]::NewGuid().ToString('N'))
if (-not $testRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test directory.' }
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected invalid output directory to be rejected.' }
}
try {
    $testScripts = Join-Path $testRoot 'scripts'
    $candidate = Join-Path $testRoot 'artifacts\builds\20261013-120000'
    New-Item -ItemType Directory -Path $testScripts, $candidate -Force | Out-Null
    $resolver = Join-Path $testScripts 'resolve-app-directory.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Destination $resolver
    Assert-Rejected { & $resolver }
    $pointer = Join-Path $testRoot 'artifacts\latest-build.txt'
    Set-Content -LiteralPath $pointer -Value $candidate -Encoding UTF8
    if ((& $resolver) -ne $candidate) { throw 'Latest successful build did not resolve.' }
    $explicit = Join-Path $testRoot 'artifacts\component-output'
    if ((& $resolver -Directory $explicit) -ne $explicit) { throw 'Explicit component output did not resolve.' }
    Assert-Rejected { & $resolver -Directory $testRoot }
    Set-Content -LiteralPath $pointer -Value (Join-Path $testRoot 'artifacts\missing') -Encoding UTF8
    Assert-Rejected { & $resolver }
    Set-Content -LiteralPath $pointer -Value $productRoot -Encoding UTF8
    Assert-Rejected { & $resolver }
    Write-Host 'Build output directory checks passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
