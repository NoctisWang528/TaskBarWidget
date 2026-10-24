using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TaskbarLyrics.Core.Utilities;
using TaskbarLyrics.App;
using TaskBarWidgets.Media.Diagnostics;

namespace TaskBarWidgets.Media.Integration;

internal sealed class UiHostBridge : IAsyncDisposable
{
    private readonly MediaPipeServer _server;
    private readonly UiRequestDispatcher _requests;
    private Process? _process;
    private volatile bool _disposed;
    private int _timingLogs;
    private int _pollLogs;
    // Card lyric ownership is tied to a server connection generation, never to a
    // client session alone: a request that was queued for an earlier connection
    // must not become authoritative after the UI reconnects with a low session.
    private readonly object _cardGate = new();
    private bool _cardLyricsVisible;
    private long _cardLyricsSession;
    private long _cardLyricsOwner;
    private int _uiProcessEpoch;
    // Raised whenever the UI can no longer own a card subscription: the client
    // disconnected, the UI process exited, or a new UI epoch starts. The owner
    // drops the coordinator's card visibility with it so a dead session can
    // never keep lyric work alive.
    internal event Action? CardLyricsReleased;
    internal event Action<long>? ConnectionClosed;
    private readonly object _updateGate = new();
    private JsonElement? _pendingUpdate;
    private bool _sendingUpdate;
    internal int ProcessId => _process is { HasExited: false } process ? process.Id : 0;
    internal long ConnectionGeneration => _server.ConnectionGeneration;
    internal bool IsConnectionCurrent(long generation) => _server.IsConnectionCurrent(generation);
    internal bool CardLyricsVisible
    {
        get { lock (_cardGate) return _cardLyricsVisible; }
    }

