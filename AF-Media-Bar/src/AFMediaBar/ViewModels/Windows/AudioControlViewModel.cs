using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Services;
using AFMediaBar.Classes.Services.Audio;
using AFMediaBar.Classes.Services.Localization;
using AFMediaBar.Classes.Settings;
using AFMediaBar.Resources;

namespace AFMediaBar.ViewModels.Windows;

/// <summary>
/// 协调托盘音频面板的设备预览、应用音量和滚轮语义。
///
/// 状态文本与空间音效文本是代码拼出来的，因此订阅语言变化并整批重发属性变更；托盘图标提示是悬停与滚动时重建的，
/// 不需要在这里缓存。
/// Coordinates device preview, application volume, and tray-wheel semantics for the audio flyout.
///
/// The status text and the spatial-audio text are composed in code, so the view model subscribes to language changes and
/// republishes every property; the tray icon tooltip is rebuilt on hover and on scroll and is therefore not cached here.
/// </summary>
public partial class AudioControlViewModel : ObservableObject, IDisposable
{
    private const int VolumeStepPercent = 2;
    private const bool TrayChordWheelEnabled = false;
    private static readonly TimeSpan DeviceApplyDelay =
        TimeSpan.FromMilliseconds(AudioApplyPolicy.OutputDevicePreviewDelayMilliseconds);
    private static readonly TimeSpan VolumeApplyDelay =
        TimeSpan.FromMilliseconds(AudioApplyPolicy.ApplicationVolumeDelayMilliseconds);
    private readonly AudioDeviceService _deviceService;
    private readonly SpatialAudioService _spatialAudioService;
    private readonly ApplicationVolumeService _volumeService;
    private readonly MediaSessionService _mediaSessionService;
    private readonly ShellTrayIconService _trayIconService;
    private readonly NativeMouseInputMonitor _mouseInputMonitor;
    private readonly AudioMonitorService _audioMonitorService;
    private readonly LocalizationService _localization;
    private readonly Dictionary<string, int> _volumeApplyVersions = new(StringComparer.OrdinalIgnoreCase);
    private int _deviceApplyVersion;
    private bool _isRefreshing;
    private bool _isPreviewingOutputDevice;
    private int _pendingTrayDeviceSteps;
    private bool _isProcessingTrayDevice;
    private int _pendingTrayVolumeSteps;
    private bool _isProcessingTrayVolume;
    private int _tooltipRefreshVersion;
    private string? _tooltipSourceId;
    private string? _tooltipSourceName;
    private DateTime _suppressTrayLeftClickUntilUtc;
    private DateTime _suppressTrayContextMenuUntilUtc;
    private readonly DispatcherTimer _trayTooltipTimer;
    private WheelGestureSlot? _appliedTrayWheelSlot;
    private bool _trayWheelResultShown;
    private DateTime _trayWheelResultUntilUtc;
    private bool _disposed;

    public ObservableCollection<AudioDeviceOption> OutputDevices { get; } = [];
    public ObservableCollection<ApplicationVolumeItemViewModel> Applications { get; } = [];

    [ObservableProperty] private AudioDeviceOption? _selectedOutputDevice;
    [ObservableProperty] private string _spatialAudioText = Translations.Get("Audio.Spatial.Unavailable");
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public ICommand RefreshCommand { get; }
    public ICommand OpenSpatialAudioSettingsCommand { get; }

    public event Action<TrayIconBounds?>? FlyoutToggleRequested;
    public event Action<TrayIconBounds?>? TrayContextMenuRequested;
    public event EventHandler? SettingsOpenRequested;

    /// <summary>
    /// 请求在托盘图标处打开输出设备菜单。菜单本身属于任务栏宿主，因此这里只表达意图并带上锚点。
    /// Requests the output-device menu to open at the tray icon. The menu belongs to the taskbar host, so this only expresses the
    /// intent and carries the anchor.
    /// </summary>
    public event Action<TrayIconBounds?>? OutputDeviceMenuRequested;

