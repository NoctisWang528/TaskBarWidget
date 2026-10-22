using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using TaskbarLyrics.App;
using TaskBarWidgets.Media.Diagnostics;

namespace TaskBarWidgets.Media.Integration;

internal sealed class MediaPipeServer : IAsyncDisposable
{
    internal const int MaximumMessageBytes = 4 * 1024 * 1024;
    private const string UiExecutableName = "TaskBarWidgets.UI.exe";
    private const int ImagePathCharacters = 1024;
    private readonly string _name;
    // Peer image expected on the WinUI bridge pipe; null when this server owns a pipe
    // whose client cannot be named up front (native host bridge, tests, probes).
    private readonly string? _expectedPeerPath;
    private int _peerGuardNotice;
    private readonly Func<object> _initialize;
    private readonly Func<object> _snapshot;
    private readonly Func<JsonElement, long, Task> _command;
    private readonly Action<bool, long>? _connectionChanged;
    private readonly CancellationTokenSource _stop = new();
    // Display snapshots are complete. Replacing one never loses state; commands use
    // a separate reliable bounded queue and are never coalesced.
    private readonly LatestDisplayQueue _outgoing = new();
    private Task? _run;
    // A scoped entry is a reply addressed to one connection generation. A zero
    // generation keeps the old behaviour for protocol notices that any current or
    // next client may consume.
    private readonly record struct OutboundCommand(object Payload, long Generation);
    private readonly Channel<OutboundCommand> _commands = Channel.CreateBounded<OutboundCommand>(16);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private long _connectionGeneration;
    private bool _disposed;

    internal MediaPipeServer(string name, Func<object> initialize, Func<object> snapshot, Func<JsonElement, long, Task> command,
        Action<bool, long>? connectionChanged = null)
    {
        _name = name;
        _initialize = initialize;
        _snapshot = snapshot;
        _command = command;
        _connectionChanged = connectionChanged;
        _expectedPeerPath = ExpectedPeerPath(name);
    }

