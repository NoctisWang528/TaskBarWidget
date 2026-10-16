using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TaskBarWidgets.UI;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private UiConnection? _connection;
    private CardGroup? _cards;
    private SettingsWindow? _settings;
    private Window? _lifetimeWindow;
    private DispatcherQueue? _dispatcher;
    private bool _stopping;

    public App()
    {
        UnhandledException += (_, args) =>
        {
            LogFailure(args.Exception);
            args.Handled = true;
            Stop();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogFailure(args.ExceptionObject);
        InitializeComponent();
    }

    private static void LogFailure(object exception)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-error.log"), $"{DateTimeOffset.Now:o}\n{exception}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--validate-templates"))
        {
            _ = UiTemplateProbe.RunAsync(this, arguments);
            return;
        }
        var hostIndex = Array.IndexOf(arguments, "--host-pid");
        var pipeIndex = Array.IndexOf(arguments, "--pipe-name");
        if (hostIndex < 0 || hostIndex + 1 >= arguments.Length ||
            !int.TryParse(arguments[hostIndex + 1], out var hostPid) || hostPid <= 0 ||
            pipeIndex < 0 || pipeIndex + 1 >= arguments.Length ||
            arguments[pipeIndex + 1] != $"TaskBarWidgets.UI.{hostPid}")
        {
            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        // Keep the UI process alive when the last user window closes; never activate this host.
        _lifetimeWindow = new Window { Title = "TaskBarWidgets UI lifecycle" };
        _lifetimeWindow.AppWindow.Hide();
        _connection = new UiConnection(arguments[pipeIndex + 1]);
        _connection.EventReceived += OnBackendEvent;
        _connection.Start();
        _ = WatchHostAsync(hostPid);
        // User-facing windows are created only when the backend requests them.
    }

    private void OnBackendEvent(JsonElement message)
    {
        _dispatcher?.TryEnqueue(() => HandleBackendEvent(message));
    }

    private void HandleBackendEvent(JsonElement message)
    {
        if (_stopping || _connection is null || !message.TryGetProperty("action", out var action)) return;
        var payload = message.TryGetProperty("payload", out var value) && value.ValueKind == JsonValueKind.Object ? value : message;
        switch (action.GetString())
        {
            case "togglePanel":
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("x", out var x) && x.TryGetDouble(out var px) && double.IsFinite(px) &&
                    payload.TryGetProperty("y", out var y) && y.TryGetDouble(out var py) && double.IsFinite(py))
                {
                    _cards ??= new CardGroup(_connection, () => OpenSettings(null));
                    var entryWindow = payload.TryGetProperty("entryWindow", out var entry) && entry.ValueKind == JsonValueKind.Number &&
                        entry.TryGetInt64(out var entryValue) && entryValue != 0 ? (nint)entryValue : 0;
                    _cards.ToggleAtPhysicalPoint(px, py, entryWindow);
                }
                break;
            case "openSettings":
                OpenSettings(payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("page", out var page)
                    && page.ValueKind == JsonValueKind.String ? page.GetString() : null);
                break;
            case "shutdown":
                Stop();
                break;
        }
    }

    private void OpenSettings(string? page)
    {
        if (_connection is null) return;
        _cards?.Hide();
        if (_settings is null)
        {
            _settings = new SettingsWindow(_connection);
            _settings.Closed += (_, args) => { if (!args.Handled) _settings = null; };
        }
        _settings.ShowPage(page);
        _settings.Activate();
    }

    private async Task WatchHostAsync(int hostPid)
    {
        try
        {
            using var host = Process.GetProcessById(hostPid);
            await host.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (ArgumentException) { }
        _dispatcher?.TryEnqueue(Stop);
    }

    private void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _lifetime.Cancel();
        _cards?.Dispose();
        _settings?.Shutdown();
        _lifetimeWindow?.Close();
        if (_connection is not null)
        {
            _connection.EventReceived -= OnBackendEvent;
            _connection.Dispose();
        }
        _lifetime.Dispose();
        Exit();
    }
}
