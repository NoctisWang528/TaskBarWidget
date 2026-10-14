using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TaskbarLyrics.Core.Utilities;

namespace TaskBarWidgets.Media.Integration;

internal sealed class UiHostBridge : IAsyncDisposable
{
    private readonly MediaPipeServer _server;
    private readonly Func<JsonElement, Task<object?>> _request;
    private Process? _process;
    private bool _disposed;
    private readonly string _name = $"TaskBarWidgets.UI.{Environment.ProcessId}";
    internal UiHostBridge(Func<JsonElement, Task<object?>> request)
    {
        _request = request;
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
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var executable = Path.Combine(root, "UI", "TaskBarWidgets.UI.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("WinUI 界面文件缺失，请使用完整构建目录。", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
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
    internal Task SendAsync(string action, double x = 0, double y = 0, string? page = null)
    {
        EnsureProcess();
        if (_process is { HasExited: false }) AllowSetForegroundWindow(_process.Id);
        return _server.SendCommandAsync(new { version = 1, type = "command", action, x, y, page });
    }
    private async Task HandleRequestAsync(JsonElement message)
    {
        if (!message.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 128 } requestId) return;
        try
        {
            var result = await _request(message);
            var payload = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await _server.SendCommandAsync(new { version = 1, type = "command", action = "uiResult", requestId, success = true, payload });
        }
        catch (Exception exception)
        {
            Log.Warn($"WinUI request failed: {exception.Message}");
            await _server.SendCommandAsync(new { version = 1, type = "command", action = "uiResult", requestId, success = false, error = exception.Message });
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await _server.SendCommandAsync(new { version = 1, type = "command", action = "shutdown" }, timeout.Token); }
        catch (OperationCanceledException) { }
        await _server.DisposeAsync();
        _process?.Dispose(); // The UI also watches our PID; never terminate unrelated windows.
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
