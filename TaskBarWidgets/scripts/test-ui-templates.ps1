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
if ($result.checks.Count -ne 5 -or @($result.checks | Where-Object { -not $_.loaded -or $_.width -le 0 }).Count) { throw 'Not all five UI surfaces realized their templates.' }
if (-not $result.audioControls.muteButtonRoundTrip -or -not $result.audioControls.inputMuteButtonRoundTrip -or
    -not $result.audioControls.volumePreserved -or -not $result.audioControls.staleReplyIgnored -or
    -not $result.audioControls.iconsUnclipped -or $result.audioControls.scalerIcons -lt 6 -or
    $result.audioControls.centeredIconHosts -lt 6 -or $result.audioControls.maximumCenterError -gt 0.5 -or
    -not $result.cardLifecycle.externalClickPolicy) { throw 'Audio controls, icon centering or outside-click component checks did not complete.' }
if (-not $result.micaControls.fixtureRoundTrip -or -not $result.micaControls.noPresets -or
    -not $result.micaControls.noMockPreview -or -not $result.micaControls.themeKinds -or
    -not $result.micaControls.fourSliders -or -not $result.micaControls.legacyCompatible -or
    -not $result.micaControls.vividSwitch -or
    ($result.micaControls.available -and (-not $result.micaControls.frameCoalesced -or
        -not $result.micaControls.vividRoundTrip -or
        -not $result.micaControls.actualControllers -or -not $result.micaControls.staleSwitchIgnored -or
        -not $result.micaControls.stableBackdrop -or -not $result.micaControls.savedReadback -or
        -not $result.micaControls.controllerLifecycle.disconnected -or
        -not $result.micaControls.discardRestored -or -not $result.micaControls.previewRestoredOnLeave -or
        -not $result.micaControls.independentThemes -or -not $result.micaControls.reopenRearmsPreview)) -or
    -not $result.micaControls.limitsClamped -or -not $result.micaControls.defaultsFallback) { throw 'Card Mica settings checks did not complete.' }
if (-not $result.cardLifecycle.micaNewCardInherits) { throw 'A card created during Mica preview did not inherit it.' }
if (-not $result.cardLifecycle.micaAlwaysInputActive) { throw 'Card Mica was not configured to remain input-active.' }
if (-not $result.offsetSettings.bothPayloadsVisible -or
    $result.offsetSettings.initialActivations -ne 1 -or $result.offsetSettings.initialBatches -ne 1 -or
    $result.offsetSettings.initialFinalRenders -ne 1 -or $result.offsetSettings.redrawQueries -ne 0 -or
    $result.offsetSettings.samePageReopenActivations -ne 1 -or $result.offsetSettings.stormActivations -ne 1 -or
    -not $result.offsetSettings.staleEntryIgnored -or -not $result.offsetSettings.searchWithoutActivation -or
    -not $result.offsetSettings.pagingWithoutActivation -or -not $result.offsetSettings.staleRequestIgnored -or
    -not $result.offsetSettings.requestIdsMonotonic -or $result.offsetSettings.validBatchFinalRenders -ne 1) {
    throw 'Track-offset page lifecycle, request ordering or single-render checks did not complete.'
}
if (-not $result.reorderControls.sameColumn -or -not $result.reorderControls.crossColumn -or
    -not $result.reorderControls.emptyColumn -or -not $result.reorderControls.pointerGapPresentation -or
    -not $result.reorderControls.reducedMotionFallback -or -not $result.reorderControls.cancelRestoresRows -or
    -not $result.reorderControls.dropCommitsOnce) {
    throw 'Reorder interaction checks did not complete.'
}
foreach ($layout in @($result.layoutChecks | Where-Object { $_.kind -eq 'settingsTaskbarLayout' })) {
    if ($layout.advancedFields -lt 1 -or -not $layout.viewport.scrollbarAtRight -or
        -not $layout.titleBar.custom -or $layout.titleBar.height -lt 44 -or -not $layout.titleBar.icon -or -not $layout.titleBar.title) {
        throw 'Settings title bar, viewport or directly expanded advanced fields were not verified at every width.'
    }
}
foreach ($spectrum in @($result.layoutChecks | Where-Object { $_.kind -eq 'settingsSpectrumAdvancedFields' })) {
    if ($spectrum.fieldCount -lt 1) { throw 'Spectrum tuning fields were not directly realized.' }
}
if (-not $result.weatherCardControls.providerNeutralSnapshot -or -not $result.weatherCardControls.missingDataHidden -or
    -not $result.weatherCardControls.allAlertsRetained -or -not $result.weatherCardControls.alertRotation -or
    -not $result.weatherCardControls.singleAlertFixed -or
    -not $result.weatherCardControls.zeroAlertsBlank -or -not $result.weatherCardControls.micaSurface -or
    -not $result.weatherCardControls.staleRetained -or -not $result.weatherCardControls.disabledCardWindowReleased -or
    -not $result.weatherCardControls.reenabledCardWindowRecreated) { throw 'Weather card component checks did not complete.' }
foreach ($pointer in @($result.layoutChecks | Where-Object kind -eq 'modulePointerMessages')) {
    if (-not $pointer.available) { Write-Host "Actual pointer verification unavailable: $($pointer.reason). Desktop acceptance remains pending." }
}
Write-Host "Published settings and four card templates loaded successfully without activation. Report: $report"
