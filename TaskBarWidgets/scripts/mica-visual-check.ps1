[CmdletBinding()]
param([string]$OutputDirectory, [int]$HoldMilliseconds = 9000, [string]$SamplesFile)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$directory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
$report = Join-Path $directory 'mica-visual-check.json'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
$checks = Join-Path $directory 'ui-mica-check'
New-Item -ItemType Directory -Path $checks -Force | Out-Null
$png = Join-Path $checks 'desktop.png'
if (Test-Path -LiteralPath $png) { Remove-Item -LiteralPath $png }

# The desktop window manager composites the Mica material outside the XAML
# visual tree, so this step captures real screen pixels. It briefly shows card
# windows on the primary monitor, never activates them, and touches no settings.
. (Join-Path $PSScriptRoot 'shared-runtime-process.ps1')
$arguments = @('--mica-visual-check', '--result', $report, '--hold-ms', $HoldMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($SamplesFile) { $arguments += @('--samples-file', [IO.Path]::GetFullPath($SamplesFile)) }
$start = New-SharedRuntimeProcess $directory 'UI/TaskBarWidgets.UI.exe' $arguments
$probe = [Diagnostics.Process]::Start($start)
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($true) {
        if ($probe.HasExited) { throw "Mica visual probe exited before showing its windows (exit $($probe.ExitCode))." }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Mica visual probe did not show its windows in time.' }
        # The probe rewrites the report when it closes; tolerate a partial read.
        $shown = $null
        if (Test-Path -LiteralPath $report) {
            try { $shown = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json } catch { $shown = $null }
        }
        if ($shown -and $shown.phase -eq 'shown') { break }
        Start-Sleep -Milliseconds 150
    }
    $left = ($shown.windows.rect.x | Measure-Object -Minimum).Minimum - 40
    $top = ($shown.windows.rect.y | Measure-Object -Minimum).Minimum - 40
    $right = (($shown.windows.rect | ForEach-Object { $_.x + $_.width }) | Measure-Object -Maximum).Maximum + 40
    $bottom = (($shown.windows.rect | ForEach-Object { $_.y + $_.height }) | Measure-Object -Maximum).Maximum + 40
    if (-not ('ScreenDpi' -as [type])) {
        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ScreenDpi { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context); }'
    }
    # PER_MONITOR_AWARE_V2 so captured pixels match the probe's physical window rects.
    [ScreenDpi]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
    Add-Type -AssemblyName System.Drawing
    $width = [int]$right - [int]$left
    $height = [int]$bottom - [int]$top
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$left, [int]$top, 0, 0, [System.Drawing.Size]::new($width, $height), [System.Drawing.CopyPixelOperation]::SourceCopy)
    }
    finally { $graphics.Dispose() }
    try { $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    Write-Host "Captured desktop pixels: $png"
}
finally {
    if (-not $probe.WaitForExit(40000)) { Stop-Process -Id $probe.Id; Write-Warning 'Mica visual probe was still running and was stopped.' }
}
$final = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $final.success) { throw $final.error }
Write-Host "Desktop Mica capture complete. Report: $report, screenshot: $png"
