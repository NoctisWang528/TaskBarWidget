using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using TaskBarWidgets.Media.Diagnostics;
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
    private readonly string? _dataRoot;
    private SettingsStore? _store;
    private MediaCoordinator? _media;
    private MediaPipeServer? _pipe;
    private NativeSettingsClient? _nativeSettings;
    private UiHostBridge? _ui;
    private NativeCardBridge? _cards;
    private CardSnapshotBuilder? _cardSnapshot;
    private AudioSnapshot? _lastAudio;
    private readonly SemaphoreSlim _audioRequests = new(1, 1);
    private bool _cardRefreshRunning, _cardDetailsRunning, _cardWasVisible;
    private DateTimeOffset _nextCardAudio, _nextCardWeather, _nextCardTraffic;
    private System.Windows.Threading.DispatcherTimer? _cardTimer;
    private UpdateCoordinator? _updates;
    private UpdateInstallBridge? _updateInstaller;
    private UpdateSnapshot _updateUnavailable = new() { Phase = "error", Error = "更新服务尚未准备好。" };
    private StorageCommandService? _storage;
    private readonly AudioDeviceService _audio = new();
    private MediaSettingsService? _settingsService;
    private readonly CancellationTokenSource _stop = new();
    private bool _shutdownRequested;
    // Per-connection artwork version memory for getState: see the artworkDelta branch.
    private readonly ArtworkDeltaState _artwork = new();
    internal AppSettings Settings { get; private set; } = new();

    internal App(Process host, string pipeName, string? dataRoot = null)
    {
        _host = host; _pipeName = pipeName; _dataRoot = dataRoot;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        try
        {
            var applicationRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            DataStartup.Prepare(_dataRoot ?? AppDataPaths.ResolveForApplicationRoot(applicationRoot), applicationRoot);
            _storage = new StorageCommandService(applicationRoot, AppDataPaths.RootDirectory);
            // The startup probe above owns legacy migration; here only refresh
            // the in-memory status after migration/restore completed.
            _ = _storage.Status();
            var settingsPath = AppDataPaths.MediaSettingsPath;
            FactoryDefaults.EnsureSeeded(settingsPath);
            _store = new SettingsStore(settingsPath);
            Settings = _store.Load(IsApplicationUsingLightTheme());
            Settings.UseFloatingWindow = false;
            Settings.StartWithWindows = false;
            Settings.AutoCheckUpdates = false;
            ApplyStartupForegroundColor(Settings);
            NativeWindowTheme.SetMode(Settings.ToolWindowTheme);
            SongSearchMapDbContext.InitializeDatabase();
            _media = new MediaCoordinator();
            _media.ApplySettings(Settings);
            _media.ApplyTuning(Settings.SpectrumTuning ?? SpectrumTuningSettings.CreateDefault());
            _pipe = new MediaPipeServer(_pipeName, MediaProtocol.Initialize,
                _media.Snapshot, (command, _) => Dispatcher.InvokeAsync(() => HandleCommandAsync(command)).Task.Unwrap());
            _media.FrameReady += _pipe.Publish;
            _nativeSettings = new NativeSettingsClient((message, token) => _pipe.SendCommandAsync(message, token));
            var currentVersion = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";
            _ui = new UiHostBridge((command, generation, token) => Dispatcher.InvokeAsync(() => HandleUiRequestAsync(command, generation, token)).Task.Unwrap());
            // The native card front-end is a second, card-only client. It shares
            // this one implementation of audio/media/weather/history so the two
            // front-ends can never disagree about device state.
            _cardSnapshot = new CardSnapshotBuilder();
            _cards = new NativeCardBridge($"TaskBarWidgets.Cards.{_host.Id}",
                token => Dispatcher.InvokeAsync(BuildCardSnapshot, System.Windows.Threading.DispatcherPriority.Background, token).Task,
                (request, generation, token) => Dispatcher.InvokeAsync(() =>
                    HandleCardRequestAsync(request, generation, token)).Task.Unwrap());
            _cards.VisibilityChanged += () => _ = Dispatcher.InvokeAsync(ApplyCardLyricsVisibility);
            _cards.Start();
            _cardTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(400),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => TaskObserver.Observe(RefreshVisibleCardsAsync(), "card state refresh"), Dispatcher);
            // The signal only says "re-read the bridge". Applying the authoritative
            // state on the dispatcher keeps a late release from overriding a newer
            // subscription that was accepted after the disconnect.
            _ui.CardLyricsReleased += () => _ = Dispatcher.InvokeAsync(ApplyCardLyricsVisibility);
            _ui.ConnectionClosed += generation => _ = Dispatcher.InvokeAsync(() =>
            {
                if (_ui.ConnectionGeneration != generation + 1) return;
                TaskObserver.Observe(ForwardToCards("cardMicaPreview", JsonSerializer.SerializeToElement(new { enabled = false })), "reset card material");
                TaskObserver.Observe(ForwardToCards("cardThemePreview", JsonSerializer.SerializeToElement(new { enabled = false })), "reset card theme");
            });
            try
            {
                _updates = new UpdateCoordinator(applicationRoot, currentVersion,
                    Settings.ProductAutomaticUpdatesEnabled,
                    snapshot =>
                    {
                        _cards?.PublishSnapshot();
                        _ui?.PublishUpdateState(snapshot);
                    });
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
            _media.CardFrameReady += _ => _cards?.PublishSnapshot();
            _pipe.Start();
            _media.Start();
            _updates?.Start();
            if (_updateInstaller is not null) TaskObserver.Observe(_updateInstaller.ReportPreviousFailureAsync(_stop.Token), "previous update outcome");
            TaskObserver.Observe(WatchHostAsync(), "parent lifecycle");
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            FootprintMetrics.Start();
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
                if (TryNumber(command, "x", out var x) && TryNumber(command, "y", out var y))
                    await ToggleNativeCardsAsync(x, y);
                break;
            case "openSettings":
                if (_ui is not null) await _ui.SendAsync("openSettings", page: command.TryGetProperty("page", out var page) ? page.GetString() : null);
                break;
            case "startUiHealth":
                // Health mode is the only path that starts settings without a user
                // action; the window itself stays hidden until the user opens it.
                _ui?.StartOnDemand();
                break;
            case "hideCards":
                // The settings window owns dismissal: native cards hide, but the
                // WinUI cards no longer exist to hide.
                await ForwardToCards("hideCards", default);
                break;
            case "previewCardMica":
                await ForwardToCards("cardMicaPreview",
                    command.TryGetProperty("payload", out var micaPreview) ? micaPreview : default);
                break;
            case "previewCardTheme":
                await ForwardToCards("cardThemePreview",
                    command.TryGetProperty("payload", out var themePreview) ? themePreview : default);
                break;
            case "mediaVisible":
                if (command.TryGetProperty("visible", out var visible) && visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _media?.SetVisible(visible.GetBoolean());
                break;
            case "taskbarAppearance":
                if (_media is not null && command.TryGetProperty("payload", out var appearance) && appearance.ValueKind == JsonValueKind.Object)
                    _media.NativeAppearance = appearance.Clone();
                break;
            default:
                if (_media is not null) await _media.CommandAsync(action, TryNumber(command, "positionSeconds", out var position) ? position : null);
                break;
        }
    }

    // ---- native card channel -------------------------------------------------

    internal object BuildCardSnapshot() => _cardSnapshot?.Build(Settings, _media!, _lastAudio,
        _updates?.Snapshot ?? _updateUnavailable, _cards?.Connected == true, (int)Settings.ToolWindowTheme)
        ?? new { connected = false };

    // The native card front-end only gets the actions its widgets need: audio,
    // media transport, weather status/refresh, history, lyrics, update and the
    // settings/update-confirm entry points. Everything else stays UI-only.
    private async Task<object?> HandleCardRequestAsync(JsonElement request, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_cards is null || !_cards.IsConnectionCurrent(generation))
            throw new OperationCanceledException("卡片连接已失效。");
        if (_media is null) throw new InvalidOperationException("媒体服务尚未准备好。");
        var action = request.GetProperty("action").GetString() ?? "";
        // v1 clients used flat arguments; media commands need a separate inner
        // action, so the canonical request is { action, payload: { ... } }.
        var payload = request.TryGetProperty("payload", out var arguments) && arguments.ValueKind == JsonValueKind.Object
            ? arguments : request;
        switch (action)
        {
            case "cardSnapshot":
                _cards.PublishSnapshot();
                return new { success = true };
            case "getState":
                return BuildCardSnapshot();
            case "getUpdateState":
                return _cardSnapshot?.DescribeUpdate(_updates?.Snapshot ?? _updateUnavailable)
                    ?? _updateUnavailable;
            case "downloadUpdate": return _cardSnapshot!.DescribeUpdate(_updates?.Download() ?? _updateUnavailable);
            case "cancelUpdateDownload": return _cardSnapshot!.DescribeUpdate(_updates?.CancelDownload() ?? _updateUnavailable);
            case "installUpdate":
                // Installation keeps its single confirmation path: the card starts
                // the settings process on demand and the user confirms there. The
                // card never installs on its own.
                if (_ui is not null) await _ui.SendAsync("confirmInstallUpdate");
                return _cardSnapshot!.DescribeUpdate(_updates?.Snapshot ?? _updateUnavailable);
            case "openSettings":
                // Starts the WinUI settings process on demand; it exits when the
                // last settings window closes.
                var settingsPage = payload.TryGetProperty("page", out var cardPage) && cardPage.ValueKind == JsonValueKind.String
                    ? cardPage.GetString() : null;
                if (_ui is not null) await _ui.SendAsync("openSettings", page: settingsPage);
                return new { success = true };
            case "getAudio":
            case "selectDevice":
            case "setVolume":
            case "setMute":
                return await HandleAudioRequestAsync(action, payload, token, () => _cards.IsConnectionCurrent(generation));
            case "mediaCommand":
            {
                var command = payload.GetProperty("action").GetString() ?? "";
                if (command == "selectSource")
                    command = "selectSource:" + payload.GetProperty("sourceId").GetString();
                if (!(command.StartsWith("selectSource:", StringComparison.Ordinal) ||
                    command is "playPause" or "previous" or "next" or "seek"))
                    throw new ArgumentException("不支持的媒体操作。");
                await _media.CommandAsync(command,
                    TryNumber(payload, "positionSeconds", out var position) ? position : null);
                // The reply is the media block itself: the card applies it directly.
                return _cardSnapshot!.DescribeMedia(_media);
            }
            case "getTrafficHistory":
            {
                var history = await GetTrafficHistoryAsync(token);
                var traffic = _cardSnapshot!.SetTraffic(history);
                _cards.PublishSnapshot();
                return traffic;
            }
            case "weatherCommand":
            {
                if (_nativeSettings is null) throw new InvalidOperationException("天气服务尚未连接主程序。");
                var operation = payload.GetProperty("operation").GetString();
                if (operation is not ("status" or "refresh")) throw new ArgumentException("不支持的卡片天气操作。");
                var result = await _nativeSettings.RequestAsync("weatherCommand", payload.Clone(), token);
                while (result.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.True)
                {
                    var jobId = result.GetProperty("jobId").GetInt64();
                    await Task.Delay(350, token);
                    result = await _nativeSettings.RequestAsync("weatherCommand", JsonSerializer.SerializeToElement(new { operation = "result", jobId }), token);
                }
                var weather = _cardSnapshot!.SetWeather(result);
                _cards.PublishSnapshot();
                if (result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                    throw new InvalidOperationException(result.GetProperty("error").GetString() ?? "天气刷新失败。");
                return weather;
            }
            case "subscribeCardLyrics":
            {
                var visible = payload.GetProperty("visible").GetBoolean();
                var session = payload.TryGetProperty("session", out var sessionValue) ? sessionValue.GetInt64() : 0;
                if (!_cards.TrySubscribeCardLyrics(visible, session, generation)) throw new OperationCanceledException("卡片订阅已失效。");
                ApplyCardLyricsVisibility();
                _cards.PublishSnapshot();
                return new { success = true };
            }
            default:
                throw new ArgumentException("不支持的卡片操作。");
        }
    }

    // Runs on the dispatcher. Reading the bridge's visible flag here, instead of
    // pushing a captured boolean from the pipe thread, makes the last state win.
    private void ApplyCardLyricsVisibility()
    {
        _media?.SetCardVisible(_cards?.CardLyricsVisible == true || _ui?.CardLyricsVisible == true);
        var visible = _cards?.CardLyricsVisible == true;
        if (visible && !_cardWasVisible)
        {
            _nextCardAudio = _nextCardWeather = _nextCardTraffic = DateTimeOffset.MinValue;
            TaskObserver.Observe(RefreshVisibleCardsAsync(), "card initial refresh");
        }
        _cardWasVisible = visible;
    }

    // One asynchronous refresh at a time, only while the group is visible. Audio
    // shares the mutation/readback gate with both front-ends; weather/history do
    // not hold it. Snapshot coalescing happens on the writer, not on this timer.
    private async Task RefreshVisibleCardsAsync()
    {
        if (_shutdownRequested || _cards?.CardLyricsVisible != true || _cardRefreshRunning) return;
        _cardRefreshRunning = true;
        try
        {
            _cards.PublishSnapshot(); // Controls/position and completed source icons, even during a slow audio read.
            if (!_cardDetailsRunning) TaskObserver.Observe(RefreshCardDetailsAsync(), "card details refresh");
            if (DateTimeOffset.UtcNow >= _nextCardAudio)
            {
                _nextCardAudio = DateTimeOffset.UtcNow.AddSeconds(1);
                await HandleAudioRequestAsync("getAudio", default, _stop.Token);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            if (!_shutdownRequested) Log.Warn("Card audio refresh failed: " + error.GetType().Name);
        }
        finally { _cardRefreshRunning = false; }
    }

    private async Task RefreshCardDetailsAsync()
    {
        if (_shutdownRequested || _cards?.CardLyricsVisible != true || _cardDetailsRunning) return;
        _cardDetailsRunning = true;
        try
        {
            var generation = _cards.ConnectionGeneration;
            if (_nativeSettings is null) return;
            if (DateTimeOffset.UtcNow >= _nextCardWeather)
            {
                _nextCardWeather = DateTimeOffset.UtcNow.AddSeconds(5);
                var weather = await _nativeSettings.RequestAsync("weatherCommand", JsonSerializer.SerializeToElement(new { operation = "status" }), _stop.Token);
                if (!_cards.IsConnectionCurrent(generation) || !_cards.CardLyricsVisible) return;
                _cardSnapshot!.SetWeather(weather);
                _cards.PublishSnapshot();
            }
            if (DateTimeOffset.UtcNow >= _nextCardTraffic)
            {
                _nextCardTraffic = DateTimeOffset.UtcNow.AddMinutes(1);
                var traffic = await GetTrafficHistoryAsync(_stop.Token);
                if (!_cards.IsConnectionCurrent(generation) || !_cards.CardLyricsVisible) return;
                _cardSnapshot!.SetTraffic(traffic);
                _cards.PublishSnapshot();
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            if (!_shutdownRequested) Log.Warn("Card refresh failed: " + error.GetType().Name);
        }
        finally { _cardDetailsRunning = false; }
    }

    // The gate covers the entire operation plus readback, rather than individual
    // COM calls. UI and native cards cannot interleave another mutation between
    // a write and the state returned for that write.
    private async Task<object> HandleAudioRequestAsync(string action, JsonElement payload, CancellationToken token, Func<bool>? isCurrent = null)
    {
        await _audioRequests.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (isCurrent?.Invoke() == false) throw new OperationCanceledException("界面连接已失效。");
            bool succeeded = true;
            string? message = null;
            if (action != "getAudio")
            {
                var flow = payload.GetProperty("flow").GetString();
                if (flow is not ("input" or "output")) throw new ArgumentException("设备类型无效。");
                var deviceId = payload.TryGetProperty("deviceId", out var id) ? id.GetString() : null;
                if (action == "selectDevice")
                {
                    if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("请选择音频设备。");
                    var result = await _audio.SetDefaultAsync(deviceId, flow == "input", true, token);
                    succeeded = result.Succeeded; message = result.Message;
                }
                else if (action == "setVolume")
                {
                    if (!TryNumber(payload, "volume", out var volume) || volume < 0 || volume > 1) throw new ArgumentException("音量范围无效。");
                    await _audio.SetVolumeAsync(deviceId, volume * 100, input: flow == "input", cancellationToken: token);
                }
                else await _audio.SetMuteAsync(deviceId, payload.GetProperty("muted").GetBoolean(), input: flow == "input", cancellationToken: token);
            }
            var snapshot = await _audio.GetSnapshotAsync(token);
            if (action != "getAudio" || _lastAudio is null || !snapshot.Outputs.SequenceEqual(_lastAudio.Outputs) ||
                !snapshot.Inputs.SequenceEqual(_lastAudio.Inputs))
            {
                _lastAudio = snapshot;
                _cardSnapshot!.BumpAudio();
                _cards?.PublishSnapshot();
            }
            return _cardSnapshot!.DescribeAudio(snapshot, succeeded, message);
        }
        finally { _audioRequests.Release(); }
    }

    private async Task<object?> HandleUiRequestAsync(JsonElement request, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The generation was captured by the pipe reader. Re-checking the captured
        // value here closes the gap between admission and dispatcher execution.
        if (_ui is null || !_ui.IsConnectionCurrent(generation))
            throw new OperationCanceledException("界面连接已失效。");
        if (_media is null) throw new InvalidOperationException("媒体服务尚未准备好。");
        var action = request.GetProperty("action").GetString();
        var payload = request.TryGetProperty("payload", out var body) ? body : default;
        switch (action)
        {
            case "getUpdateState": return _updates?.Snapshot ?? _updateUnavailable;
            case "getDataStatus": return _storage?.Status() ?? throw new InvalidOperationException("数据服务尚未准备好。");
            case "storageCommand":
            {
                var storageAction = payload.GetProperty("action").GetString() ?? "";
                return storageAction switch
                {
                    "backup" => await _storage!.BackupAsync(
                        payload.TryGetProperty("path", out var backupPath) ? backupPath.GetString() : null, token),
                    "scheduleRestore" => _storage!.ScheduleRestore(payload.GetProperty("path").GetString()!),
                    "importLegacy" => _storage!.ScheduleLegacyImport(),
                    "clearCache" => _storage!.ClearCache(),
                    "openDataFolder" => OpenDataFolder(),
                    _ => throw new ArgumentException("不支持的数据操作。")
                };
            }
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
                // The bridge ties ownership to the connection generation; the client
                // session still orders subscriptions inside one connection.
                if (_ui is null || !_ui.TrySubscribeCardLyrics(cardVisible, cardSession, generation))
                    return new { snapshot = (object?)null };
                ApplyCardLyricsVisibility();
                return new { snapshot = cardVisible ? _media.CardSnapshot : null };
            case "getState":
            {
                var state = _media.ControlState;
                var coverDataUrl = _media.CoverDataUrl;
                // The card polls every 400 ms and the source icons dominate the reply, so the
                // client may opt into artwork delta: unchanged cover/icon strings are omitted.
                // Version memory belongs to one connection; a reconnect always gets a complete
                // reply so a re-created client can never keep stale artwork. Clients that do not
                // send the flag (older builds, other callers) still receive the full payload.
                var delta = _artwork.Begin(generation, payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("artworkDelta", out var artworkDelta)
                    && artworkDelta.ValueKind == JsonValueKind.True);
                var coverChanged = _artwork.CoverChanged(coverDataUrl, delta);
                long textBytes = Encoding.UTF8.GetByteCount(state.Title) + Encoding.UTF8.GetByteCount(state.Artist) + Encoding.UTF8.GetByteCount(state.Source);
                long iconBytes = 0;
                // Read the cover and each source icon once, then measure the text payload
                // directly instead of serializing the whole reply.
                _artwork.RetainIcons((state.Sources ?? []).Select(source => source.Id));
                var sourceItems = (state.Sources ?? []).Select(source =>
                {
                    var label = source.Name switch { "QQMusic" => "QQ 音乐", "Netease" => "网易云音乐", "Kugou" => "酷狗音乐", "Douyin" => "抖音", "Browser" => BrowserSourceName(source.Id), _ => source.Name };
                    var icon = _media.SourceIcon(source) ?? string.Empty;
                    var item = new Dictionary<string, object?> { ["id"] = source.Id, ["label"] = label };
                    textBytes += Encoding.UTF8.GetByteCount(label);
                    if (_artwork.IconChanged(source.Id, icon, delta))
                    {
                        item["iconDataUrl"] = icon;
                        iconBytes += Encoding.UTF8.GetByteCount(icon);
                    }
                    return item;
                }).ToList();
                FootprintMetrics.NoteStatePayload(textBytes, coverChanged ? Encoding.UTF8.GetByteCount(coverDataUrl) : 0, iconBytes);
                if (coverChanged) _artwork.CommitCover(coverDataUrl);
                var reply = new Dictionary<string, object?>
                {
                    ["title"] = state.Title,
                    ["artist"] = state.Artist,
                    ["source"] = state.Source,
                    ["positionSeconds"] = state.PositionSeconds,
                    ["durationSeconds"] = state.DurationSeconds,
                    ["isPlaying"] = state.IsPlaying,
                    ["canPrevious"] = state.CanPrevious,
                    ["canNext"] = state.CanNext,
                    ["canPlayPause"] = state.CanPlayPause,
                    ["canSeek"] = state.CanSeek
                };
                if (coverChanged) reply["coverDataUrl"] = coverDataUrl;
                reply["sources"] = sourceItems;
                reply["selectedSourceId"] = state.SelectedSourceId;
                reply["toolWindowTheme"] = (int)Settings.ToolWindowTheme;
                return reply;
            }
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
            case "hideCards":
                await ForwardToCards("hideCards", default);
                return new { success = true };
            case "previewCardMica":
            case "previewCardTheme":
                await ForwardToCards(action == "previewCardMica" ? "cardMicaPreview" : "cardThemePreview", payload);
                return new { success = true };
            case "getAudio":
            case "selectDevice":
            case "setVolume":
            case "setMute":
                return await HandleAudioRequestAsync(action!, payload, token, () => _ui.IsConnectionCurrent(generation));
            default: throw new ArgumentException("不支持的界面操作。");
        }
    }
    private static string BrowserSourceName(string id) => id.Contains("msedge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" :
        id.Contains("chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome" : id.Contains("firefox", StringComparison.OrdinalIgnoreCase) ? "Firefox" : "浏览器";

    private static bool TryNumber(JsonElement value, string name, out double number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static object OpenDataFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = AppDataPaths.RootDirectory,
            UseShellExecute = true
        });
        return new { success = true };
    }

    private MediaSettingsService SettingsService()
    {
        if (_media is null) throw new InvalidOperationException("媒体服务尚未准备好。");
        if (_settingsService is null)
        {
            // One service instance owns the settings readback and keeps draft
            // revisions across requests; it has no window, WebView2 or script bridge.
            _settingsService = new MediaSettingsService(
                Settings.Clone(),
                _media.Offsets,
                () => Task.FromResult(_media.CurrentTrackContext),
                () => _media.Composition.CreateLyricDiagnosticRunner(Settings),
                (track, resolved, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(_media.TryApplyResolvedLyrics(track, resolved)); },
                (track, resolved, token) => _media.Composition.RememberResolvedLyricsAsync(track, resolved, token).AsTask(),
                _media.Composition.ClearLyricCache,
                SaveSettings,
                PreviewSettings,
                RetrySpectrumCapture,
                IsApplicationUsingLightTheme,
                IsSystemUiUsingLightTheme);
        }
        return _settingsService;
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
        TaskObserver.Observe(ToggleNativeCardsAsync(area.Left + area.Width / 2.0, area.Bottom), "native cards");
    }
    private Task ToggleNativeCardsAsync(double x, double y) => _pipe is not null
        ? _pipe.SendCommandAsync(new { version = 1, type = "command", action = "toggleCards", x, y }, _stop.Token)
        : Task.CompletedTask;
    private object QuickCards() => new { order = Settings.QuickCardOrder, columns = Settings.QuickCardColumns, enabled = Settings.QuickCardEnabled,
        resourceMode = Settings.QuickCardResourceMode.ToString(), font = Settings.CardFontFamily,
        mica = new
        {
            vivid = Settings.CardMicaVivid,
            tintOpacity = Settings.CardMicaTintOpacity,
            luminosityOpacity = Settings.CardMicaLuminosityOpacity,
            darkTintOpacity = Settings.CardMicaDarkTintOpacity,
            darkLuminosityOpacity = Settings.CardMicaDarkLuminosityOpacity
        } };
    internal void ShowLyricsWindow() => SendNativeCommand("showMedia");
    private void SendNativeCommand(string action)
    {
        if (_pipe is not null) TaskObserver.Observe(_pipe.SendCommandAsync(new { version = 1, type = "command", action }), "native command");
    }
    // The native card host owns the panel; ordering, enablement, resource mode,
    // font and Mica tuning are pushed to it whenever they change.
    private void PublishCardConfiguration()
    {
        if (_pipe is null) return;
        var quickCards = new
        {
            revision = _cardSnapshot?.ConfigurationRevision ?? 1,
            order = Settings.QuickCardOrder,
            columns = Settings.QuickCardColumns,
            enabled = Settings.QuickCardEnabled,
            resourceMode = Settings.QuickCardResourceMode.ToString(),
            font = Settings.CardFontFamily,
            mica = new
            {
                vivid = Settings.CardMicaVivid,
                tintOpacity = Settings.CardMicaTintOpacity,
                luminosityOpacity = Settings.CardMicaLuminosityOpacity,
                darkTintOpacity = Settings.CardMicaDarkTintOpacity,
                darkLuminosityOpacity = Settings.CardMicaDarkLuminosityOpacity,
            },
        };
        TaskObserver.Observe(_pipe.SendCommandAsync(new { version = 1, type = "command",
            action = "quickCards", payload = quickCards }), "card configuration");
    }

    // Card-only commands travel from the settings window to the native card host
    // through the media pipe; Media never interprets their payload.
    private Task ForwardToCards(string action, JsonElement payload)
    {
        if (_pipe is null) return Task.CompletedTask;
        return _pipe.SendCommandAsync(new { version = 1, type = "command", action,
            payload = payload.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : payload });
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
        _cardSnapshot?.BumpConfiguration();
        PublishCardConfiguration();
        _cards?.PublishSnapshot();
        return true;
    }

    internal void RetrySpectrumCapture() => _media?.RetrySpectrum();

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
        FootprintMetrics.Stop();
        _nativeSettings?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _cardTimer?.Stop();
        _stop.Cancel();
        if (_updates is not null) TaskObserver.Observe(_updates.DisposeAsync().AsTask(), "update service shutdown");
        _settingsService?.Dispose();
        _audio.Dispose();
        if (_ui is not null) TaskObserver.Observe(_ui.DisposeAsync().AsTask(), "WinUI shutdown");
        _media?.Dispose();
        if (_cards is not null) TaskObserver.Observe(_cards.DisposeAsync().AsTask(), "card pipe shutdown");
        if (_pipe is not null) TaskObserver.Observe(_pipe.DisposeAsync().AsTask(), "pipe shutdown");
        _stop.Dispose();
        base.OnExit(args);
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownRequested) return;
        _shutdownRequested = true;
        _nativeSettings?.Dispose();
        _cardTimer?.Stop();
        _stop.Cancel();
        if (_updates is not null) await _updates.DisposeAsync();
        // Stop input producers before releasing the media services they call into.
        _settingsService?.Dispose();
        if (_ui is not null) await _ui.DisposeAsync();
        if (_cards is not null) await _cards.DisposeAsync();
        await _audio.DisposeAsync();
        _media?.Dispose();
        if (_pipe is not null) await _pipe.DisposeAsync();
        Shutdown();
    }
}