    internal long ConnectionGeneration => Volatile.Read(ref _connectionGeneration);
    internal bool IsConnectionCurrent(long generation) => generation != 0 && ConnectionGeneration == generation;
    internal void Start() => _run = RunAsync(_stop.Token);
    internal void Publish(object frame) { _outgoing.Publish(frame); _wake.Writer.TryWrite(true); }
    internal async Task SendCommandAsync(object command, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await _commands.Writer.WriteAsync(new OutboundCommand(command, 0), linked.Token).ConfigureAwait(false);
        _wake.Writer.TryWrite(true);
    }
    internal async Task SendCommandAsync(long generation, object command, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await _commands.Writer.WriteAsync(new OutboundCommand(command, generation), linked.Token).ConfigureAwait(false);
        _wake.Writer.TryWrite(true);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                // Identify the peer before it owns a generation or receives the handshake.
                // A rejected client sees the same outcome as a failed connect: the accepted
                // pipe is closed at once, so its first read returns end-of-stream and the
                // UI reconnect loop retries instead of waiting on a silent connection.
                if (!AcceptClient(pipe)) continue;
                // Every accepted connection owns a server-side generation. Requests
                // capture it on the reader; retiring it on close makes their admission
                // stale without re-reading the current generation at execution time.
                var generation = Interlocked.Increment(ref _connectionGeneration);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    _outgoing.Clear();
                    await WriteAsync(pipe, _initialize(), connection.Token).ConfigureAwait(false);
                    var filter = new MediaFrameFilter();
                    await WriteAsync(pipe, filter.Filter(_snapshot()), connection.Token).ConfigureAwait(false);
                    _connectionChanged?.Invoke(true, generation);
                    var reader = ReadCommandsAsync(pipe, generation, connection.Token);
                    var writer = WriteFramesAsync(pipe, filter, generation, connection.Token);
                    await Task.WhenAny(reader, writer).ConfigureAwait(false);
                    connection.Cancel();
                    try { await Task.WhenAll(reader, writer).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
                }
                finally
                {
                    // Retire before notifying: once the bridge hears about the close,
                    // queued work from this generation must already be rejectable.
                    Interlocked.CompareExchange(ref _connectionGeneration, generation + 1, generation);
                    _connectionChanged?.Invoke(false, generation);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                TaskbarLyrics.Core.Utilities.Log.Warn($"Media pipe disconnected: {exception.Message}");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Named pipes are current-user only, which still admits any process running as the
    // same user. The WinUI bridge pipe is the only instance whose client can be named in
    // advance: the helper starts UI/TaskBarWidgets.UI.exe in the sibling UI directory and
    // the UI never reports its PID. The native host pipe (TaskBarWidgets.<native PID>)
    // and test/probe names belong to other clients, so validating them against the UI
    // executable would reject the native host; they keep the current-user-only guard.
    private bool AcceptClient(NamedPipeServerStream pipe)
    {
        if (_expectedPeerPath is null)
        {
            NotePeerGuard("skip", "no-expected-peer");
            return true;
        }

        var (peerPath, clientProcessId, stage, error) = ClientImagePath(pipe);
        // A separate process can present this image's own path - a second instance of
        // TaskBarWidgets.Media.exe reports an identical one - so the self-image shortcut
        // is scoped to a pipe client that is this very process (an in-process harness).
        // Any other client is judged against the expected peer path even when it runs
        // this image.
        var selfPath = clientProcessId == Environment.ProcessId ? SelfImagePath() : string.Empty;
        switch (EvaluatePeer(_expectedPeerPath, File.Exists(_expectedPeerPath), peerPath, selfPath))
        {
            case PeerVerdict.Accepted:
                return true;
            case PeerVerdict.ExpectedMissing:
                // A published helper always has the sibling UI executable, so a missing
                // file means an incomplete layout. The pipe then keeps only the
                // current-user-only guard; that is acceptable because a helper without its
                // UI cannot serve the card UI at all, but it must stay visible instead of
                // silently passing every peer.
                NotePeerGuard("fallback", "ui-executable-missing");
                return true;
            case PeerVerdict.SelfConnection:
                // Only a pipe client that is this very process reaches here (see the PID
                // gate above). A separate instance of the same image is judged against
                // the expected peer path instead and is rejected there.
                NotePeerGuard("skip", "self-connection");
                return true;
            default:
                var reason = string.IsNullOrEmpty(stage) ? "peer-path-mismatch" : $"{stage} error={error}";
                TaskbarLyrics.Core.Utilities.Log.Diagnostic("PIPE",
                    $"event=reject reason={reason} clientPid={clientProcessId}");
                return false;
        }
    }

    // PID -> image path through the existing process icon interop. Every failure is a
    // rejection with a stage token and Win32 error; paths stay out of the log.
    private static (string? ImagePath, int ProcessId, string Stage, int Error) ClientImagePath(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
            {
                return (null, 0, "client-pid", Marshal.GetLastWin32Error());
            }

            var processId = (int)clientProcessId;
            var handle = NativeIconInterop.OpenProcess(NativeIconInterop.ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return (null, processId, "open-process", Marshal.GetLastWin32Error());
            }

            try
            {
                var buffer = new char[ImagePathCharacters];
                var length = buffer.Length;
                if (!NativeIconInterop.QueryFullProcessImageName(handle, 0, buffer, ref length) || length <= 0)
                {
                    return (null, processId, "image-path", Marshal.GetLastWin32Error());
                }

                return (new string(buffer, 0, length), processId, string.Empty, 0);
            }
            finally
            {
                NativeIconInterop.CloseHandle(handle);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, 0, "peer-query-" + exception.GetType().Name, 0);
        }
    }

    private static string SelfImagePath() => Environment.ProcessPath ??
        Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName);

    // One notice per server instance: reconnects must not repeat the same line.
    private void NotePeerGuard(string outcome, string reason)
    {
        if (Interlocked.Exchange(ref _peerGuardNotice, 1) != 0) return;
        TaskbarLyrics.Core.Utilities.Log.Diagnostic("PIPE", $"event={outcome} reason={reason}");
    }

    internal static string? ExpectedPeerPath(string pipeName) =>
        ExpectedPeerPath(pipeName, AppContext.BaseDirectory, Environment.ProcessId);

