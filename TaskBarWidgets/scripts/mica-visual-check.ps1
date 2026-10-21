[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidateRange(2000, 60000)][int]$HoldMilliseconds = 9000,
    [string]$SamplesFile,
    [ValidateRange(1000, 10000)][int]$StabilizeMilliseconds = 1800,
    [ValidateRange(-32000, 32000)][int]$Left = 70,
    [ValidateRange(-32000, 32000)][int]$Top = 70,
    [ValidateRange(320, 1600)][int]$Width = 420,
    [ValidateRange(280, 1200)][int]$Height = 300
)
$ErrorActionPreference = 'Stop'
$directory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
$run = Join-Path $directory ('ui-mica-check/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$checks = Join-Path $run 'ui-mica-check'
New-Item -ItemType Directory -Path $checks -Force | Out-Null

# Isolate the runtime too: shared UI error/lifecycle logs use AppContext's base
# directory. Copy only program resources, never the user's settings or caches.
# No normal product/native/media process is launched.
$uiSource = Join-Path $directory 'UI'
foreach ($file in Get-ChildItem -LiteralPath $uiSource -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($uiSource, $file.FullName)
    if ($relative -match '(^|[\\/])(Data|database|.*\.WebView2|WebView2|EBWebView|User Data|Cache|logs|backups|.*profile.*|ui-[a-z]+-check)([\\/]|$)') { continue }
    if ($file.Extension -notin @('.exe', '.dll', '.pri', '.xbf', '.ico', '.mui') -and
        $file.Name -notin @('TaskBarWidgets.UI.deps.json', 'TaskBarWidgets.UI.runtimeconfig.json')) { continue }
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked UI program inputs are not allowed.' }
    $destination = Join-Path (Join-Path $run 'UI') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$sampleCount = 10 # DefaultSamples: light/dark x system/default/0.85/0.65/0.45.
$samplesPath = $null
if ($SamplesFile) {
    $samplesPath = Join-Path $checks 'samples.json'
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath($SamplesFile)) -Destination $samplesPath
    $samples = @(Get-Content -LiteralPath $samplesPath -Raw | ConvertFrom-Json)
    $sampleCount = $samples.Count
    if ($sampleCount -eq 0) { throw 'Sample file is empty.' }
    if ($samples | Where-Object { $_.lyrics }) {
        $lyricsTarget = Join-Path $run 'Media/Web/Lyrics'
        New-Item -ItemType Directory -Path $lyricsTarget -Force | Out-Null
        foreach ($name in @('index.html', 'style.css', 'bridge.js', 'state.js', 'presentation.js', 'app.js', 'browser.js')) {
            Copy-Item -LiteralPath (Join-Path $directory "Media/Web/Lyrics/$name") -Destination (Join-Path $lyricsTarget $name)
        }
    }
}

# These are genuine desktop pixels, not a XAML/offscreen render. Thread DPI
# awareness avoids virtualized coordinates even in an already-running PS host.
if (-not ('MicaScreenCapture' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class MicaScreenCapture {
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
}
"@
}
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'shared-runtime-process.ps1')
$previousDpi = [MicaScreenCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))
if ($previousDpi -eq [IntPtr]::Zero) { throw 'Cannot establish physical-pixel DPI context for desktop capture.' }
$captures = [Collections.Generic.List[object]]::new()
$figures = [Collections.Generic.List[string]]::new()
try {
    $screenLeft = [MicaScreenCapture]::GetSystemMetrics(76)
    $screenTop = [MicaScreenCapture]::GetSystemMetrics(77)
    $screenRight = $screenLeft + [MicaScreenCapture]::GetSystemMetrics(78)
    $screenBottom = $screenTop + [MicaScreenCapture]::GetSystemMetrics(79)
    if ($Left -lt $screenLeft -or $Top -lt $screenTop -or $Left + $Width -gt $screenRight -or $Top + $Height -gt $screenBottom) {
        throw 'Requested capture rectangle is outside the desktop. Choose -Left/-Top/-Width/-Height inside a visible monitor.'
    }
    for ($index = 0; $index -lt $sampleCount; $index++) {
        $name = 'sample-{0:d2}' -f $index
        $sampleDirectory = Join-Path $checks $name
        New-Item -ItemType Directory -Path $sampleDirectory | Out-Null
        $report = Join-Path $sampleDirectory 'probe.json'
        $png = Join-Path $sampleDirectory 'desktop.png'
        $arguments = @('--mica-visual-check', '--result', $report, '--wait-for-capture',
            '--sample-index', "$index", '--hold-ms', "$HoldMilliseconds", '--stabilize-ms', "$StabilizeMilliseconds",
            '--x', "$Left", '--y', "$Top", '--width', "$Width", '--height', "$Height")
        if ($samplesPath) { $arguments += @('--samples-file', $samplesPath) }
        $start = New-SharedRuntimeProcess $run 'UI/TaskBarWidgets.UI.exe' $arguments
        $probe = $null
        try {
            $probe = [Diagnostics.Process]::Start($start)
            $deadline = [DateTime]::UtcNow.AddSeconds(40)
            while ($true) {
                $shown = $null
                if (Test-Path -LiteralPath $report) {
                    try { $shown = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json } catch { $shown = $null }
                }
                if ($shown -and -not $shown.success) { throw "Mica sample failed: $($shown.error)" }
                if ($probe.HasExited) { throw "Mica sample exited before capture (exit $($probe.ExitCode))." }
                if ($shown -and $shown.phase -eq 'shown') { break }
                if ([DateTime]::UtcNow -gt $deadline) { throw 'Mica sample did not stabilize in time.' }
                Start-Sleep -Milliseconds 100
            }
            if (@($shown.windows).Count -ne 1) { throw 'Expected exactly one Mica sample window.' }
            if ($shown.totalCount -ne $sampleCount) { throw 'Probe sample count does not match the requested comparison matrix.' }
            $window = $shown.windows[0]
            $rect = $window.rect
            if ($rect.x -ne $Left -or $rect.y -ne $Top -or $rect.width -ne $Width -or $rect.height -ne $Height) {
                throw "Actual window rectangle differs from the fixed capture rectangle: $($rect | ConvertTo-Json -Compress)."
            }
            if ($window.foreground) { throw 'Diagnostic window unexpectedly acquired foreground focus.' }
            $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.CopyFromScreen($Left, $Top, 0, 0, [Drawing.Size]::new($Width, $Height), [Drawing.CopyPixelOperation]::SourceCopy)
                } finally { $graphics.Dispose() }
                $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
            } finally { $bitmap.Dispose() }
            # Preserve the ready-state report next to the unedited capture; the
            # process subsequently replaces probe.json with its closing result.
            $shown | Add-Member -NotePropertyName capturedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o'))
            $shown | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $sampleDirectory 'capture.json') -Encoding utf8
            [IO.File]::WriteAllText($report + '.captured', [DateTimeOffset]::UtcNow.ToString('o'))
            if (-not $probe.WaitForExit(10000)) { throw 'Mica sample did not exit after capture acknowledgement.' }
            $final = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            if ($probe.ExitCode -ne 0 -or -not $final.success -or $final.phase -ne 'closed') { throw "Mica sample did not close cleanly: $($final.error)" }
            $captures.Add([ordered]@{ sample = $name; screenshot = "$name/desktop.png"; report = "$name/capture.json"; window = $window })
            $label = [Net.WebUtility]::HtmlEncode($window.label)
            $details = [Net.WebUtility]::HtmlEncode(($window | ConvertTo-Json -Depth 12))
            $assessment = [Net.WebUtility]::HtmlEncode($window.assessment)
            $figures.Add("<figure><figcaption><b>$label</b><p>$assessment</p></figcaption><a href='$name/desktop.png'><img src='$name/desktop.png' width='$Width' height='$Height' alt='$label'></a><details><summary>Actual parameters and state</summary><pre>$details</pre></details><a href='$name/capture.json'>Capture JSON</a></figure>")
            if ($window.fallbackOrUnverified -or -not $window.stabilized -or -not $window.configurationMatches) {
                Write-Warning "$name : $($window.assessment)"
            }
            Write-Host "Captured $name : $($window.label)"
        } finally {
            if ($null -ne $probe) {
                try {
                    if (-not $probe.HasExited) {
                        # Kill only the diagnostic process and its own WebView
                        # children, never any daily product process.
                        $probe.Kill($true)
                        if (-not $probe.WaitForExit(5000)) { Write-Warning 'Diagnostic process termination timed out.' }
                    }
                } finally { $probe.Dispose() }
            }
        }
    }
} finally {
    [MicaScreenCapture]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
    # Keep completed evidence even if a later sample fails.
    [ordered]@{ complete = $captures.Count -eq $sampleCount; visualPass = $false;
        note = 'Unedited desktop captures at the same physical screen rectangle. Fallback is not a visual pass; active material still requires manual review.';
        totalCount = $sampleCount; capturedSamples = $captures.Count;
        rectangle = @{ x = $Left; y = $Top; width = $Width; height = $Height }; samples = @($captures.ToArray()) } |
        ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $checks 'comparison.json') -Encoding utf8
    @"
<!doctype html><html lang="en"><meta charset="utf-8"><title>Mica desktop comparison</title>
<style>body{font:14px system-ui;margin:24px;background:#ddd;color:#222}main{display:flex;flex-wrap:wrap;gap:20px}figure{margin:0;width:${Width}px}img{display:block;max-width:100%;height:auto}figcaption{min-height:110px}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:11px}details{margin:12px 0}</style>
<h1>Mica desktop comparison</h1><p>Captured $($captures.Count) / $sampleCount samples at the same physical screen rectangle ($Left, $Top, $Width × $Height). Images are unedited originals. Labels and diagnostics are outside the captured window.</p>
<p><strong>No automatic visual pass.</strong> Fallback, unverified, unstable or mismatched samples are not material acceptance. Review text legibility and wallpaper response manually; avoid desktop/wallpaper changes during capture.</p>
<main>$($figures -join "`n")</main></html>
"@ | Set-Content -LiteralPath (Join-Path $checks 'index.html') -Encoding utf8
}
Write-Host "Desktop Mica comparison ready for manual review: $(Join-Path $checks 'index.html')"
