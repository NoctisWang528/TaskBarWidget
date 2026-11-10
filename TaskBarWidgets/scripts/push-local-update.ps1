[CmdletBinding(DefaultParameterSetName = 'Push')]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallationDirectory,
    [Parameter(Mandatory = $true, ParameterSetName = 'Push')]
    [string]$ReleaseDirectory,
    [Parameter(ParameterSetName = 'Push')]
    [switch]$EnableDebug,
    [Parameter(Mandatory = $true, ParameterSetName = 'Disable')]
    [switch]$DisableDebug
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Check every existing ancestor, not just the leaf (junctions can redirect writes).
function Get-SafeLocalPath([string]$Path) {
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    if ($full -notmatch '^[A-Za-z]:\\' -or $full.Substring(2).Contains(':')) {
        throw 'A local drive path without alternate data streams is required.'
    }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { throw 'A local fixed drive is required.' }
    $cursor = $full
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Reparse points are not allowed in local update paths.'
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

$installRoot = Get-SafeLocalPath $InstallationDirectory
if (-not [IO.Directory]::Exists($installRoot)) { throw 'Installation directory does not exist.' }
$marker = Get-SafeLocalPath (Join-Path $installRoot 'Data\config\update-debug.enabled')
if ([IO.Directory]::Exists($marker)) { throw 'Debug marker must be a regular file.' }
if ($PSCmdlet.ParameterSetName -eq 'Disable') {
    if (-not $DisableDebug) { throw 'Specify -DisableDebug to disable local imports.' }
    if ([IO.File]::Exists($marker)) { [IO.File]::Delete($marker) }
    Write-Output 'Local update debugging disabled. An already staged update is not installed or removed.'
    return
}

$releaseRoot = Get-SafeLocalPath $ReleaseDirectory
if (-not [IO.Directory]::Exists($releaseRoot)) { throw 'Release directory does not exist.' }
foreach ($name in @('update.json', 'SHA256SUMS.txt')) {
    $asset = Get-SafeLocalPath (Join-Path $releaseRoot $name)
    if (-not [IO.File]::Exists($asset)) { throw "Release directory is missing $name." }
}
if (-not (Get-ChildItem -LiteralPath $releaseRoot -File -Filter '*.zip' | Select-Object -First 1)) {
    throw 'Release directory must contain the update ZIP.'
}

$exe = Get-SafeLocalPath (Join-Path $installRoot 'TaskBarWidgets.exe')
if (-not [IO.File]::Exists($exe)) { throw 'Installation does not contain TaskBarWidgets.exe.' }
# CIM omits ExecutablePath for an elevated host when this script is not elevated.
# Limited process-query access still lets us verify its exact installation path.
if (-not ('TaskBarWidgetsLocalUpdateProcessPath' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TaskBarWidgetsLocalUpdateProcessPath {
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder value, ref uint size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    public static string Read(uint pid) {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try {
            var value = new StringBuilder(32768);
            uint size = 32768;
            return QueryFullProcessImageName(handle, 0, value, ref size) ? value.ToString() : null;
        } finally { CloseHandle(handle); }
    }
}
'@
}
$runningInstances = @(Get-CimInstance Win32_Process -Filter "Name = 'TaskBarWidgets.exe'" | Where-Object {
    $imagePath = if ($_.ExecutablePath) { $_.ExecutablePath } else { [TaskBarWidgetsLocalUpdateProcessPath]::Read($_.ProcessId) }
    $imagePath -and [string]::Equals([IO.Path]::GetFullPath($imagePath), $exe, [StringComparison]::OrdinalIgnoreCase)
})
if ($runningInstances.Count -ne 1) { throw 'Expected exactly one running TaskBarWidgets.exe at the specified installation path. No process was started or stopped.' }
if ($EnableDebug) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $marker)) | Out-Null
    # Recheck after creating missing directories and immediately before changing the marker.
    $marker = Get-SafeLocalPath $marker
    $file = [IO.File]::Open($marker, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $file.Dispose()
} elseif (-not [IO.File]::Exists($marker)) {
    throw 'Local update debugging is disabled. Use -EnableDebug explicitly to enable and push.'
}

$requestId = [guid]::NewGuid().ToString('D')
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$request = $utf8.GetBytes((@{ version = 1; requestId = $requestId; action = 'importLocalUpdate'; releaseDirectory = $releaseRoot } | ConvertTo-Json -Compress) + "`n")
if ($request.Length -gt 16384) { throw 'Request exceeds 16 KiB.' }
$pipeName = 'TaskBarWidgets.UpdateDebug.' + $runningInstances[0].ProcessId
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Identification)
$responseBytes = [IO.MemoryStream]::new()
try {
    try { $pipe.Connect(10000) } catch {
        if ($_.Exception.GetBaseException() -is [UnauthorizedAccessException]) {
            throw 'The running application is elevated. Run this script from an administrator PowerShell window to access its local update pipe.'
        }
        throw 'Cannot connect to the local debug pipe within 10 seconds. Older installations require one manual upgrade; check that Media is running.'
    }
    $write = $pipe.WriteAsync($request, 0, $request.Length)
    if (-not $write.Wait(10000)) { throw 'Local update request write timed out.' }
    $write.GetAwaiter().GetResult()
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $buffer = [byte[]]::new(1024)
    $complete = $false
    while (-not $complete) {
        $remaining = 300000 - [int]$deadline.ElapsedMilliseconds
        if ($remaining -le 0) { throw 'Local update import response timed out after five minutes; check the existing update UI before retrying.' }
        $read = $pipe.ReadAsync($buffer, 0, $buffer.Length)
        if (-not $read.Wait($remaining)) { throw 'Local update import response timed out after five minutes; check the existing update UI before retrying.' }
        $count = $read.GetAwaiter().GetResult()
        if ($count -eq 0) { throw 'Local update pipe disconnected before a complete response.' }
        for ($i = 0; $i -lt $count; $i++) {
            if ($responseBytes.Length -ge 65536) { throw 'Local update response exceeds 64 KiB.' }
            $responseBytes.WriteByte($buffer[$i])
            if ($buffer[$i] -eq 10) { $complete = $true; break }
        }
    }
    try { $response = $utf8.GetString($responseBytes.ToArray()) | ConvertFrom-Json } catch { throw 'Invalid UTF-8 JSON response from local update pipe.' }
    if ($response.version -ne 1 -or $response.requestId -cne $requestId -or $response.success -isnot [bool]) {
        throw 'Local update response has an invalid version, requestId, or success flag.'
    }
    if (-not $response.success) {
        if ($response.error -is [string] -and $response.error) { throw ("Local update import failed: " + $response.error) }
        throw 'Local update import failed without an error message.'
    }
    if ($response.snapshot.phase -cne 'ready') { throw 'Import returned success without a ready update snapshot.' }
    # Do not dump snapshots or configuration; only report the verified state.
    Write-Output 'Local update verified and ready. Confirm installation in the existing update UI; nothing was auto-installed.'
} finally {
    $pipe.Dispose()
    $responseBytes.Dispose()
}
