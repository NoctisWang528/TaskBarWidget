using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskBarWidgets.UI.Cards;

namespace TaskBarWidgets.UI;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private UiConnection? _connection;
    private SettingsWindow? _settings;
    private Window? _lifetimeWindow;
    private DispatcherQueue? _dispatcher;
    private bool _stopping;
    private bool _healthProbe;
    private UpdateUiState _updateState = new();
    private bool _installConfirmationOpen, _uiReadyReported, _installAfterDownload;

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
        _healthProbe = arguments.Contains("--health-probe");
        // Keep a hidden owner only while awaiting the first command or health reply.
        _lifetimeWindow = new Window { Title = "TaskBarWidgets UI lifecycle" };
        _lifetimeWindow.AppWindow.Hide();
        _connection = new UiConnection(arguments[pipeIndex + 1]);
        _connection.EventReceived += OnBackendEvent;
        _connection.ConnectionChanged += OnBackendConnectionChanged;
        _connection.Start();
        _ = WatchHostAsync(hostPid);
    }

    private static bool IsOnDemand(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty("quickCardResourceMode", out var mode) &&
        mode.ValueKind == JsonValueKind.String &&
        string.Equals(mode.GetString(), "OnDemand", StringComparison.OrdinalIgnoreCase);

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
            case "openSettings":
                OpenSettings(payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("page", out var page)
                    && page.ValueKind == JsonValueKind.String ? page.GetString() : null);
                break;
            case "confirmInstallUpdate":
                _healthProbe = false;
                Observe(ConfirmCardUpdateAsync());
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
        _healthProbe = false; // A user opening settings takes ownership from the health probe.
        // The cards live in the native process now; opening settings must still
        // dismiss them so the two surfaces never overlap.
        RequestCardHide();
        // The window is reused across opens; only the first creation pays for the shell.
        var reused = _settings is not null;
        if (_settings is null)
        {
            _settings = new SettingsWindow(_connection);
            // Draft appearance travels over the pipe to the native cards and is
            // never written to the committed configuration from here.
            _settings.ThemePreviewChanged += PreviewCardTheme;
            _settings.MicaPreviewChanged += state => PreviewCardMica(state.Preview);
            _settings.UpdateStateReceived += AcceptUpdateState;
            _settings.InstallUpdateRequested += OnInstallUpdateRequested;
            _settings.SetUpdateState(_updateState);
            // The settings process exists only while settings is open. Save and
            // discard both run inside the window before Closed fires, so a deferred
            // save still finishes; only an unfinished save keeps the process alive.
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                if (!_stopping) Stop();
            };
        }
        // A second open command must not dispose a weather editor or move away
        // from an active save before the shared update path can guard it.
        if (!reused || !_settings.HasPendingChanges) _settings.ShowPage(page);
        _settings.Activate();
        UiDiagnosticLog.Write("ui-lifecycle.log", $"settings-shown page={page ?? "default"} reused={reused}");
    }

    private void PreviewCardTheme(ElementTheme theme)
    {
        if (_connection is null || _stopping) return;
        if (theme == ElementTheme.Default && _settings?.Content is FrameworkElement root) theme = root.ActualTheme;
        Observe(_connection.RequestAsync("previewCardTheme",
            new { dark = theme == ElementTheme.Dark, enabled = true }, _lifetime.Token));
    }

    private void PreviewCardMica(Cards.CardMicaAppearance? preview)
    {
        if (_connection is null || _stopping) return;
        Observe(_connection.RequestAsync("previewCardMica", preview is { } value
            ? new
            {
                enabled = true,
                vivid = value.Vivid,
                tintOpacity = value.Tuning.LightTintOpacity,
                luminosityOpacity = value.Tuning.LightLuminosityOpacity,
                darkTintOpacity = value.Tuning.DarkTintOpacity,
                darkLuminosityOpacity = value.Tuning.DarkLuminosityOpacity,
            }
            : new { enabled = false }, _lifetime.Token));
    }

    // Fire-and-forget pipe work never surfaces as an unobserved exception.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void RequestCardHide()
    {
        if (_connection is null || _stopping) return;
        Observe(_connection.RequestAsync("hideCards", null, _lifetime.Token));
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
                if (_healthProbe && _settings is null) Stop();
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task ConfirmCardUpdateAsync()
    {
        await RefreshUpdateStateAsync();
        if (_stopping) return;
        if (_updateState.Phase == "ready" && _updateState.CanInstall) OnInstallUpdateRequested();
        else OpenSettings("about");
    }

    private void AcceptUpdateState(JsonElement payload)
    {
        var state = UpdateUiState.Parse(payload);
        if (_stopping || state is null || state.Revision <= _updateState.Revision) return;
        _updateState = state;
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
            RequestCardHide();
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
