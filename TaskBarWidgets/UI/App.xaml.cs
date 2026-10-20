using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    private UpdateUiState _updateState = new();
    private bool _installConfirmationOpen, _uiReadyReported, _installAfterDownload;
    private JsonElement _quickCards;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _prewarmTimer;

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
        _connection.ConnectionChanged += OnBackendConnectionChanged;
        _connection.Start();
        _ = WatchHostAsync(hostPid);
        // User-facing windows are created only when the backend requests them.
        // Prewarm them once the UI has settled so the first click is warm.
        _prewarmTimer = _dispatcher.CreateTimer();
        _prewarmTimer.Interval = TimeSpan.FromMilliseconds(300);
        _prewarmTimer.IsRepeating = false;
        _prewarmTimer.Tick += (_, _) => _ = PrewarmCardsAsync();
        _prewarmTimer.Start();
    }

    // Build and render the enabled card windows while hidden. Any failure is
    // swallowed by CardGroup and the original lazy creation path still applies.
    private async Task PrewarmCardsAsync()
    {
        if (_stopping || _connection is null) return;
        try
        {
            _cards ??= new CardGroup(_connection, OpenSettings);
            if (_quickCards.ValueKind != JsonValueKind.Object)
            {
                // Media owns the quick-card configuration; reading it once lets
                // prewarm build exactly the cards this user has enabled.
                try
                {
                    var settings = await _connection.RequestAsync("getMediaSettings", cancellationToken: _lifetime.Token);
                    _cards.ApplyQuickCardsFromSettings(settings);
                }
                catch (Exception readError) when (readError is not OutOfMemoryException)
                {
                    // Keep the built-in defaults; the first toggle applies the
                    // real configuration before showing anything.
                    CardLifecycleLog.Write($"prewarm-settings-unavailable hr=0x{readError.HResult:X8}");
                }
            }
            await _cards.PrewarmAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { CardLifecycleLog.Write($"prewarm-start-failed hr=0x{exception.HResult:X8}"); }
    }

    private void OnBackendEvent(JsonElement message)
    {
        // Timestamped on the receiving thread so the timing log separates pipe
        // and dispatcher wait from UI-thread work.
        var receivedAt = DateTimeOffset.Now;
        _dispatcher?.TryEnqueue(() => HandleBackendEvent(message, receivedAt));
    }

    private void HandleBackendEvent(JsonElement message, DateTimeOffset receivedAt)
    {
        if (_stopping || _connection is null || !message.TryGetProperty("action", out var action)) return;
        var payload = message.TryGetProperty("payload", out var value) && value.ValueKind == JsonValueKind.Object ? value : message;
        switch (action.GetString())
        {
            case "togglePanel":
                CardLifecycleLog.Write($"toggle-received gesture={(message.TryGetProperty("gestureId", out var gesture) ? gesture.ToString() : "0")}");
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("x", out var x) && x.TryGetDouble(out var px) && double.IsFinite(px) &&
                    payload.TryGetProperty("y", out var y) && y.TryGetDouble(out var py) && double.IsFinite(py))
                {
                    if (payload.TryGetProperty("quickCards", out var quickCards) && quickCards.ValueKind == JsonValueKind.Object)
                        _quickCards = quickCards.Clone();
                    _cards ??= new CardGroup(_connection, OpenSettings);
                    _cards.ApplyQuickCards(_quickCards);
                    _cards.UpdateActionRequested -= OnUpdateActionRequested;
                    _cards.UpdateActionRequested += OnUpdateActionRequested;
                    _cards.SetUpdateState(_updateState);
                    if (_settings?.ThemePreview is { } preview) _cards.SetThemePreview(preview);
                    _cards.ApplyBackendTheme(payload);
                    var entryWindow = payload.TryGetProperty("entryWindow", out var entry) && entry.ValueKind == JsonValueKind.Number &&
                        entry.TryGetInt64(out var entryValue) && entryValue != 0 ? (nint)entryValue : 0;
                    var gestureId = message.TryGetProperty("gestureId", out var gestureValue) && gestureValue.TryGetInt64(out var id) ? id : 0;
                    _cards.BeginOpenTiming(gestureId, DateTimeOffset.Now - receivedAt);
                    _cards.ToggleAtPhysicalPoint(px, py, entryWindow);
                }
                break;
            case "openSettings":
                OpenSettings(payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("page", out var page)
                    && page.ValueKind == JsonValueKind.String ? page.GetString() : null);
                break;
            case "cardLyricsFrame":
                _cards?.ReceiveLyrics(message);
                break;
            case "uiReady":
                _ = RefreshUpdateStateAsync();
                break;
            case "updateStateChanged":
                AcceptUpdateState(payload);
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
            _settings.ThemePreviewChanged += theme => _cards?.SetThemePreview(theme);
            _settings.UpdateStateReceived += AcceptUpdateState;
            _settings.InstallUpdateRequested += OnInstallUpdateRequested;
            _settings.SetUpdateState(_updateState);
            _settings.Closed += (_, args) => { if (!args.Handled) _settings = null; };
        }
        _settings.ShowPage(page);
        _settings.Activate();
    }

    private void OnBackendConnectionChanged(bool connected)
    {
        if (connected) _dispatcher?.TryEnqueue(() => { if (!_stopping) _ = RefreshUpdateStateAsync(); });
    }

    private async Task RefreshUpdateStateAsync()
    {
        if (_connection is null || _stopping) return;
        try
        {
            AcceptUpdateState(await _connection.RequestAsync("getUpdateState", cancellationToken: _lifetime.Token));
            if (!_uiReadyReported)
            {
                _uiReadyReported = true;
                try { await _connection.RequestAsync("reportUiReady", cancellationToken: _lifetime.Token); }
                catch { _uiReadyReported = false; throw; }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void AcceptUpdateState(JsonElement payload)
    {
        var state = UpdateUiState.Parse(payload);
        if (_stopping || state is null || state.Revision <= _updateState.Revision) return;
        _updateState = state;
        _cards?.SetUpdateState(state);
        _settings?.SetUpdateState(state);
        if (_installAfterDownload)
        {
            if (state.Phase == "ready" && state.CanInstall) { _installAfterDownload = false; OnInstallUpdateRequested(); }
            else if (state.Phase is not ("downloading" or "verifying")) _installAfterDownload = false;
        }
    }

    // The card offers "下载并安装" before a package exists and "更新并重启"
    // once it is verified; both end in the same explicit install confirmation.
    private async void OnUpdateActionRequested()
    {
        if (_connection is null || _stopping) return;
        if (_updateState.Phase == "ready" && _updateState.CanInstall) { OnInstallUpdateRequested(); return; }
        if (_updateState.Phase != "available" || !_updateState.CanDownload) return;
        _installAfterDownload = true;
        try { AcceptUpdateState(await _connection.RequestAsync("downloadUpdate", null, _lifetime.Token)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _installAfterDownload = false;
            if (!_stopping) _settings?.ShowUpdateError(error.Message);
        }
    }

    private async void OnInstallUpdateRequested()
    {
        if (_installConfirmationOpen || !_updateState.CanInstall || _updateState.Phase != "ready" || _connection is null || _stopping) return;
        _installConfirmationOpen = true;
        var version = _updateState.LatestVersion;
        try
        {
            _cards?.Hide();
            // Check before navigation: changing the page disposes its weather editor.
            if (_settings?.HasPendingChanges == true)
            {
                _settings.Activate();
                await _settings.ShowUpdateDialogAsync(true, version);
                return;
            }
            OpenSettings("about");
            if (_settings is null) return;
            if (await _settings.ShowUpdateDialogAsync(false, version) != ContentDialogResult.Primary) return;
            if (_settings.HasPendingChanges || !_updateState.CanInstall || _updateState.Phase != "ready" || _updateState.LatestVersion != version) return;
            AcceptUpdateState(await _connection.RequestAsync("installUpdate", new { confirmed = true }, _lifetime.Token));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_stopping) _settings?.ShowUpdateError(error.Message); }
        finally { _installConfirmationOpen = false; }
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
        _prewarmTimer?.Stop();
        _prewarmTimer = null;
        _cards?.Dispose();
        _settings?.Shutdown();
        _lifetimeWindow?.Close();
        if (_connection is not null)
        {
            _connection.EventReceived -= OnBackendEvent;
            _connection.ConnectionChanged -= OnBackendConnectionChanged;
            _connection.Dispose();
        }
        _lifetime.Dispose();
        Exit();
    }
}
