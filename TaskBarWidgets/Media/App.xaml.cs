using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.Core.Database;
using TaskbarLyrics.Core.Utilities;
using TaskBarWidgets.Media.Updates;
using System.Reflection;

namespace TaskbarLyrics.App;

public partial class App : System.Windows.Application
{
    public App() => throw new InvalidOperationException("The media helper requires its native host launch arguments.");
    private readonly Process _host;
    private readonly string _pipeName;
    private readonly bool? _portableData;
    private SettingsStore? _store;
    private MediaCoordinator? _media;
    private MediaPipeServer? _pipe;
    private NativeSettingsClient? _nativeSettings;
    private UiHostBridge? _ui;
    private UpdateCoordinator? _updates;
    private UpdateInstallBridge? _updateInstaller;
    private UpdateSnapshot _updateUnavailable = new() { Phase = "error", Error = "更新服务尚未准备好。" };
    private readonly AudioDeviceService _audio = new();
    private SettingsWindow? _settingsWindow;
    private SpectrumTuningWindow? _tuningWindow;
    private SmtcTimelineMonitorWindow? _timelineWindow;
    private readonly CancellationTokenSource _stop = new();
    private bool _shutdownRequested;
    internal AppSettings Settings { get; private set; } = new();

    internal App(Process host, string pipeName, bool? portableData = null)
    {
        _host = host; _pipeName = pipeName; _portableData = portableData;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        try
        {
            var applicationRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            AppDataPaths.ConfigureForApplicationRoot(applicationRoot, _portableData);
            var settingsPath = Path.Combine(AppDataPaths.RootDirectory, "media-settings.json");
            _store = new SettingsStore(settingsPath);
            Settings = _store.Load();
            Settings.UseFloatingWindow = false;
            Settings.StartWithWindows = false;
            Settings.AutoCheckUpdates = false;
            ApplyStartupForegroundColor(Settings);
            NativeWindowTheme.SetMode(Settings.ToolWindowTheme);
            SongSearchMapDbContext.InitializeDatabase();
            _media = new MediaCoordinator();
            _media.ApplySettings(Settings);
            _media.ApplyTuning(Settings.SpectrumTuning ?? SpectrumTuningSettings.CreateDefault());
            _pipe = new MediaPipeServer(_pipeName, () => new { version = 1, type = "initialize", html = MediaCoordinator.GetHtml() },
                _media.Snapshot, command => Dispatcher.InvokeAsync(() => HandleCommandAsync(command)).Task.Unwrap());
            _media.FrameReady += _pipe.Publish;
            _nativeSettings = new NativeSettingsClient((message, token) => _pipe.SendCommandAsync(message, token));
            var currentVersion = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";
            _ui = new UiHostBridge((command, token) => Dispatcher.InvokeAsync(() => HandleUiRequestAsync(command, token)).Task.Unwrap());
            try
            {
                _updates = new UpdateCoordinator(applicationRoot, currentVersion, Settings.ProductAutomaticUpdatesEnabled,
                    snapshot => _ui?.PublishUpdateState(snapshot));
                _updateInstaller = new UpdateInstallBridge(_updates, _nativeSettings, _ui, _host, applicationRoot, AppDataPaths.RootDirectory);
            }
            catch (Exception error)
            {
                Log.Warn("Update service unavailable: " + error.GetType().Name);
                _updateUnavailable = new UpdateSnapshot { Revision = 1, Phase = "error", CurrentVersion = currentVersion,
                    AutomaticEnabled = Settings.ProductAutomaticUpdatesEnabled,
                    Error = "更新服务初始化失败：" + error.Message, Message = "媒体功能可继续使用。请检查目录权限，或手动下载更新后重启。" };
            }
            _media.CardFrameReady += _ui.PublishCardLyrics;
            _pipe.Start();
            _media.Start();
            _updates?.Start();
            if (_updateInstaller is not null) TaskObserver.Observe(_updateInstaller.ReportPreviousFailureAsync(_stop.Token), "previous update outcome");
            TaskObserver.Observe(WatchHostAsync(), "parent lifecycle");
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception exception)
        {
            Log.Error($"Media helper startup failed: {exception}");
            Shutdown(1);
        }
    }

