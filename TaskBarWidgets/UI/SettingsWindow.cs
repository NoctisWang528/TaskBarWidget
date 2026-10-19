using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.UI.Settings;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow : Window
{
    private readonly UiConnection _connection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SettingsDraft _native = new();
    private readonly SettingsDraft _media = new();
    private readonly Grid _root = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    private bool _highContrastSubscriptionAttempted, _highContrastSubscribed;
    private JsonObject _schema = new();
    private readonly NavigationView _navigation = new() { IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, PaneDisplayMode = NavigationViewPaneDisplayMode.Auto, OpenPaneLength = 224, PaneTitle = "TaskBarWidgets" };
    private readonly SplitView _split = new() { PanePlacement = SplitViewPanePlacement.Right, DisplayMode = SplitViewDisplayMode.Overlay, OpenPaneLength = 352 };
    private readonly StackPanel _page = new() { Spacing = 16, Margin = new Thickness(24, 16, 24, 28), Tag = "settings-content" };
    private readonly StackPanel _properties = new() { Spacing = 12, Margin = new Thickness(20) };
    private readonly TextBlock _heading = new() { FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Message = "正在读取设置…" };
    private readonly Button _apply = new() { Content = "应用更改", IsEnabled = false };
    private readonly TextBlock _saveStatus = new() { Text = "正在连接…", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private ModuleLayout? _layout;
    private StackPanel? _preview;
    private ScrollViewer? _pageScroll;
    private string _currentPage = "taskbar";
    private bool _busy;
    private bool _loaded;
    private bool _shutdown;
    private bool _closed;
    private bool _closeDialogOpen;
    internal bool ReloadWithoutPrompt;
    public event Action? Dismissed;
    internal event Action<ElementTheme>? ThemePreviewChanged;
    internal ElementTheme? ThemePreview => _loaded ? _root.RequestedTheme : null;

    public SettingsWindow(UiConnection connection)
    {
        _connection = connection;
        Title = "TaskBarWidgets — 设置";
        WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            _page.ChildrenTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection { new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { IsStaggeringEnabled = false } };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 820));
        var pages = new (string Id, string Label, string Glyph)[] {
            ("taskbar", "任务栏与布局", "\uE950"), ("quickCards", "快捷卡片", "\uE8A9"), ("hardware", "硬件与网络", "\uE7F4"),
            ("sources", "播放源", "\uE8D6"), ("lyricsGroup", "歌词", "\uE8D2"),
            ("general", "常规", "\uE713"), ("about", "关于", "\uE946") };
        foreach (var page in pages)
        {
            var item = new NavigationViewItem { Content = page.Label, Tag = page.Id, Icon = new FontIcon { Glyph = page.Glyph } };
            if (page.Id == "lyricsGroup")
            {
                item.SelectsOnInvoked = false;
                foreach (var child in new[] { ("lyrics", "显示与行为"), ("displayArea", "布局与样式"),
                    ("lyricDiagnostics", "歌词匹配"), ("trackOffsets", "单曲偏移"), ("advanced", "诊断与维护") })
                    item.MenuItems.Add(new NavigationViewItem { Content = child.Item2, Tag = child.Item1 });
            }
            if (page.Id == "quickCards")
            {
                item.SelectsOnInvoked = false;
                foreach (var child in new[] { ("quickCards", "卡片与顺序"), ("quickCardsWeather", "天气") })
                    item.MenuItems.Add(new NavigationViewItem { Content = child.Item2, Tag = child.Item1 });
            }
            _navigation.MenuItems.Add(item);
        }
        _navigation.SelectionChanged += (_, args) => {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string id) { _currentPage = id; RenderPage(); }
        };
        var toolbar = new Grid { ColumnSpacing = 12, Margin = new Thickness(24, 8, 24, 8) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var reload = ActionButton("重新读取", () => ReloadAsync());
        ToolTipService.SetToolTip(reload, "重新读取会放弃尚未应用的草稿");
        toolbar.Children.Add(_saveStatus); Grid.SetColumn(reload, 1); toolbar.Children.Add(reload);
        Grid.SetColumn(_apply, 2); toolbar.Children.Add(_apply); _apply.Click += async (_, _) => await GuardAsync(SaveAsync);
        var body = new Grid { MaxWidth = 1088, HorizontalAlignment = HorizontalAlignment.Stretch };
        void UpdateNavigationInset(NavigationViewDisplayMode mode) => body.Margin = new Thickness(0, mode == NavigationViewDisplayMode.Minimal ? 40 : 0, 0, 0);
        _navigation.DisplayModeChanged += (_, args) => UpdateNavigationInset(args.DisplayMode);
        UpdateNavigationInset(_navigation.DisplayMode);
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _heading.Margin = new Thickness(24, 20, 24, 12); body.Children.Add(_heading);
        _notice.Margin = new Thickness(24, 0, 24, 0); Grid.SetRow(_notice, 1); body.Children.Add(_notice);
        Grid.SetRow(toolbar, 2); body.Children.Add(toolbar);
        var scroll = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _pageScroll = scroll;
        Grid.SetRow(scroll, 3); body.Children.Add(scroll);
        _split.Content = body;
        _split.Pane = UiThemeStyles.Apply(new ScrollViewer { Content = _properties, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, "SettingsPane");
        _navigation.Content = _split; Content = _navigation;
        InitializeModuleGestures();
        _root.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        _root.Loaded += (_, _) => { TrySubscribeSettingsHighContrast(); UpdateTitleBarTheme(); };
        Activated += (_, _) => { if (!_closed) UpdateTitleBarTheme(); };
        _navigation.Loaded += async (_, _) => {
            _navigation.SelectedItem = NavigationPages().FirstOrDefault(item => (string)item.Tag == _currentPage);
            if (!_loaded && _connection.IsConnected) await GuardAsync(() => ReloadAsync());
        };
        _connection.ConnectionChanged += OnConnectionChanged;
        AppWindow.Closing += (_, args) => {
            CancelModulePointerDrag();
            if (_shutdown || _closed) return;
            args.Cancel = true;
            CloseDeferred();
        };
        Closed += (_, _) => { _closed = true; CancelWeatherPage(); _lifetime.Cancel(); _connection.ConnectionChanged -= OnConnectionChanged; UnsubscribeSettingsHighContrast(); _lifetime.Dispose(); };
        ShowPage("taskbar");
    }

    public void NavigateTo(string page) => ShowPage(page);
    private IEnumerable<NavigationViewItem> NavigationPages() => _navigation.MenuItems.OfType<NavigationViewItem>()
        .SelectMany(item => item.MenuItems.Count == 0 ? new[] { item } : item.MenuItems.OfType<NavigationViewItem>());
    public void ShowPage(string? page)
    {
        if (page is "audio" or "shortcuts") page = "general";
        if (page is "weather") page = "quickCardsWeather";
        var item = NavigationPages().FirstOrDefault(item => (string)item.Tag == page)
            ?? NavigationPages().First();
        _currentPage = (string)item.Tag;
        foreach (var parent in _navigation.MenuItems.OfType<NavigationViewItem>())
            if (parent.MenuItems.Contains(item)) parent.IsExpanded = true;
        if (_navigation.IsLoaded) { _navigation.SelectedItem = item; if (_currentPage == "hardware" && _weatherPage is null) RenderPage(); }
        else RenderPage();
        _split.IsPaneOpen = false;
    }
    public void Shutdown() { _shutdown = true; Close(); }
    private void CloseDeferred() => BeginCloseConfirmation();
    private async void BeginCloseConfirmation()
    {
        try { await CloseWithPendingChangesAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_closed) ShowError(error.Message);
        }
    }
    private async Task CloseWithPendingChangesAsync()
    {
        var weatherBusy = _weatherPage is { Committing: true };
        var pending = _native.Pending.Count + _media.Pending.Count > 0 || weatherBusy || HasWeatherDraft();
        if (!pending || _root.XamlRoot is null)
        {
            CancelWeatherPage();
            AppWindow.Hide();
            Dismissed?.Invoke();
            return;
        }
        if (_closeDialogOpen) return;
        _closeDialogOpen = true;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.RequestedTheme,
            Title = "有尚未应用的设置",
            Content = weatherBusy
                ? "天气操作仍在进行。可以继续编辑，之后应用或丢弃更改。"
                : "设置中还有未应用的更改。要应用后关闭、丢弃更改，还是继续编辑？",
            PrimaryButtonText = weatherBusy ? "继续编辑" : "应用并关闭",
            SecondaryButtonText = "丢弃更改",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Close
        };
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _closeDialogOpen = false; }
        if (result == ContentDialogResult.Primary && !weatherBusy)
        {
            await GuardAsync(SaveAsync);
            if (_native.Pending.Count + _media.Pending.Count > 0 || HasWeatherDraft()) return;
        }
        else if (result == ContentDialogResult.Primary || result == ContentDialogResult.None)
        {
            Activate();
            return;
        }
        _native.Pending.Clear();
        _media.Pending.Clear();
        CancelWeatherPage();
        CancelModulePointerDrag();
        AppWindow.Hide();
        Dismissed?.Invoke();
    }
    internal void LoadValidationData(JsonObject native, JsonObject media)
    {
        SetNativeReadback(native); _media.Readback(MediaValues(media)); _loaded = true;
        _notice.IsOpen = false; ApplyTheme(); RenderPage(); UpdateSaveStatus();
    }
    internal void OpenModuleProperties(string id) => OpenModule(id);
    private void OnConnectionChanged(bool connected) => DispatcherQueue.TryEnqueue(async () => {
        if (_closed) return;
        if (!connected) ShowError("主程序连接已断开；草稿仍保留，请恢复连接后应用。");
        else if (!_loaded) await GuardAsync(() => ReloadAsync());
    });
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) { if (!_closed) ShowError(error.Message); }
    }
    private async Task<JsonObject> RequestAsync(string action, object? payload = null)
    {
        var result = await _connection.RequestAsync(action, payload, _lifetime.Token);
        return JsonNode.Parse(result.GetRawText()) as JsonObject ?? new JsonObject();
    }
    private async Task ReloadAsync()
    {
        if (_busy || _weatherPage?.Committing == true) return;
        if (ReloadWithoutPrompt)
        {
            ReloadWithoutPrompt = false;
        }
        else
        if (HasPendingChanges && _root.XamlRoot is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                RequestedTheme = _root.RequestedTheme,
                Title = "重新读取设置？",
                Content = "当前有尚未保存的更改。重新读取会丢弃这些更改。",
                PrimaryButtonText = "继续编辑",
                SecondaryButtonText = "丢弃并重新读取",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Secondary) return;
        }
        CancelModulePointerDrag();
        _busy = true; UpdateSaveStatus();
        var nativeRevision = _native.Capture(); var mediaRevision = _media.Capture();
        try {
            var native = await RequestAsync("getNativeSettings");
            var media = await RequestAsync("getMediaSettings");
            await LoadFontsAsync();
            SetNativeReadback(native, nativeRevision); _media.Readback(MediaValues(media), mediaRevision);
            _loaded = true; ApplyTheme(); RenderPage();
            _notice.IsOpen = false;
        }
        finally { _busy = false; UpdateSaveStatus(); }
    }
    private void SetNativeReadback(JsonObject schema, SettingsDraft.SaveSnapshot? saved = null)
    {
        if (schema["fields"] is not JsonArray fields || schema["groups"] is not JsonArray) throw new InvalidDataException("主程序没有返回完整的监测设置。");
        _schema = schema;
        _native.Readback(fields.OfType<JsonObject>().Select(field => new KeyValuePair<string, JsonNode?>(Text(field["key"]), field["value"])), saved);
        var modules = fields.OfType<JsonObject>().Where(field => Text(field["key"]).StartsWith("items.") && Text(field["key"]).EndsWith(".label"))
            .Select(field => new DisplayModule(Text(field["itemId"], Text(field["key"]).Split('.')[1]), Text(field["itemName"], Text(field["label"])), Text(field["sampleValue"], "42"), field["doubleLineExclusive"]?.ToString() == "true")).ToList();
        _layout = new ModuleLayout(_native, modules);
    }
    private async Task SaveAsync()
    {
        if (_busy || !_loaded || _weatherPage?.Committing == true) return;
        CancelModulePointerDrag();
        _busy = true; UpdateSaveStatus();
        var native = _native.Capture(); var media = _media.Capture();
        var themeOnly = native.Values.Count == 0 && media.Values.Count == 1 && media.Values.ContainsKey("toolWindowTheme");
        try {
            if (native.Values.Count > 0) {
                if (native.Values.ContainsKey("taskbar.horizontal_arrange") || native.Values.Keys.Any(key => key.StartsWith("items.") && (key.EndsWith(".show") || key.EndsWith(".row") || key.EndsWith(".order")))) _layout?.IncludeWholeLayout(native.Values);
                SetNativeReadback(await RequestAsync("applyNativeSettings", native.Values), native);
            }
            if (media.Values.Count > 0) _media.Readback(MediaValues(await RequestAsync("applyMediaSettings", media.Values)), media);
            ApplyTheme();
            _notice.Severity = InfoBarSeverity.Success; _notice.Message = "已保存并读回主程序配置。"; _notice.IsOpen = true;
        }
        finally { _busy = false; if (!themeOnly) RenderPage(); UpdateSaveStatus(); }
    }
    private void ApplyTheme()
    {
        // Mica, popup targets and the sibling drag overlay share one theme root.
        _root.RequestedTheme = UiThemeStyles.ParseTheme(_media.Text("toolWindowTheme"));
        if (_loaded) ThemePreviewChanged?.Invoke(_root.RequestedTheme);
        UpdateTitleBarTheme();
    }
    private void UpdateTitleBarTheme()
    {
        var titleBar = AppWindow.TitleBar;
        var contrast = _accessibility.HighContrast;
        var dark = _root.ActualTheme == ElementTheme.Dark;
        Color? foreground = contrast ? null : dark ? Colors.White : Colors.Black;
        Color? background = contrast ? null : dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        titleBar.ForegroundColor = titleBar.ButtonForegroundColor = foreground;
        titleBar.BackgroundColor = titleBar.ButtonBackgroundColor = background;
        titleBar.InactiveBackgroundColor = titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = titleBar.ButtonInactiveForegroundColor = contrast ? null : dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 96, 96, 96);
        titleBar.ButtonHoverBackgroundColor = contrast ? null : dark ? Color.FromArgb(255, 55, 55, 55) : Color.FromArgb(255, 224, 224, 224);
        titleBar.ButtonPressedBackgroundColor = contrast ? null : dark ? Color.FromArgb(255, 48, 48, 48) : Color.FromArgb(255, 210, 210, 210);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = foreground;
    }
    private void OnSettingsHighContrastChanged(Windows.UI.ViewManagement.AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_closed) UpdateTitleBarTheme(); });
    private void TrySubscribeSettingsHighContrast()
    {
        if (_closed || _highContrastSubscriptionAttempted) return;
        _highContrastSubscriptionAttempted = true;
        try { _accessibility.HighContrastChanged += OnSettingsHighContrastChanged; _highContrastSubscribed = true; }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or NotSupportedException or NotImplementedException)
        {
            // Some unpackaged desktop hosts expose HighContrast but cannot add
            // its WinRT event (ERROR_NOT_FOUND). Loaded, activation and actual
            // theme changes still refresh chrome; XAML ThemeResource stays live.
        }
    }
    private void UnsubscribeSettingsHighContrast()
    {
        if (!_highContrastSubscribed) return;
        _highContrastSubscribed = false;
        try { _accessibility.HighContrastChanged -= OnSettingsHighContrastChanged; }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or NotSupportedException or NotImplementedException) { }
    }
    private T ThemeFlyout<T>(T flyout) where T : FlyoutBase
    {
        flyout.Opening += (_, _) => {
            // Flyout presenters live in a popup, outside the settings visual root.
            var type = flyout is MenuFlyout ? typeof(MenuFlyoutPresenter) : typeof(FlyoutPresenter);
            var style = new Style(type) { BasedOn = (Style)Application.Current.Resources["Default" + type.Name + "Style"] };
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, _root.RequestedTheme));
            if (flyout is MenuFlyout menu) menu.MenuFlyoutPresenterStyle = style;
            else if (flyout is Flyout content) content.FlyoutPresenterStyle = style;
        };
        return flyout;
    }
    private void ShowError(string message) { _notice.Message = message; _notice.Severity = InfoBarSeverity.Error; _notice.IsOpen = true; }
    private void UpdateSaveStatus()
    {
        var count = _native.Pending.Count + _media.Pending.Count;
        var empty = _loaded && _layout is not null && !_layout.Modules.Any(module => _layout.Enabled(module.Id));
        var weatherSaving = _weatherPage?.Committing == true;
        _apply.IsEnabled = !_busy && !weatherSaving && count > 0;
        _saveStatus.Text = _busy ? "正在保存或读取；新的编辑会保留在草稿中…" : weatherSaving ? "正在保存或刷新天气…" : empty ? "请至少添加一个显示模块再应用。" : count > 0 ? $"{count} 项更改待应用" : "配置已同步";
        if (_weatherPage is not null) UpdateWeatherButtons(_weatherPage);
    }
    private void Changed() { if (_notice.Severity == InfoBarSeverity.Success) _notice.IsOpen = false; UpdateSaveStatus(); RenderPreview(); }
    private void RenderPage()
    {
        CancelModulePointerDrag();
        if (_currentPage != "quickCardsWeather") CancelWeatherPage();
        _page.Children.Clear(); _preview = null; _split.IsPaneOpen = false;
        _heading.Text = NavigationPages().FirstOrDefault(item => (string)item.Tag == _currentPage)?.Content?.ToString() ?? "设置";
        if (!_loaded) { _page.Children.Add(Copy("连接主程序后即可读取设置。")); return; }
        if (_currentPage == "taskbar") RenderTaskbar();
        else if (_currentPage is "quickCards" or "quickCardsWeather") RenderQuickCards();
        else if (_currentPage is "hardware" or "general") { RenderNativeGroups(_currentPage); if (_currentPage == "general") RenderGeneral(); }
        else RenderMediaPage(_currentPage);
    }
    private void RenderNativeGroups(string page)
    {
        var groups = ((_schema["groups"] as JsonArray)?.OfType<JsonObject>() ?? []).Where(group => Text(group["page"]) == page && Text(group["id"]) != "items").OrderBy(group => Text(group["id"]) == "appearance" ? 0 : 1);
        foreach (var group in groups) {
            var fields = NativeFields().Where(field => Text(field["group"]) == Text(group["id"]) && Text(field["key"]) != "taskbar.horizontal_arrange").ToList();
            if (fields.Count == 0) continue;
            if (Text(group["id"]) == "appearance") { RenderAppearanceGroup(fields); continue; }
            var content = Section(Text(group["label"]));
            content.Children.Add(ResponsiveFields(fields.Where(field => field["advanced"]?.ToString() != "true").Select(field => FieldEditor(field, _native))));
            var advanced = fields.Where(field => field["advanced"]?.ToString() == "true").ToList();
            if (advanced.Count > 0) {
                content.Children.Add(new Expander { Header = "高级选项", Content = ResponsiveFields(advanced.Select(field => FieldEditor(field, _native))), HorizontalAlignment = HorizontalAlignment.Stretch });
            }
        }
    }
    private IEnumerable<JsonObject> NativeFields() => (_schema["fields"] as JsonArray)?.OfType<JsonObject>() ?? [];
    private FrameworkElement FieldEditor(JsonObject field, SettingsDraft draft, Action? after = null)
    {
        var key = Text(field["key"]); var label = Text(field["label"]); var type = Text(field["type"]);
        void Change(object? value) { draft.Set(key, value); Changed(); after?.Invoke(); if (key == "taskbar.horizontal_arrange") RenderPage(); }
        if (type == "boolean") {
            // The native template's padding aligns text with the glyph and supplies its 8 DIP gap.
            var check = new CheckBox { Content = label, IsChecked = draft.Bool(key), MinHeight = 32, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetAutomationId(check, "setting-" + key);
            check.Checked += (_, _) => Change(true); check.Unchecked += (_, _) => Change(false);
            var description = Text(field["description"]); if (field["restartRequired"]?.ToString() == "true") description += " 重启后生效";
            if (description.Length > 0) ToolTipService.SetToolTip(check, description);
            return check;
        }
        var row = new Grid { ColumnSpacing = 16, RowSpacing = 6, MinHeight = 40, Margin = new Thickness(0, 2, 0, 2) };
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = label, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        var hint = Text(field["description"]); if (field["restartRequired"]?.ToString() == "true") hint += " 重启后生效";
        if (hint.Length > 0) labels.Children.Add(Copy(hint));
        FrameworkElement control;
        if (type == "number") {
            var number = new NumberBox { Value = draft.Number(key), Minimum = Number(field["min"], -1000000), Maximum = Number(field["max"], 1000000), SmallChange = Number(field["step"], 1), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinHeight = 32 };
            number.ValueChanged += (_, args) => { if (double.IsFinite(args.NewValue)) Change(args.NewValue); }; control = number;
            number.KeyUp += (_, _) => {
                if (double.TryParse(number.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) && value >= number.Minimum && value <= number.Maximum) Change(value);
            };
        }
        else if (type == "select") {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
            foreach (var option in (field["options"] as JsonArray)?.OfType<JsonObject>() ?? []) combo.Items.Add(new ComboBoxItem { Content = Text(option["label"]), Tag = option["value"]?.DeepClone() });
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => SameChoice(item.Tag as JsonNode, draft.Get(key)));
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem selected) { draft.SetNode(key, (selected.Tag as JsonNode)?.DeepClone()); if (key == "toolWindowTheme") ApplyTheme(); Changed(); after?.Invoke(); } }; control = combo;
        }
        else {
            var text = new TextBox { Text = draft.Text(key), MinHeight = 32, PlaceholderText = type == "color" ? "#RRGGBB" : "" };
            var pattern = ReferenceEquals(draft, _native) ? "^#[0-9a-fA-F]{6}$" : "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$";
            text.TextChanged += (_, _) => { if (type != "color" || System.Text.RegularExpressions.Regex.IsMatch(text.Text, pattern)) Change(text.Text); };
            text.LostFocus += (_, _) => {
                if (type == "color" && !System.Text.RegularExpressions.Regex.IsMatch(text.Text, pattern)) { ShowError(ReferenceEquals(draft, _native) ? "颜色请输入 #RRGGBB。" : "颜色请输入 #RRGGBB 或 #AARRGGBB。"); return; }
            };
            control = text;
            if (type == "color") {
                var picker = new ColorPicker { Color = ParseColor(text.Text), IsAlphaEnabled = !ReferenceEquals(draft, _native), IsAlphaSliderVisible = !ReferenceEquals(draft, _native), IsAlphaTextInputVisible = !ReferenceEquals(draft, _native) };
                var colorButton = new Button { Content = new Border { Width = 18, Height = 18, Background = new SolidColorBrush(picker.Color) }, Padding = new Thickness(6), Flyout = ThemeFlyout(new Flyout { Content = picker }) };
                AutomationProperties.SetName(colorButton, label + "取色器");
                picker.ColorChanged += (_, args) => {
                    var color = args.NewColor;
                    text.Text = ReferenceEquals(draft, _native) ? $"#{color.R:x2}{color.G:x2}{color.B:x2}" : $"#{color.A:x2}{color.R:x2}{color.G:x2}{color.B:x2}";
                    ((Border)colorButton.Content).Background = new SolidColorBrush(color); Change(text.Text);
                };
                var colors = new Grid { ColumnSpacing = 6 }; colors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); colors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                colors.Children.Add(colorButton); Grid.SetColumn(text, 1); colors.Children.Add(text); control = colors;
            }
        }
        AutomationProperties.SetName(control, label); AutomationProperties.SetAutomationId(control, "setting-" + key);
        ArrangeField(row, labels, control);
        return row;
    }
    private static bool SameChoice(JsonNode? first, JsonNode? second) => JsonNode.DeepEquals(first, second) || Text(first) == Text(second);
    private StackPanel Section(string title, string? description = null)
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(16, 12, 16, 12) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(description)) header.Children.Add(Copy(description));
        var card = new Grid(); card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Children.Add(UiThemeStyles.Apply(new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }, "SettingsDivider"));
        Grid.SetRow(stack, 1); card.Children.Add(stack);
        _page.Children.Add(UiThemeStyles.Apply(new Border { Child = card, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }, "SettingsSurface"));
        return stack;
    }
    private static TextBlock Copy(string text, double size = 12) => new() { Text = text, FontSize = size, Opacity = .72, TextWrapping = TextWrapping.Wrap };
    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, MinHeight = 32 }; button.Click += async (_, _) => await GuardAsync(action); return button;
    }
    private static string Text(JsonNode? node, string fallback = "") => node?.ToString() ?? fallback;
    private static double Number(JsonNode? node, double fallback = 0) => double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
