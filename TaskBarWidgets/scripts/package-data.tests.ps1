$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'package-data.ps1')
$root = Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts\tests\package-data-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$source = Join-Path $root 'settings.json'
[IO.File]::WriteAllText($source, '{"value":42}')
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$record = Copy-StableDataFile $source (Join-Path $root 'copied.json')
if ($record.sha256 -ine $sourceHash -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash) { throw 'JSON snapshot changed content.' }
$failed = $false
try { Copy-StableDataFile $source (Join-Path $root 'copied.json') | Out-Null } catch { $failed = $true }
if (-not $failed) { throw 'Existing snapshot was overwritten.' }
[IO.File]::WriteAllText($source, '{broken')
$failed = $false
try { Copy-StableDataFile $source (Join-Path $root 'invalid.json') | Out-Null } catch { $failed = $true }
if (-not $failed -or (Test-Path -LiteralPath (Join-Path $root 'invalid.json'))) { throw 'Invalid JSON was accepted.' }
$traffic = Join-Path $root 'history_traffic.dat'
[IO.File]::WriteAllText($traffic, "lines: `"1`"`r`n2026/10/18 10/20`r`n")
Copy-StableDataFile $traffic (Join-Path $root 'traffic-valid.dat') | Out-Null
[IO.File]::WriteAllText($traffic, "lines: `"2`"`r`n2026/10/18 10/20`r`n")
$failed = $false
try { Copy-StableDataFile $traffic (Join-Path $root 'traffic-invalid.dat') | Out-Null } catch { $failed = $true }
if (-not $failed) { throw 'Incomplete traffic snapshot was accepted.' }
Write-Host 'Stable data snapshot validation passed.'