    private async Task WatchHostAsync()
    {
        try { await _host.WaitForExitAsync(_stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        await Dispatcher.InvokeAsync(ShutdownAsync).Task.Unwrap();
    }

    private async Task HandleCommandAsync(JsonElement command)
    {
        var action = command.GetProperty("action").GetString() ?? "";
        if (_shutdownRequested && action != "shutdown") return;
        switch (action)
        {
            case "nativeSettings": _nativeSettings?.Receive(command); break;
            case "trafficHistory": _nativeSettings?.Receive(command); break;
            case "weatherResult": _nativeSettings?.Receive(command); break;
            case "updateResult": _nativeSettings?.Receive(command); break;
            case "shutdown":
                _ = Dispatcher.BeginInvoke(() => TaskObserver.Observe(ShutdownAsync(), "helper shutdown"));
                break;
            case "togglePanel":
                if (TryNumber(command, "x", out var x) && TryNumber(command, "y", out var y) && _ui is not null)
                    await _ui.SendAsync("togglePanel", x, y, entryWindow: ValidatedEntryWindow(command),
                        gestureId: command.TryGetProperty("gestureId", out var gesture) && gesture.TryGetInt64(out var gestureId) ? gestureId : 0,
                        toolWindowTheme: (int)Settings.ToolWindowTheme, quickCards: QuickCards());
                break;
            case "openSettings":
                if (_ui is not null) await _ui.SendAsync("openSettings", page: command.TryGetProperty("page", out var page) ? page.GetString() : null);
                break;
            case "mediaVisible":
                if (command.TryGetProperty("visible", out var visible) && visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _media?.SetVisible(visible.GetBoolean());
                break;
            case "taskbarAppearance":
                if (_media is not null && command.TryGetProperty("payload", out var appearance) && appearance.ValueKind == JsonValueKind.Object)
                    _media.NativeAppearanceScript = "window.taskbarLyrics?.receive({version:1,type:'nativeAppearance',payload:" + appearance.GetRawText() + "});";
                break;
            default:
                if (_media is not null) await _media.CommandAsync(action, TryNumber(command, "positionSeconds", out var position) ? position : null);
                break;
        }
    }

    private long ValidatedEntryWindow(JsonElement command)
    {
        if (!command.TryGetProperty("entryWindow", out var value) || !value.TryGetInt64(out var handle) || handle <= 0) return 0;
        var window = (nint)handle;
        if (!IsWindow(window)) return 0;
        GetWindowThreadProcessId(window, out var process);
        return process == _host.Id ? handle : 0;
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private async Task<object?> HandleUiRequestAsync(JsonElement request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_media is null) throw new InvalidOperationException("媒体服务尚未准备好。");
        var action = request.GetProperty("action").GetString();
        var payload = request.TryGetProperty("payload", out var body) ? body : default;
        switch (action)
        {
            case "getUpdateState": return _updates?.Snapshot ?? _updateUnavailable;
            case "checkForUpdates": return _updates?.Check() ?? _updateUnavailable;
            case "downloadUpdate": return _updates?.Download() ?? _updateUnavailable;
            case "cancelUpdateDownload": return _updates?.CancelDownload() ?? _updateUnavailable;
            case "setAutomaticUpdates":
                var preference = Settings.Clone();
                preference.ProductAutomaticUpdatesEnabled = payload.GetProperty("enabled").GetBoolean();
                if (!SaveSettings(preference)) throw new IOException("更新设置保存失败，原设置保持不变。");
                return _updates?.Snapshot ?? _updateUnavailable;
            case "installUpdate":
                if (!payload.TryGetProperty("confirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("安装前必须确认重启。");
                return _updateInstaller?.Begin(_stop.Token) ?? _updateUnavailable;
            case "reportUiReady":
                await _nativeSettings!.RequestAsync("updateUiReady", cancellationToken: token);
                return new { success = true };
            case "subscribeCardLyrics":
                var cardVisible = payload.GetProperty("visible").GetBoolean();
                var cardSession = payload.GetProperty("session").GetInt64();
                _ui?.SetCardLyricsVisible(cardVisible, cardSession);
                return new { html = cardVisible ? MediaCoordinator.GetHtml() : "", snapshot = cardVisible ? _media.CardSnapshot : null };
            case "getState":
                var state = _media.ControlState;
                return new { state.Title, state.Artist, state.Source, state.PositionSeconds, state.DurationSeconds,
                    state.IsPlaying, state.CanPrevious, state.CanNext, state.CanPlayPause, state.CanSeek,
                    coverDataUrl = _media.CoverDataUrl, sources = (state.Sources ?? []).Select(source => new { source.Id,
                        label = source.Name switch { "QQMusic" => "QQ 音乐", "Netease" => "网易云音乐", "Kugou" => "酷狗音乐", "Browser" => BrowserSourceName(source.Id), _ => source.Name },
                        iconDataUrl = _media.SourceIcon(source) }),
                    state.SelectedSourceId, toolWindowTheme = (int)Settings.ToolWindowTheme };
            case "openPanel": OpenAudioPanel(); return new { success = true };
            case "getNativeSettings": return await GetNativeSettingsAsync();
            case "applyNativeSettings": return await ApplyNativeSettingsAsync(payload);
            case "getTrafficHistory": return await GetTrafficHistoryAsync(token);
            case "weatherCommand":
                if (_nativeSettings is null) throw new InvalidOperationException("天气服务尚未连接主程序。");
                return await _nativeSettings.RequestAsync("weatherCommand", payload.Clone(), token);
            case "getMediaSettings": return SettingsService().ReadServiceSettings(Settings);
            case "getFonts": return FontCatalogService.GetOptions();
            case "applyMediaSettings": return SettingsService().ApplyServiceSettings(Settings, payload);
            case "settingsCommand":
                return await SettingsService().ExecuteServiceCommandAsync(Settings, payload.GetProperty("action").GetString() ?? "",
                    payload.TryGetProperty("value", out var value) ? value.Clone() : null);
            case "mediaCommand":
                var command = payload.GetProperty("action").GetString() ?? "";
                if (command == "selectSource") command = "selectSource:" + payload.GetProperty("sourceId").GetString();
                if (!(command.StartsWith("selectSource:", StringComparison.Ordinal) || command is "playPause" or "previous" or "next" or "seek")) throw new ArgumentException("不支持的媒体操作。");
                await _media.CommandAsync(command, TryNumber(payload, "positionSeconds", out var position) ? position : null);
                return new { success = true };
            case "getAudio": return AudioPayload(await _audio.GetSnapshotAsync(token));
            case "selectDevice":
            case "setVolume":
            case "setMute":
                var flow = payload.GetProperty("flow").GetString();
                if (flow is not ("input" or "output")) throw new ArgumentException("设备类型无效。");
                var deviceId = payload.TryGetProperty("deviceId", out var id) ? id.GetString() : null;
                if (action == "selectDevice")
                {
                    if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("请选择音频设备。");
                    var result = await _audio.SetDefaultAsync(deviceId, flow == "input", true, token);
                    return AudioPayload(await _audio.GetSnapshotAsync(token), result.Succeeded, result.Message);
                }
                else if (action == "setVolume")
                {
                    if (!TryNumber(payload, "volume", out var volume) || volume < 0 || volume > 1) throw new ArgumentException("音量范围无效。");
                    await _audio.SetVolumeAsync(deviceId, volume * 100, input: flow == "input", cancellationToken: token);
                }
                else await _audio.SetMuteAsync(deviceId, payload.GetProperty("muted").GetBoolean(), input: flow == "input", cancellationToken: token);
                return AudioPayload(await _audio.GetSnapshotAsync(token));
            default: throw new ArgumentException("不支持的界面操作。");
        }
    }
    private static string BrowserSourceName(string id) => id.Contains("msedge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" :
        id.Contains("chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome" : id.Contains("firefox", StringComparison.OrdinalIgnoreCase) ? "Firefox" : "浏览器";

    private static object AudioPayload(AudioSnapshot snapshot, bool operationSucceeded = true, string? message = null)
    {
        static object Endpoint(AudioEndpoint item) => new { item.Id, item.Name, item.IsDefault, item.IsCommunications, volume = item.Volume / 100.0, item.IsMuted, item.CanControl };
        return new { outputs = snapshot.Outputs.Select(Endpoint), inputs = snapshot.Inputs.Select(Endpoint), operationSucceeded, message };
    }

    private static bool TryNumber(JsonElement value, string name, out double number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out number) && double.IsFinite(number);
    }

    private SettingsWindow SettingsService()
    {
        if (_media is null) throw new InvalidOperationException("媒体服务尚未准备好。");
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings.Clone(), _media.Offsets, () => Task.FromResult(_media.CurrentTrackContext),
                () => _media.Composition.CreateLyricDiagnosticRunner(Settings),
                (track, resolved, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(_media.TryApplyResolvedLyrics(track, resolved)); },
                (track, resolved, token) => _media.Composition.RememberResolvedLyricsAsync(track, resolved, token).AsTask(),
                _media.Composition.ClearLyricCache);
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; _media.ApplySettings(Settings); };
        }
        return _settingsWindow;
    }

    private void OpenSettings() { if (_ui is not null) TaskObserver.Observe(_ui.SendAsync("openSettings"), "WinUI settings"); }
    internal void OpenMonitorSettings() => OpenSettings();
    internal Task<JsonElement> GetNativeSettingsAsync() => _nativeSettings is not null
        ? _nativeSettings.RequestAsync("getNativeSettings")
        : Task.FromException<JsonElement>(new InvalidOperationException("主程序设置连接尚未准备好。"));
    internal Task<JsonElement> GetTrafficHistoryAsync(CancellationToken cancellationToken) => _nativeSettings is not null
        ? _nativeSettings.RequestAsync("getTrafficHistory", cancellationToken: cancellationToken)
        : Task.FromException<JsonElement>(new InvalidOperationException("流量历史连接尚未准备好。"));
    internal Task<JsonElement> ApplyNativeSettingsAsync(JsonElement values) => _nativeSettings is not null
        ? _nativeSettings.RequestAsync("applyNativeSettings", values.Clone())
        : Task.FromException<JsonElement>(new InvalidOperationException("主程序设置连接尚未准备好。"));
    internal void OpenAudioPanel()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var area = screen.WorkingArea;
        if (_ui is not null) TaskObserver.Observe(_ui.SendAsync("togglePanel", area.Left + area.Width / 2.0, area.Bottom,
            toolWindowTheme: (int)Settings.ToolWindowTheme, quickCards: QuickCards()), "WinUI cards");
    }
    private object QuickCards() => new { order = Settings.QuickCardOrder, enabled = Settings.QuickCardEnabled,
        resourceMode = Settings.QuickCardResourceMode.ToString(), weatherModules = Settings.WeatherCardModules,
        weatherReducedMotion = Settings.WeatherCardReducedMotion };
    internal void ShowLyricsWindow() => SendNativeCommand("showMedia");
    private void SendNativeCommand(string action)
    {
        if (_pipe is not null) TaskObserver.Observe(_pipe.SendCommandAsync(new { version = 1, type = "command", action }), "native command");
    }
    internal void PreviewSettings(AppSettings settings) => _media?.ApplySettings(settings);
    internal bool SaveSettings(AppSettings settings)
    {
        settings.UseFloatingWindow = false; settings.StartWithWindows = false; settings.AutoCheckUpdates = false;
        var updated = settings.Clone();
        if (_store?.Save(updated) != true) return false;
        Settings = updated;
        if (_updates is not null && _updates.Snapshot.AutomaticEnabled != Settings.ProductAutomaticUpdatesEnabled)
            _updates.SetAutomatic(Settings.ProductAutomaticUpdatesEnabled);
        if (_updates is null && _updateUnavailable.AutomaticEnabled != Settings.ProductAutomaticUpdatesEnabled)
        {
            _updateUnavailable = _updateUnavailable with { Revision = _updateUnavailable.Revision + 1, AutomaticEnabled = Settings.ProductAutomaticUpdatesEnabled };
            _ui?.PublishUpdateState(_updateUnavailable);
        }
        NativeWindowTheme.SetMode(Settings.ToolWindowTheme);
        _media?.ApplySettings(Settings);
        _media?.ApplyTuning(Settings.SpectrumTuning);
        return true;
    }

    internal void RetrySpectrumCapture() => _media?.RetrySpectrum();
    internal void OpenSpectrumTuningWindow()
    {
        if (_media is null) return;
        if (_tuningWindow is null)
        {
            _tuningWindow = new SpectrumTuningWindow(Settings.SpectrumTuning, tuning =>
            {
                Settings.SpectrumTuning = tuning.Clone(); _media.ApplyTuning(tuning); _store?.Save(Settings);
            });
            _tuningWindow.Closed += (_, _) => { _tuningWindow = null; _media.SetPreview(false); };
        }
        _media.SetPreview(true); _tuningWindow.Show(); _tuningWindow.Activate();
    }

    internal void OpenSmtcTimelineMonitorWindow()
    {
        if (_media is null) return;
        if (_timelineWindow is null)
        {
            _timelineWindow = new SmtcTimelineMonitorWindow(_media.Provider, _media.Lyrics);
            _timelineWindow.Closed += (_, _) => _timelineWindow = null;
        }
        _timelineWindow.Show(); _timelineWindow.Activate();
    }

    internal static void ApplyStartupForegroundColor(AppSettings settings) => ForegroundColorPolicy.ApplySelectedMode(settings, IsSystemUiUsingLightTheme());
    internal static bool IsApplicationUsingLightTheme() => ReadLightThemePreference("AppsUseLightTheme");
    internal static bool IsSystemUiUsingLightTheme() => ReadLightThemePreference("SystemUsesLightTheme");
    private static bool ReadLightThemePreference(string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue(value) is not int setting || setting != 0;
    }
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        NativeWindowTheme.RefreshSystemTheme();
        if (ForegroundColorPolicy.ApplySystemTheme(Settings, IsSystemUiUsingLightTheme())) _media?.ApplySettings(Settings);
    });

