[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$productRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$isolationRoot = Join-Path $productRoot ('artifacts\standalone-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $isolationRoot -Force | Out-Null
# The copy has no TrafficMonitor/TaskbarLyrics/AF-Media-Bar siblings. Dependencies
# are reused only from this product's own package cache; original source is never moved.
& robocopy $productRoot $isolationRoot /E /XD (Join-Path $productRoot 'artifacts') .packages bin obj x64 node_modules .vs /XF '*.obj' '*.pch' '*.pdb' '*.aps' '*.tlog' '*.log' /NFL /NDL /NJH /NJS *> (Join-Path $isolationRoot 'copy.log')
if ($LASTEXITCODE -gt 7) { throw "Independent source copy failed: $LASTEXITCODE" }
New-Item -ItemType Junction -Path (Join-Path $isolationRoot '.packages') -Target (Join-Path $productRoot '.packages') | Out-Null
& (Join-Path $isolationRoot 'scripts\build.ps1')
Write-Host "Independent source-copy build passed: $isolationRoot"
