using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TaskbarLyrics.Core.Utilities;
using TaskbarLyrics.App;

namespace TaskBarWidgets.Media.Integration;

internal sealed class UiHostBridge : IAsyncDisposable
{
    private readonly MediaPipeServer _server;
    private readonly UiRequestDispatcher _requests;
    private Process? _process;
    private volatile bool _disposed;
    private int _timingLogs;
    private int _pollLogs;
    private bool _cardLyricsVisible;
    private long _cardLyricsSession;
    private readonly object _updateGate = new();
    private JsonElement? _pendingUpdate;
    private bool _sendingUpdate;
    internal int ProcessId => _process is { HasExited: false } process ? process.Id : 0;

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
    internal void SetCardLyricsVisible(bool visible, long session)
    {
        if (session < _cardLyricsSession) return;
        _cardLyricsSession = session; _cardLyricsVisible = visible;
    }
    internal void PublishCardLyrics(object frame)
    {
        if (!_disposed && _cardLyricsVisible)
            _server.Publish(new { version = 1, type = "command", action = "cardLyricsFrame", session = _cardLyricsSession, payload = frame });
    }
    private readonly string _name = $"TaskBarWidgets.UI.{Environment.ProcessId}";
    internal UiHostBridge(Func<JsonElement, CancellationToken, Task<object?>> request)
    {
        _requests = new UiRequestDispatcher(request, RecordTiming);
        _server = new MediaPipeServer(_name,
            () => new { version = 1, type = "command", action = "uiReady" },
            () => new { version = 1, type = "command", action = "uiReady" }, HandleRequestAsync);
        _server.Start();
        EnsureProcess();
    }
    private void EnsureProcess()
    {
        if (_disposed || _process is { HasExited: false }) return;
        _process?.Dispose();
        _cardLyricsSession = 0; _cardLyricsVisible = false;
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
        _process = Process.Start(start) ?? throw new IOException("WinUI 界面未能启动。");
        var launched = _process;
        launched.EnableRaisingEvents = true;
        launched.Exited += (_, _) =>
        {
            if (_disposed) return;
            try { Log.Warn($"WinUI process exited: {launched.ExitCode}. See UI/ui-error.log if present."); }
            catch (InvalidOperationException) { }
        };
    }
    internal Task SendAsync(string action, double x = 0, double y = 0, string? page = null, long entryWindow = 0, long gestureId = 0, int? toolWindowTheme = null)
    {
        EnsureProcess();
        if (_process is { HasExited: false }) AllowSetForegroundWindow(_process.Id);
        return _server.SendCommandAsync(new { version = 1, type = "command", action, x, y, page, entryWindow, gestureId, toolWindowTheme });
    }
    private Task HandleRequestAsync(JsonElement message)
    {
        if (_disposed || !message.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 128 } requestId) return Task.CompletedTask;
        if (_requests.TryPost(message, CompleteRequestAsync)) return Task.CompletedTask;
        // Only overload replies are awaited by the reader, keeping transport
        // backpressure bounded even if a client stops consuming responses.
        return _server.SendCommandAsync(new { version = 1, type = "command", action = "uiResult", requestId, success = false, error = "请求队列已满，请稍后重试。" });
    }
    private Task CompleteRequestAsync(UiRequestResult result, CancellationToken cancellationToken)
    {
        if (_disposed) return Task.CompletedTask;
        var requestId = result.Request.GetProperty("requestId").GetString();
        if (result.Error is not null)
            return _server.SendCommandAsync(new { version = 1, type = "command", action = "uiResult", requestId, success = false, error = result.Error.Message }, cancellationToken);
        var payload = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return _server.SendCommandAsync(new { version = 1, type = "command", action = "uiResult", requestId, success = true, payload }, cancellationToken);
    }
    private void RecordTiming(UiRequestTiming timing)
    {
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
