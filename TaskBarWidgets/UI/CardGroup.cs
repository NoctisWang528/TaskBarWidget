using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TaskBarWidgets.Media.Integration;
using TaskBarWidgets.UI.Cards;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;

namespace TaskBarWidgets.UI;

public sealed class CardGroup : IDisposable
{
    internal const double CardWidth = 384;
    private readonly UiConnection _connection;
    private readonly Action _openSettings;
    private readonly CardWindow[] _cards = [];
    internal IReadOnlyList<CardWindow> CardWindows => _cards;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _seekTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly List<MenuFlyout> _flyouts = [];
    private readonly Dictionary<string, (string Id, double Volume)> _pendingVolumes = [];
    private readonly TextBlock _trafficTotal = Text("—", 18), _trafficScope = Text("" , 10), _trafficDetail = Text("", 11), _trafficStatus = Text("", 10);
    private readonly Grid _months = new();
    private readonly Button[] _days = new Button[84];
    private readonly Grid[] _dayGlows = new Grid[84];
    private readonly Dictionary<Button, (SpriteVisual Visual, DropShadow Shadow)> _glows = [];
    private readonly Border[] _legend = new Border[5];
    private readonly TextBlock _title = Text("暂无媒体", 15), _artist = Text("", 12), _sourceLabel = Text("暂无来源", 11);
    private readonly Image _cover = new() { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _position = Text("0:00", 10), _duration = Text("0:00", 10), _status = Text("", 10);
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _previous = Icon("\uE892", "上一首"), _play = Icon("\uE768", "播放 / 暂停", 42), _next = Icon("\uE893", "下一首");
    private readonly Button _source = new() { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0), MinHeight = 24, MaxWidth = 240, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Button _output = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch },
        _input = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _outputName = Text("读取输出设备…", 12), _inputName = Text("读取输入设备…", 12),
        _outputPercent = Text("0", 11), _inputPercent = Text("0", 11);
    private readonly Button _outputMute = Icon("\uE767", "输出静音"), _inputMute = Icon("\uE720", "输入静音");
    private readonly Slider _outputVolume = new() { Minimum = 0, Maximum = 100 }, _inputVolume = new() { Minimum = 0, Maximum = 100 };
    private AudioChoice[] _outputs = [], _inputs = [];
    private SourceChoice[] _sources = [];
    private TrafficHeatmap _traffic = TrafficHeatmap.Create(DateOnly.FromDateTime(DateTime.Today));
    private DateOnly? _selectedDate;
    private CancellationTokenSource? _visibleLifetime;
    private bool _visible, _disposed, _refreshing, _rendering, _switching, _seekPending, _writingVolumes;
    private bool _refreshAgain, _audioAgain, _trafficAgain;
    private readonly HashSet<string> _writingFlows = [];
    private int _audioGeneration;
    private int _generation, _ticks, _flyoutsOpen;
    private double _anchorX, _anchorY;
    private DateTime _deactivatedAt;
    private string? _outputId, _inputId, _lastCover, _selectedSourceId;
    private bool _outputMuted, _inputMuted;

