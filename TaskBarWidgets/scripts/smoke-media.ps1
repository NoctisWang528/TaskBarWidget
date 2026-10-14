[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $OutputDirectory
$executable = Join-Path $appDirectory 'Media\TaskBarWidgets.Media.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the Media component before running this smoke check.' }
$pipeName = "TaskBarWidgets.$PID"
$process = $null
$pipe = $null
$reader = $null
$writer = $null
function Read-ProtocolMessage {
    $pending = $reader.ReadLineAsync()
    if (-not $pending.Wait(20000)) { throw 'Media protocol read timed out.' }
    if ($null -eq $pending.Result) { throw 'Media pipe closed unexpectedly.' }
    return ($pending.Result | ConvertFrom-Json)
}
try {
    $process = Start-Process -FilePath $executable -ArgumentList @('--host-pid', $PID, '--pipe-name', $pipeName) -WorkingDirectory (Split-Path $executable) -WindowStyle Hidden -PassThru
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect(20000)
    $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 8192, $true)
    $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 8192, $true)
    $writer.AutoFlush = $true
    $initial = Read-ProtocolMessage
    if ($initial.version -ne 1 -or $initial.type -ne 'initialize' -or $initial.html -notmatch 'taskbarLyrics') { throw 'Invalid renderer initialization.' }
    $frame = Read-ProtocolMessage
    if ($frame.version -ne 1 -or $frame.type -ne 'frame') { throw 'Missing initial media snapshot.' }
    # Invalid version types must be ignored without terminating the connection.
    $writer.WriteLine('{"version":"invalid","type":"command","action":"unknown-smoke-test"}')
    $writer.WriteLine('{"version":1,"type":"command","action":"mediaVisible","visible":false}')
    $nextFrame = Read-ProtocolMessage
    if ($nextFrame.type -ne 'frame') { throw 'Media stream did not survive malformed command.' }
    $writer.WriteLine('{"version":1,"type":"command","action":"shutdown"}')
    if (-not $process.WaitForExit(10000)) { throw 'Media helper failed to shut down.' }
    if ($process.ExitCode -ne 0) { throw "Media helper exited with $($process.ExitCode)." }
    Write-Host 'Media smoke check passed: launch, full renderer initialization, frame stream, invalid-version rejection, graceful shutdown. No playback command was executed.'
}
finally {
    if ($writer) { $writer.Dispose() }
    if ($reader) { $reader.Dispose() }
    if ($pipe) { $pipe.Dispose() }
    if ($process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}
