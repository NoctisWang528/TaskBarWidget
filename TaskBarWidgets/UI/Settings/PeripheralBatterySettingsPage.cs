using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.UI.Settings;
using Windows.Foundation;
using Windows.System;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private PeripheralBatteryPage? _peripheralPage;
    private PeripheralBatterySession? _peripheralSession;
    private int _peripheralConnectionVersion;
    private long _nativeSchemaVersion;
    private bool _peripheralSchemaReading;
    private string? _peripheralInventory;
    private PeripheralSettingsDraft PeripheralDraft => new(_media);

    private sealed class PeripheralBatterySession(DispatcherQueueTimer timer)
    {
        public DispatcherQueueTimer Timer { get; } = timer;
        public JsonObject Snapshot { get; set; } = new();
        public bool Reading, Subscribed, SubscriptionConfirmed;
        public int Generation;
        public long Revision = -1;
    }

    private sealed class PeripheralBatteryPage
    {
        public StackPanel Devices { get; } = new() { Spacing = 8 };
        public TextBlock Status { get; } = Copy("正在读取外设…");
        public ComboBox Mode { get; } = new() { Header = "显示方式", HorizontalAlignment = HorizontalAlignment.Stretch };
        public Dictionary<string, ToggleSwitch> Options { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PeripheralBatteryRow> Rows { get; } = new(StringComparer.Ordinal);
        public JsonObject Snapshot { get; set; } = new();
        public Button? Refresh { get; set; }
        public bool Updating;
    }

    private sealed class PeripheralBatteryRow
    {
        public required Border Surface;
        public required FontIcon Icon;
        public required TextBlock Name;
        public required TextBlock Details;
        public required TextBlock Battery;
        public required ToggleSwitch Visible;
        public required PointerDragButton Handle;
        public required MenuFlyoutItem Up;
        public required MenuFlyoutItem Down;
    }

    private void RenderPeripheralBattery()
    {
        var page = new PeripheralBatteryPage { Snapshot = _peripheralSession?.Snapshot ?? new JsonObject() };
        _peripheralPage = page;
        var appearance = Section("卡片显示");
        page.Mode.Items.Add("缩略");
        page.Mode.Items.Add("详细");
        AutomationProperties.SetName(page.Mode, "外设电量显示方式");
        AutomationProperties.SetAutomationId(page.Mode, "setting-peripheralBattery-mode");
        page.Mode.SelectionChanged += (_, _) => {
            if (page.Updating || page.Mode.SelectedIndex < 0) return;
            PeripheralDraft.Set("mode", page.Mode.SelectedIndex == 1 ? "detailed" : "compact");
            Changed();
        };
        appearance.Children.Add(page.Mode);
        void Toggle(StackPanel parent, string key, string label, string? description = null)
        {
            var control = SettingsToggle.Compact(false, label, "setting-peripheralBattery-" + key,
                changed: value => {
                    if (page.Updating) return;
                    PeripheralDraft.Set(key, value);
                    Changed();
                });
            page.Options[key] = control;
            parent.Children.Add(SettingsToggle.WithLabel(label, description, control));
        }
        Toggle(appearance, "hideDisconnected", "卡片隐藏已断开的设备");
        var devices = Section("设备与顺序", "选择在卡片中显示的设备。拖动手柄排序，或使用更多菜单调整。");
        page.Refresh = ActionButton("刷新设备", () => _peripheralSession is { } session
            ? ReadPeripheralBatteriesAsync(session, refresh: true) : Task.CompletedTask);
        AutomationProperties.SetAutomationId(page.Refresh, "peripheral-battery-refresh");
        devices.Children.Add(page.Refresh);
        AutomationProperties.SetAutomationId(page.Status, "peripheral-battery-status");
        devices.Children.Add(page.Status);
        AutomationProperties.SetAutomationId(page.Devices, "peripheral-battery-devices");
        devices.Children.Add(page.Devices);
        var extensions = Section("可选支持");
        Toggle(extensions, "airPodsAdvertisementEnabled", "AirPods 广播电量（估算）", "约10%一档；附近同型号设备可能混淆。");
        Toggle(extensions, "headsetControlEnabled", "启用 HeadsetControl", "为支持的耳机读取电量。");
        Toggle(extensions, "vendorExtensionsEnabled", "使用已安装的厂商服务");
        ToolTipService.SetToolTip(page.Options["vendorExtensionsEnabled"], "读取厂商服务提供的电量。");
        UpdatePeripheralControls(page);
        page.Refresh.IsEnabled = _peripheralSession?.Reading == false && (_connection.IsConnected || ProbeRequestHandler is not null);
        UpdatePeripheralRows(page);
    }

    private void UpdatePeripheralSampling()
    {
        if (_closed || !_loaded || _currentPage is not ("taskbar" or "peripheralBattery"))
        {
            LeavePeripheralBatteryPage();
            return;
        }
        if (_peripheralSession is not null) return;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        var session = new PeripheralBatterySession(timer);
        _peripheralSession = session;
        timer.Tick += async (_, _) => await ReadPeripheralBatteriesAsync(session);
        _ = ReadPeripheralBatteriesAsync(session, subscribe: true);
    }

    private void UpdatePeripheralControls(PeripheralBatteryPage page)
    {
        page.Updating = true;
        try
        {
            page.Mode.SelectedIndex = PeripheralDraft.Mode == "detailed" ? 1 : 0;
            foreach (var (key, toggle) in page.Options) toggle.IsOn = PeripheralDraft.Bool(key);
        }
        finally { page.Updating = false; }
    }

    private bool PeripheralRequestCurrent(PeripheralBatterySession page, int generation, int connection) =>
        !_closed && ReferenceEquals(page, _peripheralSession) && (_currentPage is "taskbar" or "peripheralBattery") &&
        generation == page.Generation && connection == Volatile.Read(ref _peripheralConnectionVersion) &&
        (_connection.IsConnected || ProbeRequestHandler is not null);

    private async Task ReadPeripheralBatteriesAsync(PeripheralBatterySession page, bool subscribe = false, bool refresh = false)
    {
        var generation = page.Generation;
        var connection = Volatile.Read(ref _peripheralConnectionVersion);
        if (page.Reading || !PeripheralRequestCurrent(page, generation, connection)) return;
        page.Reading = true;
        if (_peripheralPage?.Refresh is { } refreshButton) refreshButton.IsEnabled = false;
        try
        {
            if (subscribe || !page.SubscriptionConfirmed)
            {
                // Mark before awaiting so leaving during subscription still sends the release.
                page.Subscribed = true;
                await RequestAsync("subscribePeripheralBatteries", new { active = true });
                if (!PeripheralRequestCurrent(page, generation, connection)) return;
                page.SubscriptionConfirmed = true;
                page.Timer.Start();
            }
            var snapshot = await RequestAsync(refresh ? "refreshPeripheralBatteries" : "getPeripheralBatteries");
            if (!PeripheralRequestCurrent(page, generation, connection)) return;
            if (snapshot["version"]?.ToString() != "1" || snapshot["devices"] is not JsonArray)
                throw new InvalidDataException("外设电量数据格式不受支持。");
            var revision = (long)Number(snapshot["revision"]);
            if (revision < page.Revision) return;
            page.Revision = revision;
            page.Snapshot = snapshot;
            if (_peripheralPage is { } view)
            {
                view.Snapshot = snapshot;
                UpdatePeripheralRows(view);
            }
            var inventory = PeripheralSettingsDraft.InventoryKey(snapshot);
            if (_peripheralInventory != inventory && await RefreshPeripheralModuleSchemaAsync())
                _peripheralInventory = inventory;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            if (PeripheralRequestCurrent(page, generation, connection))
            {
                if (_peripheralPage is { } view) view.Status.Text = "无法读取外设电量：" + error.Message;
            }
        }
        finally
        {
            if (PeripheralRequestCurrent(page, generation, connection))
            {
                page.Reading = false;
                if (_peripheralPage?.Refresh is { } button) button.IsEnabled = true;
            }
        }
    }

    private void LeavePeripheralBatteryPage()
    {
        _peripheralPage = null;
        if (_peripheralSession is not { } page) return;
        _peripheralSession = null;
        page.Generation++;
        page.Timer.Stop();
        if (page.Subscribed && (_connection.IsConnected || ProbeRequestHandler is not null))
            _ = ReleasePeripheralSubscriptionAsync();
    }

    private async Task ReleasePeripheralSubscriptionAsync()
    {
        try
        {
            // Closing cancels the settings lifetime; allow this release to finish.
            if (ProbeRequestHandler is not null)
                await ProbeRequestHandler("subscribePeripheralBatteries", new JsonObject { ["active"] = false });
            else await _connection.RequestAsync("subscribePeripheralBatteries", new { active = false });
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void PeripheralConnectionChanged(bool connected)
    {
        if (_peripheralSession is not { } page) return;
        page.Timer.Stop();
        page.Generation++;
        page.Reading = page.Subscribed = page.SubscriptionConfirmed = false;
        page.Revision = -1;
        if (_peripheralPage?.Refresh is { } button) button.IsEnabled = connected;
        if (connected)
        {
            _peripheralInventory = null;
            _ = ReadPeripheralBatteriesAsync(page, subscribe: true);
        }
        else if (_peripheralPage is { } view) view.Status.Text = "连接已断开，保留最近读数与未应用的更改。";
    }

    private async Task<bool> RefreshPeripheralModuleSchemaAsync()
    {
        if (_closed || _busy || !_loaded || _peripheralSchemaReading ||
            (!_connection.IsConnected && ProbeRequestHandler is null)) return false;
        _peripheralSchemaReading = true;
        var version = _nativeSchemaVersion;
        var connection = Volatile.Read(ref _peripheralConnectionVersion);
        var session = _peripheralSession;
        var captured = _native.Capture();
        try
        {
            var schema = await RequestAsync("getNativeSettings");
            if (_closed || _busy || !ReferenceEquals(session, _peripheralSession) ||
                version != _nativeSchemaVersion || connection != _peripheralConnectionVersion) return false;
            var before = _layout?.Modules.Select(module => module.Id).ToArray() ?? [];
            SetNativeReadback(schema, captured, preservePending: true);
            if (_currentPage == "taskbar" && !before.SequenceEqual(_layout!.Modules.Select(module => module.Id))) RenderPage();
            UpdateSaveStatus();
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_closed && connection == _peripheralConnectionVersion) ShowError("无法更新外设显示模块：" + error.Message);
            return false;
        }
        finally { _peripheralSchemaReading = false; }
    }

    private static IEnumerable<JsonObject> PeripheralDevices(JsonObject snapshot) =>
        (snapshot["devices"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(device => Text(device["id"]).Length > 0).DistinctBy(device => Text(device["id"]));

    private void UpdatePeripheralRows(PeripheralBatteryPage page)
    {
        var devices = PeripheralDevices(page.Snapshot).ToDictionary(device => Text(device["id"]), StringComparer.Ordinal);
        var order = PeripheralDraft.Order(devices.Keys).Where(devices.ContainsKey).ToArray();
        page.Status.Text = !_connection.IsConnected && ProbeRequestHandler is null ? "连接已断开，保留最近读数与未应用的更改。"
            : Text(page.Snapshot["error"]) is { Length: > 0 } error ? error
            : page.Snapshot["refreshing"]?.ToString() == "true" ? "正在刷新设备…"
            : devices.Count == 0 ? "未发现外设。连接设备后可刷新。" : $"{devices.Count} 个设备 · 更改后点击“应用更改”保存";
        // Keep realized rows and focus stable; inventory changes wait until a drag ends.
        if (_pointerReorder?.IsActive == true) return;
        foreach (var id in page.Rows.Keys.Where(id => !devices.ContainsKey(id)).ToArray())
        {
            page.Devices.Children.Remove(page.Rows[id].Surface);
            page.Rows.Remove(id);
        }
        page.Updating = true;
        try
        {
            for (var index = 0; index < order.Length; index++)
            {
                var id = order[index];
                var device = devices[id];
                if (!page.Rows.TryGetValue(id, out var row))
                {
                    row = BuildPeripheralBatteryRow(page, id);
                    page.Rows.Add(id, row);
                }
                row.Name.Text = Text(device["name"], "未知设备");
                row.Handle.DragLabel = row.Name.Text;
                row.Icon.Glyph = PeripheralGlyph(Text(device["kind"]));
                row.Details.Text = PeripheralDeviceDetails(device);
                row.Battery.Text = PeripheralBatteryText(device);
                row.Visible.IsOn = PeripheralDraft.Visible(id, device["showInCard"]?.ToString() == "true");
                row.Up.IsEnabled = index > 0;
                row.Down.IsEnabled = index < order.Length - 1;
                AutomationProperties.SetName(row.Visible, "在外设电量卡片中显示 " + row.Name.Text);
                AutomationProperties.SetName(row.Handle, row.Name.Text + "，拖动排序，Alt 加上下方向键调整");
                ToolTipService.SetToolTip(row.Surface, PeripheralEvidence(device));
                if (page.Devices.Children.IndexOf(row.Surface) == index) continue;
                page.Devices.Children.Remove(row.Surface);
                page.Devices.Children.Insert(index, row.Surface);
            }
        }
        finally { page.Updating = false; }
    }

    private PeripheralBatteryRow BuildPeripheralBatteryRow(PeripheralBatteryPage page, string id)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 10, 8, 10), MinHeight = 80 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var handle = new PointerDragButton { Content = ReorderDots(), Width = 32, Height = 36, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var icon = new FontIcon { FontSize = 20, Width = 24, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var name = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        var details = Copy("");
        var battery = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(name); labels.Children.Add(details); labels.Children.Add(battery);
        var visible = SettingsToggle.Compact(false, "在卡片中显示", "peripheral-visible-" + id,
            changed: value => { if (!page.Updating) { PeripheralDraft.SetVisible(id, value); Changed(); } });
        var menu = ThemeFlyout(new MenuFlyout());
        var up = new MenuFlyoutItem { Text = "上移" };
        var down = new MenuFlyoutItem { Text = "下移" };
        up.Click += (_, _) => MovePeripheralBy(page, id, -1);
        down.Click += (_, _) => MovePeripheralBy(page, id, 1);
        menu.Items.Add(up); menu.Items.Add(down);
        var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 16 }, Width = 36, Height = 36, Padding = new Thickness(0), Flyout = menu, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(more, "设备排序");
        foreach (var (element, column) in new (FrameworkElement, int)[] { (handle, 0), (icon, 1), (labels, 2), (visible, 3), (more, 4) }) AddCardRow(grid, element, column);
        var surface = UiThemeStyles.Apply(new Border { Child = grid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ContextFlyout = menu }, "SettingsSourceSurface");
        AutomationProperties.SetAutomationId(surface, "peripheral-row-" + id);
        ToolTipService.SetToolTip(handle, "拖动排序；Alt + ↑ / ↓ 调整");
        handle.KeyDown += (_, args) => {
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0 || args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
            MovePeripheralBy(page, id, args.Key == VirtualKey.Up ? -1 : 1);
            args.Handled = true;
        };
        handle.StartPointerDrag = (button, args) => !_busy && _pointerReorder?.Begin(button, args, null,
            point => MovePeripheralDrag(page, id, point), point => {
                if (PeripheralDropGap(page, point) is { } gap) ReorderPeripheral(page, id, gap);
            }, surface, PointerReorder.CreateDragGhost(icon.Glyph, name.Text, "外设电量")) == true;
        return new PeripheralBatteryRow { Surface = surface, Icon = icon, Name = name, Details = details, Battery = battery, Visible = visible, Handle = handle, Up = up, Down = down };
    }

    private string[] PeripheralVisibleOrder(PeripheralBatteryPage page) =>
        page.Devices.Children.OfType<Border>().Select(surface => page.Rows.First(pair => ReferenceEquals(pair.Value.Surface, surface)).Key).ToArray();

    private void MovePeripheralBy(PeripheralBatteryPage page, string id, int delta)
    {
        var order = PeripheralVisibleOrder(page);
        var source = Array.IndexOf(order, id);
        if (source < 0 || source + delta < 0 || source + delta >= order.Length) return;
        ReorderPeripheral(page, id, source + (delta > 0 ? 2 : -1));
        page.Rows[id].Handle.Focus(FocusState.Programmatic);
    }

    private void ReorderPeripheral(PeripheralBatteryPage page, string id, int gap)
    {
        if (_busy || !ReferenceEquals(page, _peripheralPage)) return;
        CancelModulePointerDrag();
        PeripheralDraft.Reorder(PeripheralVisibleOrder(page), id, gap);
        Changed();
        UpdatePeripheralRows(page);
    }

    private int? PeripheralDropGap(PeripheralBatteryPage page, Point point)
    {
        if (_pageScroll is null || !ContainsPoint(_pageScroll, point) || !ContainsPoint(page.Devices, point)) return null;
        var rows = page.Devices.Children.OfType<Border>().ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var bounds = PointerReorder.LayoutBounds(rows[index], _navigation);
            if (point.Y < bounds.Y + bounds.Height / 2) return index;
        }
        return rows.Length;
    }

    private void MovePeripheralDrag(PeripheralBatteryPage page, string id, Point point)
    {
        if (_busy) { CancelModulePointerDrag(); return; }
        AutoScrollDrag(point);
        if (PeripheralDropGap(page, point) is not { } gap)
        {
            _pointerReorder?.HideInsertion();
            _pointerReorder?.ClearRowPreview();
            return;
        }
        var rows = page.Devices.Children.OfType<FrameworkElement>().ToArray();
        var source = Array.IndexOf(PeripheralVisibleOrder(page), id);
        if (source < 0) return;
        _pointerReorder?.ShowRowPreview(rows, source, gap, rows[source].ActualHeight + page.Devices.Spacing);
        var finalGap = gap - (source < gap ? 1 : 0);
        if (finalGap < rows.Length) _pointerReorder?.ShowVerticalGap(rows[finalGap], false);
        else _pointerReorder?.ShowVerticalGap(rows[^1], true);
    }

    private static string PeripheralGlyph(string kind) => kind switch
    {
        "mouse" => "\uE962", "keyboard" => "\uE765", "headset" or "earbuds" => "\uE7F6",
        "gamepad" => "\uE7FC", "pen" => "\uEDC6", _ => "\uE950"
    };

    private static string PeripheralDeviceDetails(JsonObject device)
    {
        var connection = Text(device["connection"]) switch {
            "bluetooth" or "ble" => "蓝牙", "usb" => "USB", "wireless" or "receiver" or "dongle" or "usb-2.4g" => "无线接收器", _ => "连接未知" };
        var state = Text(device["state"]) switch {
            "online" => "已连接", "disconnected" => "已断开", "sleeping" => "休眠",
            "unsupported" => "不支持读取电量", _ => "暂不可用" };
        return connection + " · " + state + (device["stale"]?.ToString() == "true" ? " · 上次读数" : "");
    }

    private static string PeripheralEvidence(JsonObject device)
    {
        var support = Text(device["compatibility"]) switch {
            "verified" => "已验证", "implemented" => "协议已实现", "extension" => "扩展支持", _ => "仅识别设备" };
        var updated = DateTimeOffset.TryParse(Text(device["updatedAt"]), out var time) ? time.ToLocalTime().ToString("g") : "尚无读数";
        return support + " · " + Text(device["source"], "来源未知") + " · " + updated;
    }

    private static string PeripheralBatteryText(JsonObject device)
    {
        var batteries = (device["batteries"] as JsonArray ?? []).OfType<JsonObject>().Select(battery => {
            var role = Text(battery["role"]) switch {
                "left" => "左耳", "right" => "右耳", "case" => "充电盒", "main" or "" => "", _ => Text(battery["role"]) };
            var value = battery["percentage"] is JsonValue percentage && percentage.TryGetValue<int>(out var number) && number is >= 0 and <= 100
                ? $"{number}%" : Text(battery["levelText"], "—");
            if (battery["estimated"]?.ToString() == "true") value = "≈" + value;
            var level = Text(battery["levelText"]);
            if (battery["percentage"] is not null && level.Length > 0) value += "（" + level + "）";
            var charging = battery["charging"] is JsonValue charge && charge.TryGetValue<bool>(out var active)
                ? active ? " · 充电中" : " · 未充电" : "";
            return (role.Length > 0 ? role + " " : "") + value + charging;
        }).ToArray();
        return batteries.Length > 0 ? string.Join(" / ", batteries) : "—";
    }
}
