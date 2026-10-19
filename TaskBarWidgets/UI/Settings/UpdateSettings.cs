using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private UpdateUiState _updateState = new();
    private TextBlock? _updateVersion, _updateStatus, _updateNotes, _updateBytes, _updateLastChecked;
    private ProgressBar? _updateProgress;
    private StackPanel? _updateRuntimeDownloads;
    private CheckBox? _automaticUpdates;
    private Button? _checkUpdate, _downloadUpdate, _cancelUpdate, _installUpdate;
    private bool _bindingUpdates, _updateCommandBusy;
    internal event Action<JsonElement>? UpdateStateReceived;
    internal event Action? InstallUpdateRequested;
    public bool HasPendingChanges => _native.Pending.Count > 0 || _media.Pending.Count > 0 || _busy || _weatherPage is { } weather &&
        (weather.Committing || weather.Searching || weather.LoadingStatus || weather.ProviderEdited || weather.HostEdited || weather.SelectedCity is not null || weather.ApiKey.Password.Length > 0);
    private bool HasWeatherDraft() => _weatherPage is { } weather &&
        (weather.Committing || weather.Searching || weather.LoadingStatus || weather.ProviderEdited || weather.HostEdited ||
         weather.SelectedCity is not null || weather.ApiKey.Password.Length > 0);

    internal void SetUpdateState(UpdateUiState state)
    {
        _updateState = state;
        _navigation.IsEnabled = state.Phase is not ("preparing" or "installing");
        RefreshUpdateControls();
    }

    private void RenderUpdateSettings()
    {
        var section = Section("应用更新", "自动检查并下载更新。安装前会再次确认，配置与历史会保留。");
        _updateVersion = Copy(""); section.Children.Add(_updateVersion);
        _automaticUpdates = new CheckBox { Content = "自动检查并下载更新", IsChecked = _updateState.AutomaticEnabled };
        AutomationProperties.SetAutomationId(_automaticUpdates, "update-automatic");
        _automaticUpdates.Checked += async (_, _) => { if (!_bindingUpdates) await RunUpdateCommandAsync("setAutomaticUpdates", new { enabled = true }); };
        _automaticUpdates.Unchecked += async (_, _) => { if (!_bindingUpdates) await RunUpdateCommandAsync("setAutomaticUpdates", new { enabled = false }); };
        section.Children.Add(_automaticUpdates);
        section.Children.Add(Copy("此选项立即保存，无需点击“应用更改”。"));
        _updateStatus = Copy("", 14); section.Children.Add(_updateStatus);
        _updateProgress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_updateProgress, "更新下载进度"); section.Children.Add(_updateProgress);
        _updateBytes = Copy(""); section.Children.Add(_updateBytes);
        _updateLastChecked = Copy(""); section.Children.Add(_updateLastChecked);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _checkUpdate = ActionButton("检查更新", () => RunUpdateCommandAsync("checkForUpdates"));
        _downloadUpdate = ActionButton("下载更新", () => RunUpdateCommandAsync("downloadUpdate"));
        _cancelUpdate = ActionButton("取消下载", () => RunUpdateCommandAsync("cancelUpdateDownload"));
        _installUpdate = new Button { Content = "更新并重启", MinHeight = 32 };
        _installUpdate.Click += (_, _) => InstallUpdateRequested?.Invoke();
        foreach (var (button, id) in new[] { (_checkUpdate, "check"), (_downloadUpdate, "download"), (_cancelUpdate, "cancel"), (_installUpdate, "install") }) {
            AutomationProperties.SetAutomationId(button, "update-" + id); actions.Children.Add(button);
        }
        section.Children.Add(actions);
        section.Children.Add(new HyperlinkButton { Content = "打开发布页 / 手动下载", NavigateUri = new Uri("https://github.com/NoctisWang528/TaskBarWidget/releases/latest") });
        section.Children.Add(new HyperlinkButton { Content = "运行环境要求与下载说明", NavigateUri = new Uri("https://github.com/NoctisWang528/TaskBarWidget#下载与运行") });
        _updateRuntimeDownloads = new StackPanel { Visibility = Visibility.Collapsed };
        _updateRuntimeDownloads.Children.Add(new HyperlinkButton { Content = "微软下载：.NET 8 Desktop Runtime x64", NavigateUri = new Uri("https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0") });
        _updateRuntimeDownloads.Children.Add(new HyperlinkButton { Content = "微软下载：Windows App Runtime", NavigateUri = new Uri("https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads") });
        section.Children.Add(_updateRuntimeDownloads);
        _updateNotes = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        section.Children.Add(new Expander { Header = "更新说明", Content = _updateNotes, HorizontalAlignment = HorizontalAlignment.Stretch });
        RefreshUpdateControls();
    }

    private void RefreshUpdateControls()
    {
        if (_updateStatus is null || _currentPage != "about") return;
        var state = _updateState;
        _updateVersion!.Text = "当前版本 " + (state.CurrentVersion.Length > 0 ? state.CurrentVersion : _media.Text("appVersion", "未知")) +
            (state.LatestVersion.Length > 0 ? "  ·  最新版本 " + state.LatestVersion : "");
        _bindingUpdates = true;
        _automaticUpdates!.IsChecked = state.AutomaticEnabled;
        _bindingUpdates = false;
        _automaticUpdates.IsEnabled = !_updateCommandBusy && state.Phase is not ("preparing" or "installing");
        _updateStatus.Text = state.Status;
        if (_updateRuntimeDownloads is not null) _updateRuntimeDownloads.Visibility = state.Error.Contains("runtime", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        _updateNotes!.Text = state.Notes.Length > 0 ? state.Notes : "暂无更新说明。";
        _updateLastChecked!.Text = state.LastCheckedAt.Length > 0 ? "上次检查：" + state.LastCheckedAt : "";
        _updateProgress!.Visibility = state.Phase is "downloading" or "verifying" ? Visibility.Visible : Visibility.Collapsed;
        _updateProgress.IsIndeterminate = state.Phase == "verifying" || state.TotalBytes <= 0;
        _updateProgress.Value = state.TotalBytes > 0 ? Math.Clamp(100d * state.BytesDownloaded / state.TotalBytes, 0, 100) : 0;
        _updateBytes!.Text = state.TotalBytes > 0 ? $"{state.BytesDownloaded / 1048576d:0.0} / {state.TotalBytes / 1048576d:0.0} MB" : "";
        _checkUpdate!.IsEnabled = !_updateCommandBusy && !state.Busy;
        _downloadUpdate!.IsEnabled = !_updateCommandBusy && state.CanDownload && !state.Busy;
        _cancelUpdate!.Visibility = state.Phase == "downloading" ? Visibility.Visible : Visibility.Collapsed;
        _cancelUpdate.IsEnabled = !_updateCommandBusy;
        _installUpdate!.IsEnabled = !_updateCommandBusy && state.CanInstall && state.Phase == "ready";
    }

    private async Task RunUpdateCommandAsync(string action, object? payload = null)
    {
        if (_updateCommandBusy || _closed) return;
        _updateCommandBusy = true; RefreshUpdateControls();
        try { UpdateStateReceived?.Invoke(await _connection.RequestAsync(action, payload, _lifetime.Token)); }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_closed) ShowError(error.Message); }
        finally { _updateCommandBusy = false; if (!_closed) RefreshUpdateControls(); }
    }

    internal async Task<ContentDialogResult> ShowUpdateDialogAsync(bool pending, string version)
    {
        if (!_root.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler handler = (_, _) => loaded.TrySetResult();
            _root.Loaded += handler;
            try { if (!_root.IsLoaded) await loaded.Task.WaitAsync(_lifetime.Token); }
            finally { _root.Loaded -= handler; }
        }
        return await new ContentDialog {
            XamlRoot = _root.XamlRoot, RequestedTheme = _root.RequestedTheme,
            Title = pending ? "请先处理设置更改" : "安装更新？",
            Content = pending ? "有尚未应用的设置或天气操作。请先在设置中应用、完成或自行放弃更改，再安装更新。" :
                $"将安装版本 {version}。应用会正常退出、备份配置与历史并重新启动。请等待更新完成。",
            PrimaryButtonText = pending ? "" : "安装并重启", CloseButtonText = pending ? "返回设置" : "取消", DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }

    internal void ShowUpdateError(string message) => ShowError(message);
}