    // The UI connects to the pipe named after the helper PID. Published layouts place
    // Media/ and UI/ side by side, so the expected peer is the sibling UI executable.
    internal static string? ExpectedPeerPath(string pipeName, string baseDirectory, int processId)
    {
        if (!string.Equals(pipeName, $"TaskBarWidgets.UI.{processId}", StringComparison.Ordinal)) return null;
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
        var directory = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetDirectoryName(directory);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "UI", UiExecutableName);
    }

    internal enum PeerVerdict
    {
        NoExpectation,
        ExpectedMissing,
        SelfConnection,
        Accepted,
        Rejected
    }

    // Pure decision point: the file-system fact and the two paths are parameters so
    // tests cover every branch without a UI process or a real named pipe.
    internal static PeerVerdict EvaluatePeer(string? expectedPath, bool expectedExists, string? peerPath, string selfPath)
    {
        if (string.IsNullOrEmpty(expectedPath)) return PeerVerdict.NoExpectation;
        // Without the sibling UI executable the helper cannot serve WinUI anyway; keep the
        // current-user-only pipe instead of locking out test/development layouts.
        if (!expectedExists) return PeerVerdict.ExpectedMissing;
        if (string.IsNullOrEmpty(peerPath)) return PeerVerdict.Rejected;
        if (!string.IsNullOrEmpty(selfPath) && PathsEqual(peerPath, selfPath)) return PeerVerdict.SelfConnection;
        return PathsEqual(peerPath, expectedPath) ? PeerVerdict.Accepted : PeerVerdict.Rejected;
    }

    internal static bool PathsEqual(string left, string right, Func<string, string?>? resolveLongPath = null) =>
        string.Equals(NormalizePath(left, resolveLongPath), NormalizePath(right, resolveLongPath), StringComparison.OrdinalIgnoreCase);

    // Full-path normalization with optional 8.3-to-long expansion. The resolver is
    // injectable so tests do not depend on short-name generation on the test volume.
    internal static string NormalizePath(string path, Func<string, string?>? resolveLongPath = null)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        var resolved = (resolveLongPath ?? TryResolveLongPath)(full);
        return (string.IsNullOrEmpty(resolved) ? full : resolved)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? TryResolveLongPath(string path)
    {
        try
        {
            var length = GetLongPathName(path, null, 0);
            if (length == 0 || length > 32768) return null;
            var buffer = new char[(int)length];
            length = GetLongPathName(path, buffer, length);
            return length == 0 || length > (uint)buffer.Length ? null : new string(buffer, 0, (int)length);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, [Out] char[]? longPath, uint bufferLength);

    private async Task WriteFramesAsync(Stream pipe, MediaFrameFilter filter, long generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_commands.Reader.TryRead(out var command))
            {
                // A reply produced for a retired connection is dropped instead of
                // being delivered to the client that reconnected in the meantime.
                if (command.Generation != 0 && command.Generation != generation) continue;
                await WriteAsync(pipe, command.Payload, cancellationToken).ConfigureAwait(false);
            }
            if (_outgoing.TryTake(out var frame) && frame is not null)
            {
                await WriteAsync(pipe, filter.Filter(frame), cancellationToken).ConfigureAwait(false);
                FootprintMetrics.NoteFrame();
                continue;
            }
            await _wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(Stream pipe, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Media frame exceeds protocol limit.");
        FootprintMetrics.NotePipeWrite(bytes.Length);
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { 10 }, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadCommandsAsync(Stream pipe, long generation, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var length = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (length == 0) return;
            for (var i = 0; i < length; ++i)
            {
                if (buffer[i] == 10)
                {
                    var bytes = message.ToArray();
                    message.SetLength(0);
                    try
                    {
                        using var json = JsonDocument.Parse(bytes);
                        if (IsCommand(json.RootElement)) await _command(json.RootElement.Clone(), generation).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
                    {
                        TaskbarLyrics.Core.Utilities.Log.Warn($"Rejected media command: {exception.Message}");
                    }
                }
                else
                {
                    if (message.Length >= MaximumMessageBytes) throw new InvalidDataException("Media command exceeds protocol limit.");
                    message.WriteByte(buffer[i]);
                }
            }
        }
    }

    internal static bool IsCommand(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1 &&
        value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "command" &&
        value.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _commands.Writer.TryComplete();
        _wake.Writer.TryComplete();
        if (_run is not null)
        {
            try { await _run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
