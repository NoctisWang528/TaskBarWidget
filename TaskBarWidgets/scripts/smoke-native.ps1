[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
$executable = Join-Path $appDirectory 'TaskBarWidgets.exe'
if (Get-Process -Name TaskBarWidgets -ErrorAction SilentlyContinue) {
    throw 'TaskBarWidgets is already running; exit it before checking a fresh startup.'
}
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the native component first.' }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class NativeStartupWindows {
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    public static int VisibleWindows(uint processId) {
        var windows = new HashSet<IntPtr>();
        WindowCallback inspect = (hwnd, _) => {
            uint pid; Rect rect;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == processId && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out rect)
                && rect.Right > rect.Left && rect.Bottom > rect.Top) {
                var caption = new StringBuilder(256);
                GetWindowText(hwnd, caption, caption.Capacity);
                if (caption.ToString() == "TaskBarWidgetsTaskbarWindow") windows.Add(hwnd);
            }
            return true;
        };
        EnumWindows((hwnd, data) => { inspect(hwnd, data); EnumChildWindows(hwnd, inspect, data); return true; }, IntPtr.Zero);
        return windows.Count;
    }
}
'@
# The production manifest requests administrator access. This check intentionally
# exercises the actual native entry point; the helper-only smoke cannot cover it.
$process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -Verb RunAs -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw "Native application exited during startup (exit $($process.ExitCode))." }
        $visibleCount = [NativeStartupWindows]::VisibleWindows([uint32]$process.Id)
    } while ($visibleCount -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($visibleCount -eq 0) { throw 'Process is alive but its visible taskbar window was not found.' }
    Start-Sleep -Seconds 3
    $process.Refresh()
    if ($process.HasExited) { throw "Native application exited after creating a window (exit $($process.ExitCode))." }
    Write-Host "Native startup check passed: PID $($process.Id) remains running; $visibleCount visible taskbar window(s). Application left running for desktop acceptance."
}
finally { $process.Dispose() }
