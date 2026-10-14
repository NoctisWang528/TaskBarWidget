[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$directory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
$uiDirectory = Join-Path $directory 'UI'
foreach ($name in @('TaskBarWidgets.UI.exe','TaskBarWidgets.UI.pri','App.xbf')) {
    if (-not (Test-Path -LiteralPath (Join-Path $uiDirectory $name) -PathType Leaf)) { throw "Missing UI publish file: $name" }
}
$report = Join-Path $directory 'ui-template-check.json'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
# Test mode never starts native/media hosts. Show(false) realizes templates strictly
# offscreen, with no switcher entries, Window.Activate calls, or foreground request.
$probe = Start-Process -FilePath (Join-Path $uiDirectory 'TaskBarWidgets.UI.exe') -ArgumentList @('--validate-templates','--result',"`"$report`"") -WorkingDirectory $uiDirectory -WindowStyle Hidden -PassThru
if (-not $probe.WaitForExit(15000)) { Stop-Process -Id $probe.Id; throw 'UI template probe timed out; only its process was stopped.' }
if ($probe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw "UI template probe failed (exit $($probe.ExitCode))." }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.success) { throw $result.error }
if ($result.checks.Count -ne 4 -or @($result.checks | Where-Object { -not $_.loaded -or $_.width -le 0 }).Count) { throw 'Not all four UI surfaces realized their templates.' }
Write-Host "Published settings and three card templates loaded successfully without activation. Report: $report"
