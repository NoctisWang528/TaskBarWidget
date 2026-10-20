[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$SkipWeb)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$logDirectory = Join-Path $productRoot 'artifacts\logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
Push-Location $productRoot
try {
    & (Join-Path $PSScriptRoot 'check-boundaries.ps1')
    & (Join-Path $PSScriptRoot 'check-factory-defaults.ps1')
    & (Join-Path $PSScriptRoot 'import-traffic-history.tests.ps1') *> (Join-Path $logDirectory 'history-import-tests.log')
    Get-Content (Join-Path $logDirectory 'history-import-tests.log') -Tail 1 | Write-Host
    & (Join-Path $productRoot 'Native\tests\run-native-tests.ps1') *> (Join-Path $logDirectory 'native-state-tests.log')
    Get-Content (Join-Path $logDirectory 'native-state-tests.log') -Tail 2 | Write-Host
    foreach ($project in Get-ChildItem -LiteralPath 'tests' -Recurse -Filter '*.csproj' | Where-Object FullName -NotMatch '\\(bin|obj)\\') {
        $log = Join-Path $logDirectory ($project.BaseName + '-tests.log')
        & dotnet test $project.FullName -c $Configuration -p:BuildInParallel=false --verbosity minimal *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 35 | Write-Host; throw "Tests failed: $($project.Name). See $log" }
        Get-Content -LiteralPath $log -Tail 4 | Write-Host
    }
    if (-not $SkipWeb) {
        $npm = (Get-Command npm.cmd -ErrorAction Stop).Source
        if (-not (Test-Path -LiteralPath 'node_modules')) {
            & $npm ci *> (Join-Path $logDirectory 'npm-ci.log')
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed; see artifacts/logs/npm-ci.log' }
        }
        & $npm run test:web *> (Join-Path $logDirectory 'web-tests.log')
        if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $logDirectory 'web-tests.log') -Tail 35 | Write-Host; throw 'Web tests failed.' }
        Get-Content (Join-Path $logDirectory 'web-tests.log') -Tail 8 | Write-Host
    }
    Write-Host 'Automated verification passed. Run scripts/smoke-media.ps1 after packaging for helper IPC validation. Real desktop behavior still requires user acceptance.'
}
finally { Pop-Location }