    /// <summary>请求在托盘图标处打开当前应用音量菜单。/ Requests the current application's volume menu to open at the tray icon.</summary>
    public event Action<TrayIconBounds?>? CurrentAppVolumeMenuRequested;

    /// <summary>
    /// 创建音频面板状态协调器；设备、应用音量和提示状态均通过注入服务异步刷新。
    /// Creates the audio-panel state coordinator; devices, application volume, and tooltip state are refreshed asynchronously through injected services.
    /// </summary>
    /// <param name="localization">界面语言服务：语言变化时让全部绑定重新取值。/ The interface-language service, which makes every binding re-read its text when the language changes.</param>
    public AudioControlViewModel(
        AudioDeviceService deviceService,
        SpatialAudioService spatialAudioService,
        ApplicationVolumeService volumeService,
        MediaSessionService mediaSessionService,
        ShellTrayIconService trayIconService,
        NativeMouseInputMonitor mouseInputMonitor,
        AudioMonitorService audioMonitorService,
        LocalizationService localization)
    {
        _deviceService = deviceService;
        _spatialAudioService = spatialAudioService;
        _volumeService = volumeService;
        _mediaSessionService = mediaSessionService;
        _trayIconService = trayIconService;
        _mouseInputMonitor = mouseInputMonitor;
        _audioMonitorService = audioMonitorService;
        _localization = localization;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenSpatialAudioSettingsCommand = new RelayCommand(OpenSpatialAudioSettings);
        _localization.LanguageChanged += OnLanguageChanged;
        _trayIconService.LeftClicked += OnTrayLeftClicked;
        _trayIconService.ContextMenuRequested += OnTrayContextMenuRequested;
        _trayIconService.TooltipOpening += OnTrayTooltipOpening;
        _trayIconService.PointerMovedOverIcon += OnTrayPointerMovedOverIcon;
        _mouseInputMonitor.WheelChanged += OnTrayWheelChanged;
        _mediaSessionService.SnapshotChanged += OnMediaSnapshotChanged;
        SettingsManager.InteractionSettingsChanged += OnInteractionSettingsChanged;
        // 指针停在图标上时定期读取系统音频状态；Shell 不会主动通知我们普通音量混音器的更改。
        _trayTooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _trayTooltipTimer.Tick += (_, _) => AdvanceTrayTooltipPoll();
        _mouseInputMonitor.Start();
        QueueTrayTooltipRefresh();
    }

    /// <summary>
    /// 刷新输出设备、应用音量和空间音效状态。
    /// Refreshes output devices, application volumes, and spatial-audio state.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var devices = await _deviceService.GetRenderDevicesAsync().WaitAsync(TimeSpan.FromSeconds(3));
            _isRefreshing = true;
            OutputDevices.Clear();
            foreach (var device in devices)
            {
                OutputDevices.Add(device);
            }

            SelectedOutputDevice = devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
            _isRefreshing = false;
            RefreshSpatialAudio();

            var applications = await Task.Run(() => _volumeService.GetApplications(
                _mediaSessionService.SelectedSourceId,
                _mediaSessionService.SelectedSourceName)).WaitAsync(TimeSpan.FromSeconds(3));
            Applications.Clear();
            foreach (var application in applications)
            {
                Applications.Add(new ApplicationVolumeItemViewModel(application, QueueApplicationVolume));
            }

