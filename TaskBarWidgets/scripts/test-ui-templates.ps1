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
$fixture = Join-Path $productRoot 'UI.Validation\settings-fixture.json'
. (Join-Path $PSScriptRoot 'shared-runtime-process.ps1')
$start = New-SharedRuntimeProcess $directory 'UI/TaskBarWidgets.UI.exe' @('--validate-templates','--result',$report,'--fixture',$fixture)
$probe = [Diagnostics.Process]::Start($start)
if (-not $probe.WaitForExit(60000)) { Stop-Process -Id $probe.Id; throw 'UI template probe timed out; only its process was stopped.' }
if ($probe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw "UI template probe failed (exit $($probe.ExitCode))." }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.success) { throw $result.error }
$runtimePrefix = (Get-SystemDotnetRoot).TrimEnd('\') + '\'
if (-not $result.runtimeDirectory.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'UI did not use the installed shared .NET runtime.' }
if ($result.winUiModule -notmatch '\\WindowsApps\\Microsoft.WindowsAppRuntime\.2_[^\\]+_x64__8wekyb3d8bbwe\\') { throw 'UI did not load WinUI from the installed x64 Windows App Runtime package.' }
if ($result.checks.Count -ne 4 -or @($result.checks | Where-Object { -not $_.loaded -or $_.width -le 0 }).Count) { throw 'Not all four UI surfaces realized their templates.' }
if (-not $result.audioControls.muteButtonRoundTrip -or -not $result.audioControls.staleReplyIgnored -or -not $result.cardLifecycle.externalClickPolicy) { throw 'Audio controls or outside-click component checks did not complete.' }
foreach ($pointer in @($result.layoutChecks | Where-Object kind -eq 'modulePointerMessages')) {
    if (-not $pointer.available) { Write-Host "Actual pointer verification unavailable: $($pointer.reason). Desktop acceptance remains pending." }
}
Write-Host "Published settings and three card templates loaded successfully without activation. Report: $report"