    internal void PublishUpdateState(object snapshot)
    {
        lock (_updateGate)
        {
            if (_disposed) return;
            _pendingUpdate = JsonSerializer.SerializeToElement(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (_sendingUpdate) return;
            _sendingUpdate = true;
        }
        TaskObserver.Observe(PumpUpdateStateAsync(), "update state notification");
    }
    private async Task PumpUpdateStateAsync()
    {
        try
        {
            while (true)
            {
                JsonElement payload;
                lock (_updateGate)
                {
                    if (_disposed || _pendingUpdate is null) { _sendingUpdate = false; return; }
                    payload = _pendingUpdate.Value; _pendingUpdate = null;
                }
                await _server.SendCommandAsync(new { version = 1, type = "command", action = "updateStateChanged", payload });
            }
        }
        catch { lock (_updateGate) _sendingUpdate = false; throw; }
    }
    // The caller must pass the generation captured when the request arrived. The
    // session still orders subscriptions inside one connection; a connection that
    // owns no subscription starts that ordering over at the UI's session 1.
    internal bool TrySubscribeCardLyrics(bool visible, long session, long generation)
    {
        lock (_cardGate)
        {
            if (_disposed || !_server.IsConnectionCurrent(generation)) return false;
            if (generation != _cardLyricsOwner) _cardLyricsSession = 0;
            if (session < _cardLyricsSession) return false;
            _cardLyricsSession = session;
            _cardLyricsOwner = generation;
            _cardLyricsVisible = visible;
            return true;
        }
    }
    internal void BeginConnection(long generation)
    {
        var released = false;
        lock (_cardGate)
        {
            if (_cardLyricsOwner == generation) return;
            _cardLyricsOwner = 0;
            _cardLyricsSession = 0;
            if (_cardLyricsVisible) { _cardLyricsVisible = false; released = true; }
        }
        if (released) CardLyricsReleased?.Invoke();
    }
    // A delayed close callback is scoped to the connection that produced it. It
    // may clear that connection's own subscription, but never a newer owner's.
    internal void EndConnection(long generation)
    {
        var released = false;
        lock (_cardGate)
        {
            if (_cardLyricsOwner == 0 || _cardLyricsOwner > generation) return;
            _cardLyricsOwner = 0;
            _cardLyricsSession = 0;
            if (_cardLyricsVisible) { _cardLyricsVisible = false; released = true; }
        }
        if (released) CardLyricsReleased?.Invoke();
    }
    internal void ReleaseCardLyrics()
    {
        var released = false;
        lock (_cardGate)
        {
            _cardLyricsOwner = 0;
            _cardLyricsSession = 0;
            if (_cardLyricsVisible) { _cardLyricsVisible = false; released = true; }
        }
        if (released) CardLyricsReleased?.Invoke();
    }
    // Only the exit callback of the current UI process may release the card. A
    // late Exited event from a replaced process must leave the new UI untouched.
    // The epoch is checked under the card gate so a replacement process cannot
    // start a new subscription between the check and the release.
    internal void ReleaseCardLyricsForProcess(int epoch)
    {
        var released = false;
        lock (_cardGate)
        {
            if (Volatile.Read(ref _uiProcessEpoch) != epoch) return;
            _cardLyricsOwner = 0;
            _cardLyricsSession = 0;
            if (_cardLyricsVisible) { _cardLyricsVisible = false; released = true; }
        }
        if (released) CardLyricsReleased?.Invoke();
    }
    internal void PublishCardLyrics(object frame)
    {
        if (_disposed) return;
        long session;
        lock (_cardGate)
        {
            if (!_cardLyricsVisible) return;
            session = _cardLyricsSession;
        }
        _server.Publish(new { version = 1, type = "command", action = "cardLyricsFrame", session, payload = frame });
    }
    private readonly string _name;
    // Settings is started on demand: the constructor no longer launches WinUI,
    // because a resident settings process is exactly the memory cost this
    // architecture removes.
    internal UiHostBridge(Func<JsonElement, long, CancellationToken, Task<object?>> request, bool startUiProcess = false, string? pipeName = null)
    {
        _name = pipeName ?? $"TaskBarWidgets.UI.{Environment.ProcessId}";
        _server = new MediaPipeServer(_name,
            () => new { version = 1, type = "command", action = "uiReady" },
            () => new { version = 1, type = "command", action = "uiReady" }, HandleRequestAsync,
            OnConnectionChanged);
        _requests = new UiRequestDispatcher(request, RecordTiming, isCurrent: IsConnectionCurrent);
        _server.Start();
        if (startUiProcess) EnsureProcess();
    }
    private void OnConnectionChanged(bool connected, long generation)
    {
        if (connected) BeginConnection(generation);
        else
        {
            EndConnection(generation);
            ConnectionClosed?.Invoke(generation);
        }
    }
    // Health mode asks for the process without a settings command.
    internal void StartOnDemand() => EnsureProcess(healthOnly: true);

    private void EnsureProcess(bool healthOnly = false)
    {
        if (_disposed || _process is { HasExited: false }) return;
        _process?.Dispose();
        // A restarted UI is a new process epoch and a new subscription epoch.
        var epoch = Interlocked.Increment(ref _uiProcessEpoch);
        ReleaseCardLyrics();
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var executable = Path.Combine(root, "UI", "TaskBarWidgets.UI.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("WinUI 界面文件缺失，请使用完整构建目录。", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        // Use the same shared runtime installation that is already running this process.
        var runtime = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        start.Environment["DOTNET_ROOT"] = start.Environment["DOTNET_ROOT_X64"] = runtime;
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("DOTNET_ROLL_FORWARD", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.ArgumentList.Add("--host-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--pipe-name"); start.ArgumentList.Add(_name);
        if (healthOnly) start.ArgumentList.Add("--health-probe");
        _process = Process.Start(start) ?? throw new IOException("WinUI 界面未能启动。");
        var launched = _process;
        launched.EnableRaisingEvents = true;
        launched.Exited += (_, _) =>
        {
            ReleaseCardLyricsForProcess(epoch);
            if (_disposed) return;
            try { Log.Warn($"WinUI process exited: {launched.ExitCode}. See UI/ui-error.log if present."); }
            catch (InvalidOperationException) { }
        };
    }
    internal Task SendAsync(string action, double x = 0, double y = 0, string? page = null, long entryWindow = 0, long gestureId = 0,
        int? toolWindowTheme = null, object? quickCards = null)
    {
        EnsureProcess();
        if (_process is { HasExited: false }) AllowSetForegroundWindow(_process.Id);
        return _server.SendCommandAsync(new { version = 1, type = "command", action, x, y, page, entryWindow, gestureId, toolWindowTheme, quickCards });
    }
    private Task HandleRequestAsync(JsonElement message, long generation)
    {
        if (_disposed || !message.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 128 } requestId) return Task.CompletedTask;
        if (_requests.TryPost(message, generation, CompleteRequestAsync)) return Task.CompletedTask;
        // Only overload replies are awaited by the reader, keeping transport
        // backpressure bounded even if a client stops consuming responses.
        return _server.SendCommandAsync(generation, new { version = 1, type = "command", action = "uiResult", requestId, success = false, error = "请求队列已满，请稍后重试。" });
    }
    private Task CompleteRequestAsync(UiRequestResult result, CancellationToken cancellationToken)
    {
        // A result produced for a retired connection is dropped instead of being
        // delivered to whichever UI connected in the meantime.
        if (_disposed || !_server.IsConnectionCurrent(result.ConnectionGeneration)) return Task.CompletedTask;
        var requestId = result.Request.GetProperty("requestId").GetString();
        if (result.Error is not null)
            return _server.SendCommandAsync(result.ConnectionGeneration, new { version = 1, type = "command", action = "uiResult", requestId, success = false, error = result.Error.Message }, cancellationToken);
        var payload = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return _server.SendCommandAsync(result.ConnectionGeneration, new { version = 1, type = "command", action = "uiResult", requestId, success = true, payload }, cancellationToken);
    }
    private void RecordTiming(UiRequestTiming timing)
    {
        // Counted before the existing log throttling so every getState is measured.
        if (timing.Action == "getState") FootprintMetrics.NoteStateTiming(timing.OperationMilliseconds);
        if ((timing.Action is "getAudio" or "getState") && timing.QueueMilliseconds + timing.OperationMilliseconds < 100 && Interlocked.Increment(ref _pollLogs) > 4) return;
        if (Volatile.Read(ref _timingLogs) >= 128 || Interlocked.Increment(ref _timingLogs) > 128) return;
        Log.Diagnostic("UIREQUEST", $"action={timing.Action} lane={timing.Lane} queueMs={timing.QueueMilliseconds:F1} operationMs={timing.OperationMilliseconds:F1} outcome={timing.Outcome}");
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await _server.SendCommandAsync(new { version = 1, type = "command", action = "shutdown" }, timeout.Token); }
        catch (OperationCanceledException) { }
        await _requests.DisposeAsync();
        await _server.DisposeAsync();
        _process?.Dispose(); // The UI also watches our PID; never terminate unrelated windows.
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