            StatusText = applications.Count == 0 ? Translations.Get("Audio.Status.NoSessions") : string.Empty;
            UpdateTooltipFromLoadedState();
        }
        catch (Exception exception)
        {
            _isRefreshing = false;
            StatusText = Translations.Format("Audio.Status.ReadFailed", exception.Message);
            Debug.WriteLine($"[AudioControlViewModel] Refresh failed: {exception}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 根据托盘滚轮增量预览下一个输出设备。
    /// Previews the next output device for a tray-wheel delta.
    /// </summary>
    /// <param name="delta">滚轮增量 / Wheel delta.</param>
    public void PreviewOutputDeviceWheel(int delta)
    {
        PreviewOutputDeviceSteps(TrayWheelPolicy.GetDeviceSteps(delta));
    }

    private void PreviewOutputDeviceSteps(int signedSteps)
    {
        if (OutputDevices.Count == 0 || signedSteps == 0)
        {
            return;
        }

        var current = Math.Max(0, SelectedOutputDevice is null ? 0 : OutputDevices.IndexOf(SelectedOutputDevice));
        var device = OutputDevices[WheelInput.MoveCircular(current, signedSteps, OutputDevices.Count)];

        _isPreviewingOutputDevice = true;
        try
        {
            SelectedOutputDevice = device;
        }
        finally
        {
            _isPreviewingOutputDevice = false;
        }

        // 单设备或循环回原设备时属性值不会变化，也必须即时刷新原生提示。
        // Refresh the native tooltip immediately even when one device or a full cycle keeps the same selection.
        SetTrayTooltip(AudioTooltipPolicy.BuildOutputDevice(device));
    }

    partial void OnSelectedOutputDeviceChanged(AudioDeviceOption? value)
    {
        if (!_isRefreshing && value is not null)
        {
            StartOutputDeviceApply(
                value,
                _isPreviewingOutputDevice ? DeviceApplyDelay : TimeSpan.Zero);
        }
    }

    private void StartOutputDeviceApply(AudioDeviceOption device, TimeSpan delay)
    {
        var version = ++_deviceApplyVersion;
        _ = ApplyOutputDeviceAsync(device, version, delay);
        SetTrayTooltip(AudioTooltipPolicy.BuildOutputDevice(device));
    }

    private async Task ApplyOutputDeviceAsync(AudioDeviceOption device, int version, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            if (!AudioApplyPolicy.IsCurrent(_disposed, version, _deviceApplyVersion))
            {
                return;
            }

            // 缓冲结束后再次读取系统默认设备；循环滚回原设备时不要重复切换而打断音频。
            // Recheck the system default after buffering so cycling back does not interrupt audio with a redundant switch.
            if (await Task.Run(() => _deviceService.IsDefaultRenderDevice(device.Id)))
            {
                return;
            }

            if (!AudioApplyPolicy.IsCurrent(_disposed, version, _deviceApplyVersion))
                return;
            await Task.Run(() => _deviceService.SetDefaultRenderDevice(device.PolicyId));
            _audioMonitorService.ResetAfterEnvironmentChange();
            if (!AudioApplyPolicy.IsCurrent(_disposed, version, _deviceApplyVersion))
                return;
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            if (!AudioApplyPolicy.IsCurrent(_disposed, version, _deviceApplyVersion))
                return;
            StatusText = Translations.Format("Audio.Status.SwitchOutputDeviceFailed", exception.Message);
            SetTrayTooltip(Translations.Get("Audio.Tooltip.SwitchOutputDeviceFailed"));
        }
    }

    private void QueueApplicationVolume(
        ApplicationVolumeItemViewModel application,
        int volumePercent,
        bool applyImmediately)
    {
        StartApplicationVolumeApply(
            application,
            volumePercent,
            applyImmediately ? TimeSpan.Zero : VolumeApplyDelay);
    }

    private void StartApplicationVolumeApply(
        ApplicationVolumeItemViewModel application,
        int volumePercent,
        TimeSpan delay)
    {
        var version = _volumeApplyVersions.GetValueOrDefault(application.ProcessName) + 1;
        _volumeApplyVersions[application.ProcessName] = version;
        _ = ApplyApplicationVolumeAsync(application, volumePercent, version, delay);
    }

    private async Task ApplyApplicationVolumeAsync(
        ApplicationVolumeItemViewModel application,
        int volumePercent,
        int version,
        TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            if (_disposed || !_volumeApplyVersions.TryGetValue(application.ProcessName, out var current) || current != version)
            {
                return;
            }

            await Task.Run(() => _volumeService.SetApplicationVolume(application.ProcessName, volumePercent));
        }
        catch (Exception exception)
        {
            StatusText = Translations.Format(
                "Audio.Status.AdjustApplicationVolumeFailed",
                application.DisplayName,
                exception.Message);
        }
        finally
        {
            if (_volumeApplyVersions.TryGetValue(application.ProcessName, out var current) && current == version)
            {
                _volumeApplyVersions.Remove(application.ProcessName);
            }
        }
    }

    private void OnTrayWheelChanged(object? sender, TrayWheelEventArgs e)
    {
        var interaction = SettingsManager.Current.Interaction.Normalize();
        var chordHeld = GlobalWheelGesturePolicy.IsChordHeld(
            interaction, e.IsShiftDown, e.IsLeftButtonDown, e.IsRightButtonDown);
        if (chordHeld && !TrayChordWheelEnabled)
            return;

        if (TrayChordWheelEnabled && interaction.Modifier == InteractionModifier.LeftMouseButton && e.IsLeftButtonDown)
            _suppressTrayLeftClickUntilUtc = DateTime.UtcNow.AddMilliseconds(450);
        if (TrayChordWheelEnabled && interaction.Modifier == InteractionModifier.RightMouseButton && e.IsRightButtonDown)
            _suppressTrayContextMenuUntilUtc = DateTime.UtcNow.AddMilliseconds(450);

        switch (GlobalWheelGesturePolicy.ResolveTray(
                    interaction,
                    e.IsShiftDown,
                    e.IsLeftButtonDown,
                    e.IsRightButtonDown))
        {
            case TrayWheelBehavior.AdjustVolume:
                _pendingTrayVolumeSteps += TrayWheelPolicy.GetVolumeSteps(e.Delta);
                _ = ProcessTrayVolumeAsync();
                break;
            case TrayWheelBehavior.SwitchOutputDevice:
                _pendingTrayDeviceSteps += TrayWheelPolicy.GetDeviceSteps(e.Delta);
                _ = ProcessTrayDeviceAsync();
                break;
        }
    }

    private async Task ProcessTrayDeviceAsync()
    {
        if (_isProcessingTrayDevice)
            return;

        _isProcessingTrayDevice = true;
        try
        {
            while (_pendingTrayDeviceSteps != 0)
            {
                if (OutputDevices.Count == 0)
                    await LoadOutputDevicesForTrayAsync();
                if (_disposed)
                    return;
                if (OutputDevices.Count == 0)
                {
                    _pendingTrayDeviceSteps = 0;
                    SetTrayTooltip(AudioTooltipPolicy.BuildOutputDevice(null));
                    return;
                }

                var steps = _pendingTrayDeviceSteps;
                _pendingTrayDeviceSteps = 0;
                PreviewOutputDeviceSteps(steps);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[AudioControlViewModel] Tray device preview failed: {exception}");
            SetTrayTooltip(Translations.Get("Audio.Tooltip.SwitchOutputDeviceFailed"));
        }
        finally
        {
            _isProcessingTrayDevice = false;
            if (!_disposed && _pendingTrayDeviceSteps != 0)
                _ = ProcessTrayDeviceAsync();
        }
    }

    private async Task LoadOutputDevicesForTrayAsync()
    {
        var devices = await _deviceService.GetRenderDevicesAsync().WaitAsync(TimeSpan.FromSeconds(3));
        if (_disposed)
            return;

        _isRefreshing = true;
        try
        {
            OutputDevices.Clear();
            foreach (var device in devices)
                OutputDevices.Add(device);
            SelectedOutputDevice = devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task ProcessTrayVolumeAsync()
    {
        if (_isProcessingTrayVolume)
        {
            return;
        }

        _isProcessingTrayVolume = true;
        try
        {
            while (_pendingTrayVolumeSteps != 0)
            {
                var steps = _pendingTrayVolumeSteps;
                _pendingTrayVolumeSteps = 0;
                var current = await Task.Run(() => _volumeService.GetCurrentMediaVolume(
                    _mediaSessionService.SelectedSourceId,
                    _mediaSessionService.SelectedSourceName));
                if (current is null)
                {
                    SetTrayTooltip(AudioTooltipPolicy.BuildMediaVolume(null, null));
                    continue;
                }

                var next = Math.Clamp(current.VolumePercent + steps * VolumeStepPercent, 0, 100);
                await Task.Run(() => _volumeService.SetApplicationVolume(current.ProcessName, next));
                Applications.FirstOrDefault(item => string.Equals(item.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase))?.SynchronizeVolume(next);
                SetTrayTooltip(AudioTooltipPolicy.BuildMediaVolume(current.DisplayName, next));
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[AudioControlViewModel] Tray volume failed: {exception}");
            SetTrayTooltip(Translations.Get("Audio.Tooltip.AdjustVolumeFailed"));
        }
        finally
        {
            _isProcessingTrayVolume = false;
            if (_pendingTrayVolumeSteps != 0)
            {
                _ = ProcessTrayVolumeAsync();
            }
        }
    }

    private void RefreshSpatialAudio()
    {
        SpatialAudioText = SelectedOutputDevice is null
            ? Translations.Get("Audio.Spatial.NoDevice")
            : _spatialAudioService.GetState(SelectedOutputDevice.Id).DisplayName;
    }

    private void OpenSpatialAudioSettings()
    {
        try
        {
            _spatialAudioService.OpenSystemSettings();
        }
        catch (Exception exception)
        {
            StatusText = Translations.Format("Audio.Status.OpenSoundSettingsFailed", exception.Message);
        }
    }

    /// <summary>
    /// 界面语言变化时让全部绑定重新取值。状态文本与空间音效文本是代码拼出来的，不会自己跟着资源走；
    /// <see cref="ObservableObject.OnPropertyChanged(string)"/> 收到空属性名时会重读全部绑定。
    /// Re-reads every binding when the interface language changes. The status text and the spatial-audio text are composed in
    /// code and do not follow the resources on their own, and <see cref="ObservableObject.OnPropertyChanged(string)"/>
    /// re-reads every binding when it receives an empty property name.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);

    private void OnTrayLeftClicked(object? sender, EventArgs e)
    {
        // 组合滚轮（按住鼠标键再滚动）结束时的松键会合成一次单击：用户只打算滚动，因此这次单击必须被吞掉。
        // 判定来自全局鼠标钩子，且标记是一次性的，所以无论结果如何都要取走。
        // Releasing the button after a chord wheel (scrolling while a mouse button is held) synthesizes a click, and the user only
        // meant to scroll, so it has to be swallowed. The hook makes that call and the flag is one-shot, so it is taken either way.
        var chordWheelClick = TrayChordWheelEnabled && _mouseInputMonitor.ConsumeSuppressedClick();
        if (chordWheelClick || DateTime.UtcNow < _suppressTrayLeftClickUntilUtc)
            return;

        switch (SettingsManager.Current.Interaction.TrayClickAction)
        {
            case TrayClickAction.OpenSettings:
                SettingsOpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayClickAction.OpenAudioControl:
                FlyoutToggleRequested?.Invoke(GetTrayBounds());
                break;
            case TrayClickAction.OpenOutputDeviceMenu:
                OutputDeviceMenuRequested?.Invoke(GetTrayBounds());
                break;
            case TrayClickAction.OpenCurrentAppVolumeMenu:
                CurrentAppVolumeMenuRequested?.Invoke(GetTrayBounds());
                break;
            case TrayClickAction.OpenContextMenu:
                TrayContextMenuRequested?.Invoke(GetTrayBounds());
                break;
        }
    }
    private void OnTrayContextMenuRequested(object? sender, EventArgs e)
    {
        // 右键菜单同样可能是组合滚轮松键的合成结果，因此与左键点击走同一个抑制判定。
        // The context menu can equally be synthesized by releasing after a chord wheel, so it shares the left click's rule.
        var chordWheelClick = TrayChordWheelEnabled && _mouseInputMonitor.ConsumeSuppressedClick();
        if (!chordWheelClick && DateTime.UtcNow >= _suppressTrayContextMenuUntilUtc)
            TrayContextMenuRequested?.Invoke(GetTrayBounds());
    }

    private void OnMediaSnapshotChanged(object? sender, MediaSnapshot snapshot)
    {
        if (string.Equals(_tooltipSourceId, snapshot.SourceId, StringComparison.Ordinal) &&
            string.Equals(_tooltipSourceName, snapshot.SourceName, StringComparison.Ordinal))
        {
            return;
        }

        _tooltipSourceId = snapshot.SourceId;
        _tooltipSourceName = snapshot.SourceName;
        if (SettingsManager.Current.Interaction.TrayPrimaryWheelAction == TrayWheelBehavior.AdjustVolume)
            QueueTrayTooltipRefresh();
    }

    private void OnTrayTooltipOpening(object? sender, EventArgs e)
    {
        // Shell 气泡打开时再次读取实时值；首次指向图标的读取已由鼠标移动消息提前触发。
        _trayTooltipTimer.Start();
        QueueTrayTooltipRefresh();
    }

    private void OnTrayPointerMovedOverIcon(object? sender, EventArgs e)
    {
        if (_trayTooltipTimer.IsEnabled)
            return;

        // 先于 Shell 的气泡打开通知读取系统状态，避免第一次悬停显示上次缓存的设备或音量。
        _trayTooltipTimer.Start();
        QueueTrayTooltipRefresh();
    }

    private void OnInteractionSettingsChanged(object? sender, EventArgs e)
    {
        QueueTrayTooltipRefresh();
    }

    private void QueueTrayTooltipRefresh()
    {
        var version = ++_tooltipRefreshVersion;
        _ = RefreshTrayTooltipAsync(version);
    }

    private async Task RefreshTrayTooltipAsync(int version)
    {
        try
        {
            var settings = SettingsManager.Current.Interaction.Normalize();
            var slot = settings.ShowWheelTooltips && ChordWheelHeld
                ? WheelGestureSlot.Chord
                : WheelGestureSlot.Primary;
            if (_trayWheelResultShown && slot == _appliedTrayWheelSlot && DateTime.UtcNow < _trayWheelResultUntilUtc)
                return;

            _appliedTrayWheelSlot = slot;
            _trayWheelResultShown = false;
            var behavior = slot == WheelGestureSlot.Chord
                ? (TrayChordWheelEnabled ? settings.TrayChordWheelAction : TrayWheelBehavior.Disabled)
                : settings.TrayPrimaryWheelAction;
            if (behavior == TrayWheelBehavior.Disabled && settings.ShowWheelTooltips)
            {
                if (!_disposed && version == _tooltipRefreshVersion)
                    _trayIconService.UpdateTooltip("AF Media Bar");
                return;
            }
            // Even with no wheel binding, the tray remains a useful audio-status surface.
            if (behavior == TrayWheelBehavior.Disabled)
                behavior = TrayWheelBehavior.SwitchOutputDevice;
            ApplicationVolumeSnapshot? application = null;
            AudioDeviceOption? device = null;
            if (behavior == TrayWheelBehavior.AdjustVolume)
            {
                application = await Task.Run(() => _volumeService.GetCurrentMediaVolume(
                    _mediaSessionService.SelectedSourceId,
                    _mediaSessionService.SelectedSourceName));
            }
            else if (behavior == TrayWheelBehavior.SwitchOutputDevice)
            {
                var devices = await _deviceService.GetRenderDevicesAsync();
                device = devices.FirstOrDefault(candidate => candidate.IsDefault) ?? devices.FirstOrDefault();
            }

            // 关闭滚轮操作提示只去掉手势说明；当前设备/音量仍用悬停层组件同款状态文案。
            // Disabling wheel hints removes only the gesture description; the current device/volume uses the same status text as the hover controls.
            var text = settings.ShowWheelTooltips
                ? WheelTooltipPolicy.BuildHintWithValue(
                    WheelTooltipPolicy.BuildHint(
                        slot,
                        settings.Modifier,
                        WheelTooltipPolicy.BuildActionName(behavior)),
                    behavior == TrayWheelBehavior.AdjustVolume
                        ? BuildVolumeDetail(application)
                        : device?.DisplayName)
                : AudioTooltipPolicy.Build(behavior, application, device);

            if (!_disposed && version == _tooltipRefreshVersion)
            {
                _trayIconService.UpdateTooltip(text);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[AudioControlViewModel] Tooltip refresh failed: {exception.Message}");
            if (!_disposed && version == _tooltipRefreshVersion)
            {
                _trayIconService.UpdateTooltip("AF Media Bar");
            }
        }
    }

    private void UpdateTooltipFromLoadedState() => QueueTrayTooltipRefresh();

    private static string? BuildVolumeDetail(ApplicationVolumeSnapshot? application) =>
        application is null
            ? null
            : Translations.Format("Audio.Volume.CurrentValue", application.DisplayName, application.VolumePercent);

    /// <summary>
    /// 当前是否按住了共用组合键。托盘的滚轮绑定同样按这个键在普通与组合之间切换；托盘图标是 Shell 图标，
    /// 不提供按键事件，因此输入状态一律向全局鼠标监听器询问，Win32 调用留在那个服务里。
    /// Whether the shared chord key is currently held. The tray's wheel binding switches between plain and chord on the same key, and
    /// because the shell tray icon offers no key events the input state is asked of the global mouse monitor, keeping Win32 calls
    /// inside that service.
    /// </summary>
    private bool ChordWheelHeld => GlobalWheelGesturePolicy.IsChordHeld(
        SettingsManager.Current.Interaction,
        _mouseInputMonitor.IsShiftDown,
        _mouseInputMonitor.IsLeftButtonDown,
        _mouseInputMonitor.IsRightButtonDown);

    /// <summary>
    /// 指针停在托盘图标上时重新读取音频状态，离开后停止轮询。
    /// Refreshes audio state while the pointer rests on the tray icon and stops polling when it leaves.
    /// </summary>
    private void AdvanceTrayTooltipPoll()
    {
        if (!_mouseInputMonitor.IsPointerOverTray())
        {
            _trayTooltipTimer.Stop();
            return;
        }

        QueueTrayTooltipRefresh();
    }

    private void SetTrayTooltip(string text)
    {
        _tooltipRefreshVersion++;
        // 暂留滚轮结果供确认，之后恢复实时读取，避免系统设置中的变化被旧结果永久遮住。
        _appliedTrayWheelSlot = SettingsManager.Current.Interaction.ShowWheelTooltips && ChordWheelHeld
            ? WheelGestureSlot.Chord
            : WheelGestureSlot.Primary;
        _trayWheelResultShown = true;
        _trayWheelResultUntilUtc = DateTime.UtcNow.AddSeconds(1);
        _trayIconService.UpdateTooltip(text);
    }

    private TrayIconBounds? GetTrayBounds() => _trayIconService.TryGetBounds(out var bounds) ? bounds : null;

    /// <summary>
    /// 取消托盘与媒体事件订阅、取消语言订阅并释放输入监听器。
    /// Unsubscribes tray and media events, unsubscribes from the language, and releases the input monitor.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _tooltipRefreshVersion++;
        _localization.LanguageChanged -= OnLanguageChanged;
        _trayIconService.LeftClicked -= OnTrayLeftClicked;
        _trayIconService.ContextMenuRequested -= OnTrayContextMenuRequested;
        _trayIconService.TooltipOpening -= OnTrayTooltipOpening;
        _trayIconService.PointerMovedOverIcon -= OnTrayPointerMovedOverIcon;
        _trayTooltipTimer.Stop();
        _mouseInputMonitor.WheelChanged -= OnTrayWheelChanged;
        _mediaSessionService.SnapshotChanged -= OnMediaSnapshotChanged;
        SettingsManager.InteractionSettingsChanged -= OnInteractionSettingsChanged;
        _mouseInputMonitor.Dispose();
        _deviceApplyVersion++;
        _pendingTrayDeviceSteps = 0;
        _pendingTrayVolumeSteps = 0;
        _volumeApplyVersions.Clear();
    }
}
