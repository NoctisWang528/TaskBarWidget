using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.UI.Settings;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private DispatcherQueueTimer? _settingsSyncTimer;
    private Task _settingsSyncTask = Task.CompletedTask;
    private bool _settingsSyncEnabled = true;
    private bool _readSettingsRequested, _syncFailed, _refreshingControls;
    private readonly Button _retrySync = new() { Content = "重试", Visibility = Visibility.Collapsed };
    private readonly List<SettingReadback> _settingReadbacks = [];
    private sealed class SettingReadback(FrameworkElement control, Func<string> signature, Action refresh)
    {
        internal FrameworkElement Control { get; } = control;
        internal Func<string> Signature { get; } = signature;
        internal Action Refresh { get; } = refresh;
        internal string LastValue { get; set; } = signature();
    }

    private void InitializeSettingsSync()
    {
        _settingsSyncTimer = DispatcherQueue.CreateTimer();
        _settingsSyncTimer.Interval = TimeSpan.FromMilliseconds(500);
        _settingsSyncTimer.IsRepeating = false;
        _settingsSyncTimer.Tick += async (_, _) => await FlushSettingsAsync();
        _retrySync.Click += async (_, _) => { _readSettingsRequested = true; await FlushSettingsAsync(); };
        _root.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(RefreshSettingControls);
    }

    private void QueueSettingsSync(bool read = false)
    {
        if (!_settingsSyncEnabled || _closed || _shutdown || (!_connection.IsConnected && ProbeRequestHandler is null)) return;
        _readSettingsRequested |= read;
        _settingsSyncTimer?.Stop();
        _settingsSyncTimer?.Start();
    }

    private async Task FlushSettingsAsync()
    {
        if (_closed || _shutdown) return;
        if (!_settingsSyncTask.IsCompleted) { await _settingsSyncTask; return; }
        _settingsSyncTask = SynchronizeSettingsAsync();
        await _settingsSyncTask;
    }

    private async Task SynchronizeSettingsAsync()
    {
        if (_busy || _aiUsageMutating || _pointerReorder?.IsActive == true ||
            _weatherPage is { Committing: true } or { Searching: true } or { LoadingStatus: true })
        {
            QueueSettingsSync();
            return;
        }
        _syncFailed = false;
        try
        {
            if (_loaded) await SaveAsync();
            if (_syncFailed) return;
            if (_readSettingsRequested || !_loaded)
            {
                _readSettingsRequested = false;
                await ReloadAsync();
            }
            if (_readSettingsRequested || _native.Pending.Count + _media.Pending.Count > 0 ||
                _weatherPage is { } weather && !WeatherCredentialsPending(weather) &&
                (weather.SelectedCity is not null || CanSwitchWeatherProvider(weather))) QueueSettingsSync();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error)
        {
            if (!_closed) { _syncFailed = true; ShowError("设置未同步：" + error.Message); }
        }
        finally { if (!_closed) UpdateSaveStatus(); }
    }

    private void RegisterSettingReadback(FrameworkElement control, SettingsDraft draft, string key, Action refresh)
        => RegisterReadback(control, () => draft.Get(key)?.ToJsonString() ?? "null", refresh);

    private void RegisterReadback(FrameworkElement control, Func<string> signature, Action refresh)
    {
        var binding = new SettingReadback(control, signature, refresh);
        _settingReadbacks.Add(binding);
        control.Unloaded += (_, _) => _settingReadbacks.Remove(binding);
    }

    private bool HasFocusedEditor(FrameworkElement control)
    {
        if (_root.XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(_root.XamlRoot) as DependencyObject;
        var editable = false;
        while (focused is not null)
        {
            editable |= focused is TextBox or NumberBox or PasswordBox or ComboBox { IsEditable: true };
            if (ReferenceEquals(focused, control)) return editable;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private void RefreshSettingControls()
    {
        if (_closed || _refreshingControls || !_loaded || _pointerReorder?.IsActive == true) return;
        _refreshingControls = _native.UpdatingControls = _media.UpdatingControls = true;
        try
        {
            foreach (var binding in _settingReadbacks.ToArray())
            {
                if (!_settingReadbacks.Contains(binding) || HasFocusedEditor(binding.Control)) continue;
                var value = binding.Signature();
                if (binding.LastValue == value) continue;
                binding.Refresh();
                binding.LastValue = value;
            }
            if (_peripheralPage is { } peripheral)
            {
                UpdatePeripheralControls(peripheral);
                UpdatePeripheralRows(peripheral);
            }
        }
        finally { _refreshingControls = _native.UpdatingControls = _media.UpdatingControls = false; }
        RenderPreview();
    }
}
