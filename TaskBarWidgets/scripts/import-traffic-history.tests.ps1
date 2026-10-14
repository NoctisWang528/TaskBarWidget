$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $root ('artifacts\history-test-' + [Guid]::NewGuid().ToString('N'))
$targetDirectory = Join-Path $testRoot 'target'
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
$source = Join-Path $testRoot 'source.dat'
$target = Join-Path $targetDirectory 'history_traffic.dat'
[IO.File]::WriteAllText($source, "lines: `"5`"`n2026/10/13 10/20`n2026/10/12 100`n2026/10/11 0/0`n2026/10/10 3/7`n2026/10/10 3/7`n")
[IO.File]::WriteAllText($target, "lines: `"2`"`n2026/10/13 5/40`n2026/10/12 40/60`n")
$originalHash = (Get-FileHash -LiteralPath $source).Hash
& (Join-Path $PSScriptRoot 'import-traffic-history.ps1') -SourceFile $source -DestinationDirectory $targetDirectory
$actual = [IO.File]::ReadAllLines($target)
$expected = @('lines: "4"', '2026/10/13 5/40', '2026/10/12 40/60', '2026/10/11 0/0', '2026/10/10 3/7')
if (($actual -join "`n") -ne ($expected -join "`n")) { throw 'History import duplicated usage or lost known-zero/legacy records.' }
$firstHash = (Get-FileHash -LiteralPath $target).Hash
& (Join-Path $PSScriptRoot 'import-traffic-history.ps1') -SourceFile $source -DestinationDirectory $targetDirectory
if ((Get-FileHash -LiteralPath $target).Hash -ne $firstHash) { throw 'History import is not idempotent.' }
if ((Get-FileHash -LiteralPath $source).Hash -ne $originalHash) { throw 'Source history was modified.' }
[IO.File]::WriteAllText($source, "lines: `"1`"`n2026/02/30 100/200`n")
$rejected = $false
try { & (Join-Path $PSScriptRoot 'import-traffic-history.ps1') -SourceFile $source -DestinationDirectory $targetDirectory }
catch { $rejected = $true }
if (-not $rejected -or (Get-FileHash -LiteralPath $target).Hash -ne $firstHash) { throw 'Invalid history must not change the destination.' }
Write-Host 'Traffic history import tests passed: legacy totals, zero days, whole-record max, duplicate dates, idempotence, source preservation and invalid-input rejection.'