    public CardGroup(UiConnection connection, Action openSettings)
    {
        _connection = connection;
        _openSettings = openSettings;
        _cards = [new CardWindow("流量消耗", BuildTraffic(), 284), new CardWindow("音频设备", BuildAudio(), 244),
            new CardWindow("媒体", BuildMedia(), 248)];
        foreach (var card in _cards)
        {
            card.Window.Activated += (_, args) =>
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated)
                    card.Root.DispatcherQueue.TryEnqueue(CheckForeground);
            };
            card.Window.AppWindow.Closing += (_, args) => { if (!_disposed) { args.Cancel = true; Hide(); } };
            card.Root.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { Hide(); args.Handled = true; } };
        }
        _cards[0].Root.ActualThemeChanged += (_, _) => RenderTraffic(_traffic);
        _output.IsEnabled = _input.IsEnabled = _outputMute.IsEnabled = _inputMute.IsEnabled = _outputVolume.IsEnabled = _inputVolume.IsEnabled = false;
        _previous.IsEnabled = _play.IsEnabled = _next.IsEnabled = _seek.IsEnabled = _source.IsEnabled = false;
        _timer.Tick += async (_, _) =>
        {
            CheckForeground();
            if (_visible) await RefreshAsync(++_ticks % 8 == 0, _ticks % 25 == 0);
        };
        _volumeTimer.Tick += async (_, _) => await FlushVolumesAsync();
        _seekTimer.Tick += async (_, _) =>
        {
            _seekTimer.Stop();
            try { await CommandAsync("mediaCommand", new { action = "seek", positionSeconds = _seek.Value }); }
            finally { _seekPending = false; }
        };
        RenderTraffic(_traffic);
        _trafficTotal.Text = "—";
        _trafficStatus.Text = "打开后读取流量历史";
    }

    public void ToggleAtPhysicalPoint(double x, double y)
    {
        if (_disposed) return;
        if (_visible) { Hide(); return; }
        if ((DateTime.UtcNow - _deactivatedAt).TotalMilliseconds < 350 && Math.Abs(x - _anchorX) < 120 && Math.Abs(y - _anchorY) < 60) return;
        _anchorX = x; _anchorY = y;
        Position(x, y);
        _visible = true;
        _generation++;
        _ticks = 0;
        _visibleLifetime = new CancellationTokenSource();
        foreach (var card in _cards) card.Show();
        _cards[2].Window.Activate();
        _timer.Start();
        _ = RefreshAsync(true, true);
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        _generation++;
        _timer.Stop();
        _seekTimer.Stop();
        _seekPending = false;
        _visibleLifetime?.Cancel();
        _visibleLifetime?.Dispose();
        _visibleLifetime = null;
        foreach (var flyout in _flyouts) flyout.Hide();
        foreach (var card in _cards) card.Hide();
    }

    private FrameworkElement BuildTraffic()
    {
        var content = new StackPanel { Spacing = 6 };
        var header = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Add(header, Text("流量消耗 · 最近 12 周", 14), 0);
        Add(header, _trafficTotal, 1);
        content.Children.Add(header);
        content.Children.Add(_trafficScope);
        var calendar = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        _months.Margin = new Thickness(18, 0, 0, 3);
        for (var week = 0; week < 12; week++) _months.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        calendar.Children.Add(_months);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        for (var week = 0; week < 12; week++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        for (var day = 0; day < 7; day++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        for (var weekday = 0; weekday < 7; weekday += 2)
        {
            var label = Text(new[] { "一", "三", "五", "日" }[weekday / 2], 10);
            label.VerticalAlignment = VerticalAlignment.Center;
            Add(grid, label, 0, weekday);
        }
        for (var index = 0; index < _days.Length; index++)
        {
            var slot = new Grid { Width = 18, Height = 18 };
            var glow = _dayGlows[index] = new Grid { Width = 14, Height = 14 };
            var button = _days[index] = new Button { Width = 14, Height = 14, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), CornerRadius = new CornerRadius(3) };
            slot.Children.Add(glow); slot.Children.Add(button);
            button.Click += (_, _) => { if (button.Tag is TrafficHeatmapDay day) { _selectedDate = day.Date; RenderTraffic(_traffic); } };
            button.Loaded += (_, _) => UpdateGlow(button, glow);
            button.GotFocus += (_, _) => UpdateGlow(button, glow);
            button.LostFocus += (_, _) => UpdateGlow(button, glow);
            button.KeyDown += OnDayKeyDown;
            Add(grid, slot, index / 7 + 1, index % 7);
        }
        calendar.Children.Add(grid);
        content.Children.Add(calendar);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        legend.Children.Add(Text("低", 10));
        for (var index = 0; index < _legend.Length; index++)
        {
            _legend[index] = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            legend.Children.Add(_legend[index]);
        }
        legend.Children.Add(Text("高 ≥5 GB · 灰格 0 · 空框 无记录", 10));
        content.Children.Add(legend);
        content.Children.Add(_trafficDetail);
        content.Children.Add(_trafficStatus);
        return content;
    }

    private FrameworkElement BuildAudio()
    {
        var content = new StackPanel { Spacing = 8 };
        AddAudioSection(content, "输出设备", "output", _output, _outputName, _outputMute, _outputVolume, _outputPercent);
        AddAudioSection(content, "输入设备", "input", _input, _inputName, _inputMute, _inputVolume, _inputPercent);
        content.Children.Add(_status);
        return content;
    }

    private void AddAudioSection(StackPanel parent, string title, string flow, Button selector, TextBlock name, Button mute, Slider volume, TextBlock percent)
    {
        var section = new StackPanel { Spacing = 3 };
        section.Children.Add(Text(title, 10));
        var row = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Add(row, name, 0); Add(row, new FontIcon { Glyph = "\uE70D", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, 1);
        selector.Content = row;
        var flyout = NewFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            foreach (var endpoint in flow == "input" ? _inputs : _outputs)
            {
                var item = new MenuFlyoutItem { Text = endpoint.Name + (endpoint.IsCommunications ? " · 通信" : "") };
                if (endpoint.IsDefault) item.Icon = new FontIcon { Glyph = "\uE73E" };
                item.Click += async (_, _) => await SelectDeviceAsync(flow, endpoint.Id);
                flyout.Items.Add(item);
            }
            if (flyout.Items.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = "没有可用设备", IsEnabled = false });
        };
        selector.Flyout = flyout;
        AutomationProperties.SetName(selector, "选择" + title);
        section.Children.Add(selector);
        var levels = Columns(GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(30));
        Add(levels, mute, 0); Add(levels, volume, 1); Add(levels, percent, 2);
        percent.VerticalAlignment = VerticalAlignment.Center;
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        section.Children.Add(levels);
        volume.ValueChanged += (_, args) =>
        {
            percent.Text = $"{args.NewValue:0}";
            var id = flow == "input" ? _inputId : _outputId;
            if (_disposed || _rendering || id is null) return;
            _pendingVolumes[flow] = (id, args.NewValue / 100);
            _volumeTimer.Stop(); _volumeTimer.Start();
        };
        mute.Click += async (_, _) => await CommandAsync("setMute", new { flow,
            deviceId = flow == "input" ? _inputId : _outputId, muted = !(flow == "input" ? _inputMuted : _outputMuted) }, true);
        AutomationProperties.SetName(volume, title + "音量");
        parent.Children.Add(section);
    }

    private FrameworkElement BuildMedia()
    {
        var content = new Grid();
        foreach (var size in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            content.RowDefinitions.Add(new RowDefinition { Height = size });
        var summary = Columns(new GridLength(64), new GridLength(1, GridUnitType.Star), GridLength.Auto);
        var cover = new Grid();
        var placeholder = Text("♫", 27); placeholder.Opacity = 0.45;
        placeholder.HorizontalAlignment = HorizontalAlignment.Center; placeholder.VerticalAlignment = VerticalAlignment.Center;
        cover.Children.Add(placeholder); cover.Children.Add(_cover);
        var artwork = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)), Child = cover };
        Add(summary, artwork, 0);
        var metadata = new StackPanel { Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        metadata.Children.Add(_title); metadata.Children.Add(_artist);
        _source.Content = _sourceLabel;
        var sources = NewFlyout();
        sources.Opening += (_, _) =>
        {
            sources.Items.Clear();
            foreach (var choice in _sources)
            {
                var item = new MenuFlyoutItem { Text = choice.Label };
                if (choice.Id == _selectedSourceId) item.Icon = new FontIcon { Glyph = "\uE73E" };
                item.Click += async (_, _) => await CommandAsync("mediaCommand", new { action = "selectSource", sourceId = choice.Id });
                sources.Items.Add(item);
            }
        };
        _source.Flyout = sources;
        metadata.Children.Add(_source);
        Add(summary, metadata, 1);
        var settings = Icon("\uE713", "打开设置");
        settings.VerticalAlignment = VerticalAlignment.Top;
        settings.Click += (_, _) => { Hide(); _openSettings(); };
        Add(summary, settings, 2);
        content.Children.Add(summary);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        controls.Children.Add(_previous); controls.Children.Add(_play); controls.Children.Add(_next);
        Add(content, controls, 0, 2);
        _previous.Click += async (_, _) => await CommandAsync("mediaCommand", new { action = "previous" });
        _play.Click += async (_, _) => await CommandAsync("mediaCommand", new { action = "playPause" });
        _next.Click += async (_, _) => await CommandAsync("mediaCommand", new { action = "next" });
        var progress = Columns(GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Add(progress, _position, 0); Add(progress, _seek, 1); Add(progress, _duration, 2);
        _position.VerticalAlignment = _duration.VerticalAlignment = VerticalAlignment.Center;
        _seek.Margin = new Thickness(10, 0, 10, 0);
        _seek.ValueChanged += (_, _) =>
        {
            if (_disposed || _rendering) return;
            _seekPending = true;
            _seekTimer.Stop(); _seekTimer.Start();
        };
        AutomationProperties.SetName(_seek, "播放进度");
        AutomationProperties.SetName(_source, "播放来源");
        Add(content, progress, 0, 3);
        return content;
    }

    private MenuFlyout NewFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => _flyoutsOpen++;
        flyout.Closed += (_, _) =>
        {
            _flyoutsOpen = Math.Max(0, _flyoutsOpen - 1);
            if (_cards.Length > 0) _cards[0].Root.DispatcherQueue.TryEnqueue(CheckForeground);
        };
        _flyouts.Add(flyout);
        return flyout;
    }

    private async Task RefreshAsync(bool audio, bool traffic)
    {
        if (!_visible || _visibleLifetime is null) return;
        if (_refreshing) { _refreshAgain = true; _audioAgain |= audio; _trafficAgain |= traffic; return; }
        _refreshing = true;
        var generation = _generation;
        var token = _visibleLifetime.Token;
        try
        {
            var state = await _connection.RequestAsync("getState", cancellationToken: token);
            if (!_visible || generation != _generation) return;
            RenderMedia(state);
            if (audio && !_switching)
            {
                var audioGeneration = _audioGeneration;
                var devices = await _connection.RequestAsync("getAudio", cancellationToken: token);
                if (!_visible || generation != _generation || audioGeneration != _audioGeneration) return;
                RenderAudio(devices);
            }
            if (traffic)
            {
                var history = await _connection.RequestAsync("getTrafficHistory", cancellationToken: token);
                if (!_visible || generation != _generation) return;
                RenderTraffic(TrafficHeatmap.Parse(history));
                _trafficStatus.Text = "";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_visible && generation == _generation)
            {
                _status.Text = exception.Message;
                if (traffic) _trafficStatus.Text = "流量历史暂不可用，稍后重试";
            }
        }
        finally
        {
            _refreshing = false;
            if (_visible && _refreshAgain)
            {
                var nextAudio = _audioAgain; var nextTraffic = _trafficAgain;
                _refreshAgain = _audioAgain = _trafficAgain = false;
                _ = RefreshAsync(nextAudio, nextTraffic);
            }
        }
    }

    private void RenderMedia(JsonElement state)
    {
        _rendering = true;
        try
        {
            _title.Text = String(state, "title", "暂无媒体");
            _artist.Text = String(state, "artist");
            var source = String(state, "source");
            _sources = Array(state, "sources").Select(value => new SourceChoice(String(value, "id"), String(value, "label"))).ToArray();
            _selectedSourceId = String(state, "selectedSourceId");
            _sourceLabel.Text = source + (_sources.Length > 1 ? "  ▾" : "");
            _source.IsEnabled = _sources.Length > 1;
            _previous.IsEnabled = Bool(state, "canPrevious"); _next.IsEnabled = Bool(state, "canNext");
            _play.IsEnabled = Bool(state, "canPlayPause");
            ((FontIcon)_play.Content).Glyph = Bool(state, "isPlaying") ? "\uE769" : "\uE768";
            var duration = Number(state, "durationSeconds");
            _seek.IsEnabled = Bool(state, "canSeek") && duration > 0;
            if (!_seekPending) { _seek.Maximum = Math.Max(1, duration); _seek.Value = Math.Clamp(Number(state, "positionSeconds"), 0, _seek.Maximum); }
            _position.Text = Time(Number(state, "positionSeconds")); _duration.Text = Time(duration);
            var theme = (int)Number(state, "toolWindowTheme");
            foreach (var card in _cards) card.Root.RequestedTheme = theme == 1 ? ElementTheme.Light : theme == 2 ? ElementTheme.Dark : ElementTheme.Default;
            var cover = String(state, "coverDataUrl");
            if (_lastCover != cover) { _lastCover = cover; _ = RenderCoverAsync(cover); }
        }
        finally { _rendering = false; }
    }

    private async Task RenderCoverAsync(string data)
    {
        _cover.Source = null;
        try
        {
            var comma = data.IndexOf(',');
            if (comma < 0 || data.Length > 4 * 1024 * 1024 || !data.StartsWith("data:image/", StringComparison.Ordinal)) return;
            var bytes = Convert.FromBase64String(data[(comma + 1)..]);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (!_disposed && _lastCover == data) _cover.Source = image;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { System.Diagnostics.Trace.WriteLine("Cover image ignored: " + exception.Message); }
    }

    private void RenderAudio(JsonElement snapshot)
    {
        _outputs = AudioChoices(snapshot, "outputs"); _inputs = AudioChoices(snapshot, "inputs");
        _rendering = true;
        try
        {
            RenderEndpoint(_outputs, false); RenderEndpoint(_inputs, true);
        }
        finally { _rendering = false; }
    }

    private void RenderEndpoint(AudioChoice[] devices, bool input)
    {
        var current = devices.FirstOrDefault(device => device.IsDefault);
        var flow = input ? "input" : "output";
        (input ? _inputName : _outputName).Text = current?.Name ?? "没有可用设备";
        var slider = input ? _inputVolume : _outputVolume;
        var mute = input ? _inputMute : _outputMute;
        slider.IsEnabled = mute.IsEnabled = current is not null && !_switching;
        (input ? _input : _output).IsEnabled = devices.Length > 0 && !_switching;
        if (!_pendingVolumes.ContainsKey(flow) && !_writingFlows.Contains(flow)) slider.Value = Math.Clamp((current?.Volume ?? 0) * 100, 0, 100);
        ((FontIcon)mute.Content).Glyph = current?.IsMuted == true ? "\uE74F" : input ? "\uE720" : "\uE767";
        if (input) { _inputId = current?.Id; _inputMuted = current?.IsMuted ?? false; }
        else { _outputId = current?.Id; _outputMuted = current?.IsMuted ?? false; }
    }

    private async Task SelectDeviceAsync(string flow, string id)
    {
        if (_disposed || _switching) return;
        _switching = true;
        _audioGeneration++;
        _pendingVolumes.Remove(flow);
        _output.IsEnabled = _input.IsEnabled = _outputMute.IsEnabled = _inputMute.IsEnabled = _outputVolume.IsEnabled = _inputVolume.IsEnabled = false;
        try { await CommandAsync("selectDevice", new { flow, deviceId = id }); }
        finally
        {
            _switching = false;
            if (_visible) await RefreshAsync(true, false);
        }
    }

    private async Task FlushVolumesAsync()
    {
        _volumeTimer.Stop();
        if (_writingVolumes) return;
        _writingVolumes = true;
        var pending = _pendingVolumes.ToArray();
        _pendingVolumes.Clear();
        try
        {
            foreach (var (flow, value) in pending)
            {
                _writingFlows.Add(flow);
                try { await CommandAsync("setVolume", new { flow, deviceId = value.Id, volume = value.Volume }); }
                finally { _writingFlows.Remove(flow); }
            }
        }
        finally
        {
            _writingVolumes = false;
            if (!_disposed && _pendingVolumes.Count > 0) _volumeTimer.Start();
        }
    }

    private async Task CommandAsync(string action, object payload, bool refreshAudio = false)
    {
        if (_disposed) return;
        try
        {
            var reply = await _connection.RequestAsync(action, payload);
            if (!_disposed) _status.Text = String(reply, "message");
            if (_visible) await RefreshAsync(refreshAudio, false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (!_disposed) _status.Text = exception.Message; }
    }

    private void RenderTraffic(TrafficHeatmap traffic)
    {
        if (_disposed) return;
        _traffic = traffic;
        if (_selectedDate is null || !traffic.Days.Any(day => day.Date == _selectedDate && !day.IsFuture)) _selectedDate = traffic.Today;
        _trafficTotal.Text = TrafficHeatmap.FormatUsage(traffic.TotalKiB);
        _trafficScope.Text = traffic.ScopeLabel;
        _trafficDetail.Text = traffic.Days.First(day => day.Date == _selectedDate).Description;
        for (var index = 0; index < _legend.Length; index++) _legend[index].Background = new SolidColorBrush(ColorForLevel(index + 1));
        _months.Children.Clear();
        for (var week = 0; week < 12; week++)
        {
            var day = traffic.Days[week * 7];
            if (week > 0 && day.Date.Month == traffic.Days[(week - 1) * 7].Date.Month) continue;
            Add(_months, Text(day.Date.Month + "月", 9), week);
        }
        for (var index = 0; index < _days.Length; index++)
        {
            var button = _days[index]; var day = traffic.Days[index];
            button.Tag = day; button.IsEnabled = !day.IsFuture;
            button.Background = new SolidColorBrush(DayColor(day));
            button.BorderThickness = new Thickness(day.Date == _selectedDate ? 2 : day.Level == 0 ? 1 : 0);
            button.BorderBrush = new SolidColorBrush(IsDark ? Color.FromArgb(160, 230, 230, 230) : Color.FromArgb(160, 40, 40, 40));
            // Keep native button focus behavior while preserving semantic usage
            // colors through Fluent pointer/disabled visual states.
            button.Resources["ButtonBackgroundPointerOver"] = button.Background;
            button.Resources["ButtonBackgroundPressed"] = button.Background;
            button.Resources["ButtonBackgroundDisabled"] = button.Background;
            button.Resources["ButtonBorderBrushPointerOver"] = button.BorderBrush;
            button.Resources["ButtonBorderBrushPressed"] = button.BorderBrush;
            button.Resources["ButtonBorderBrushDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetName(button, day.Description);
            if (button.IsLoaded) UpdateGlow(button, _dayGlows[index]);
        }
    }

    private bool IsDark => _cards.Length > 0 && _cards[0].Root.ActualTheme == ElementTheme.Dark;
    private Color DayColor(TrafficHeatmapDay day)
    {
        if (day.IsFuture) return Microsoft.UI.Colors.Transparent;
        return ColorForLevel(day.Level);
    }

    private Color ColorForLevel(int level)
    {
        var light = new[] { 0x00FFFFFFu, 0xFFE5E8ECu, 0xFFB4D8C0u, 0xFF6DB889u, 0xFF338557u, 0xFF175736u };
        var dark = new[] { 0x0026272Bu, 0xFF44484Eu, 0xFF294B3Bu, 0xFF347654u, 0xFF42A56Bu, 0xFF76D99Au };
        var value = (IsDark ? dark : light)[level];
        return Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private void UpdateGlow(Button button, Grid host)
    {
        if (_disposed) return;
        if (button.Tag is not TrafficHeatmapDay day) return;
        var emphasized = day.Date == _selectedDate || button.FocusState != FocusState.Unfocused;
        if (day.IsFuture || day.Level < 2) { host.Visibility = Visibility.Collapsed; return; }
        host.Visibility = Visibility.Visible;
        if (!_glows.TryGetValue(button, out var glow))
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            var sprite = compositor.CreateSpriteVisual(); sprite.Size = new Vector2(14, 14);
            var shadow = compositor.CreateDropShadow(); shadow.Offset = Vector3.Zero; sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);
            glow = (sprite, shadow); _glows.Add(button, glow);
        }
        var color = DayColor(day);
        glow.Visual.Brush = glow.Visual.Compositor.CreateColorBrush(color);
        glow.Shadow.Color = color;
        glow.Shadow.BlurRadius = emphasized ? 8 : 4;
        glow.Shadow.Opacity = emphasized ? 0.65f : IsDark ? 0.3f : 0.18f;
    }

    private void OnDayKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var index = System.Array.IndexOf(_days, (Button)sender);
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)) return;
        args.Handled = true;
        var next = args.Key switch { VirtualKey.Left => index - 7, VirtualKey.Right => index + 7,
            VirtualKey.Up when index % 7 > 0 => index - 1, VirtualKey.Down when index % 7 < 6 => index + 1, _ => -1 };
        if (next < 0 || next >= _days.Length || !_days[next].IsEnabled) return;
        _days[next].Focus(FocusState.Keyboard);
    }

    private void CheckForeground()
    {
        if (!_visible || _disposed) return;
        var foreground = GetForegroundWindow();
        // WinUI flyouts can own a separate native popup HWND; follow its native
        // owner chain as well as tracking Opening/Closed focus transitions.
        var rootOwner = GetAncestor(foreground, 3);
        if (foreground == 0 || _cards.Any(card => card.Handle == foreground || card.Handle == rootOwner)) return;
        GetWindowThreadProcessId(foreground, out var process);
        if (_flyoutsOpen > 0 && process == Environment.ProcessId) return;
        _deactivatedAt = DateTime.UtcNow;
        Hide();
    }

    internal void Position(double x, double y)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = (int)x, Y = (int)y }, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        foreach (var card in _cards)
            if (MonitorFromWindow(card.Handle, 2) != monitor) card.Window.AppWindow.Move(new PointInt32(info.Work.Left + 10, info.Work.Top + 10));
        var dpi = GetDpiForWindow(_cards[0].Handle);
        var scale = dpi > 0 ? dpi / 96d : 1;
        var width = Math.Min((int)Math.Ceiling(CardWidth * scale), Math.Max(1, info.Work.Right - info.Work.Left - (int)(20 * scale)));
        var gap = (int)Math.Round(12 * scale);
        var available = Math.Max(3, info.Work.Bottom - info.Work.Top - gap * 2 - (int)(20 * scale));
        var fit = Math.Min(1, available / (_cards.Sum(card => card.Height) * scale));
        var heights = _cards.Select(card => Math.Max(1, (int)Math.Floor(card.Height * scale * fit))).ToArray();
        var total = heights.Sum() + gap * 2;
        var left = Math.Clamp((int)Math.Round(x - width / 2d), info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        var top = Math.Clamp((int)Math.Round(y - total - 10 * scale), info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - total));
        for (var index = 0; index < _cards.Length; index++)
        {
            _cards[index].Window.AppWindow.MoveAndResize(new RectInt32(left, top, width, heights[index]));
            top += heights[index] + gap;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide(); _disposed = true;
        _volumeTimer.Stop(); _seekTimer.Stop(); _timer.Stop();
        _pendingVolumes.Clear();
        foreach (var card in _cards) card.Close();
        foreach (var glow in _glows.Values) { glow.Visual.Dispose(); glow.Shadow.Dispose(); }
        _glows.Clear();
    }

    private sealed record AudioChoice(string Id, string Name, bool IsDefault, bool IsCommunications, double Volume, bool IsMuted);
    private sealed record SourceChoice(string Id, string Label);
    private static AudioChoice[] AudioChoices(JsonElement value, string name) => Array(value, name).Select(item => new AudioChoice(
        String(item, "id"), String(item, "name"), Bool(item, "isDefault"), Bool(item, "isCommunications"), Number(item, "volume"), Bool(item, "isMuted"))).ToArray();
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
    private static string String(JsonElement value, string name, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
    private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static double Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 359999)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static TextBlock Text(string value, double size = 12) => new() { Text = value, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private static Button Icon(string glyph, string name, double size = 32)
    {
        var button = new Button { Width = size, Height = size, MinWidth = 0, Padding = new Thickness(4), CornerRadius = new CornerRadius(size / 2), Content = new FontIcon { Glyph = glyph, FontSize = size > 32 ? 20 : 16 } };
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); return button;
    }
    private static Grid Columns(params GridLength[] widths)
    {
        var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }); return grid;
    }
    private static void Add(Grid grid, FrameworkElement element, int column, int row = 0) { Grid.SetColumn(element, column); Grid.SetRow(element, row); grid.Children.Add(element); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
