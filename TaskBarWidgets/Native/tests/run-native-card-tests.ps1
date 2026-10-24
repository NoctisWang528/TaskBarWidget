param([Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($BuildDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $source 'build-info.json'))) { throw 'A complete product build is required.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$msbuild = Join-Path $installation 'MSBuild/Current/Bin/MSBuild.exe'
& $msbuild (Join-Path $PSScriptRoot 'NativeCardsTests.vcxproj') /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /nologo
if ($LASTEXITCODE) { throw 'Native card probe build failed.' }
$probe = Join-Path $source 'NativeCardsProbe.exe'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Bin/Tests/Cards/NativeCardsProbe.exe') -Destination $probe -Force
$process = Start-Process -FilePath $probe -WorkingDirectory $source -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $source 'native-card-check.log') -RedirectStandardError (Join-Path $source 'native-card-error.log')
if (-not $process.WaitForExit(45000)) { Stop-Process -Id $process.Id; throw 'Isolated card probe did not exit in 45 seconds.' }
$process.Refresh()
Get-Content -LiteralPath (Join-Path $source 'native-card-check.log')
Get-Content -LiteralPath (Join-Path $source 'native-card-error.log')
if ($process.ExitCode) { throw "Native card probe failed ($($process.ExitCode))." }
Remove-Item -LiteralPath $probe