    protected override void OnExit(ExitEventArgs args)
    {
        _nativeSettings?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _stop.Cancel();
        if (_updates is not null) TaskObserver.Observe(_updates.DisposeAsync().AsTask(), "update service shutdown");
        _settingsWindow?.Close(); _tuningWindow?.Close(); _timelineWindow?.Close();
        _audio.Dispose();
        if (_ui is not null) TaskObserver.Observe(_ui.DisposeAsync().AsTask(), "WinUI shutdown");
        _media?.Dispose();
        if (_pipe is not null) TaskObserver.Observe(_pipe.DisposeAsync().AsTask(), "pipe shutdown");
        _stop.Dispose();
        base.OnExit(args);
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownRequested) return;
        _shutdownRequested = true;
        _nativeSettings?.Dispose();
        _stop.Cancel();
        if (_updates is not null) await _updates.DisposeAsync();
        // Window-close callbacks may restore previews. Run them while media services
        // still exist, then stop input producers before releasing those services.
        _settingsWindow?.Close();
        _tuningWindow?.Close();
        _timelineWindow?.Close();
        if (_ui is not null) await _ui.DisposeAsync();
        await _audio.DisposeAsync();
        _media?.Dispose();
        if (_pipe is not null) await _pipe.DisposeAsync();
        Shutdown();
    }
}

