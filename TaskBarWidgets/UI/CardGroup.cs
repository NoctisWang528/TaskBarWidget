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
using TaskBarWidgets.UI.Settings;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup : IDisposable
{
    internal const double CardWidth = 384;
    private readonly UiConnection _connection;
    private readonly Action _openSettings;
    private readonly CardWindow[] _cards = [];
    internal IReadOnlyList<CardWindow> CardWindows => _cards;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _seekTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly OutsideClickMonitor _outsideClicks;
    private readonly CardLyricsView _lyrics = new();
    private double _anchorX, _anchorY;
    private readonly List<MenuFlyout> _flyouts = [];
    private readonly Dictionary<string, (string Id, double Volume, int EndpointRevision)> _pendingVolumes = [];
    private readonly Dictionary<string, int> _endpointRevisions = new() { ["input"] = 0, ["output"] = 0 };
    private readonly TextBlock _title = Text("暂无媒体", 15), _artist = Text("", 12), _sourceLabel = Text("暂无来源", 11);
    private readonly Image _cover = new() { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _position = Text("0:00", 10), _duration = Text("0:00", 10), _status = Text("", 10);
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _previous = Icon("\uE892", "上一首"), _play = Icon("\uE768", "播放 / 暂停", 42), _next = Icon("\uE893", "下一首");
    private readonly Button _source = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(8, 4, 8, 4), MinHeight = 34, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Image _sourceImage = new() { Width = 20, Height = 20, Stretch = Stretch.Uniform };
    private readonly FontIcon _sourceFallback = new() { Glyph = "\uE8D6", FontSize = 16 };
    private readonly Dictionary<string, ImageSource> _sourceImageCache = [];
    private string _lastSourceIcon = "";
    private readonly Button _output = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch },
        _input = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _outputName = Text("读取输出设备…", 12), _inputName = Text("读取输入设备…", 12),
        _outputPercent = Text("0", 11), _inputPercent = Text("0", 11);
    private readonly Button _outputMute = Icon("\uE767", "输出静音"), _inputMute = Icon("\uE720", "输入静音");
    private readonly Slider _outputVolume = new() { Minimum = 0, Maximum = 100 }, _inputVolume = new() { Minimum = 0, Maximum = 100 };
    private AudioChoice[] _outputs = [], _inputs = [];
    private SourceChoice[] _sources = [];
    private CancellationTokenSource? _visibleLifetime;
    private bool _visible, _disposed, _refreshing, _rendering, _seekPending, _writingVolumes, _audioRefreshing;
    private bool _refreshAgain, _audioAgain, _trafficAgain;
    private readonly HashSet<string> _writingFlows = [];
    private readonly HashSet<string> _busyAudioFlows = [];
    private long _audioRequestSequence, _audioAppliedSequence;
    private int _audioGeneration;
    private int _generation, _ticks, _flyoutsOpen;
    private string? _outputId, _inputId, _lastCover, _selectedSourceId;
    private bool _outputMuted, _inputMuted;
    private ElementTheme? _themePreview;
    private ElementTheme _requestedTheme;
    private bool _themeApplied;

    public CardGroup(UiConnection connection, Action openSettings)
    {
        _connection = connection;
        _openSettings = openSettings;
        _cards = [new CardWindow("流量消耗", BuildTraffic(), 284), new CardWindow("音频设备", BuildAudio(), 244, autoHeight: true),
            new CardWindow("媒体", BuildMedia(), 248)];
        _outsideClicks = new OutsideClickMonitor(_cards[0].Root.DispatcherQueue, () => _flyoutsOpen > 0);
        foreach (var card in _cards)
        {
            card.Window.AppWindow.Closing += (_, args) => { if (!_disposed) { args.Cancel = true; Hide("native-close"); } };
            card.Root.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { Hide("escape"); args.Handled = true; } };
        }
        _cards[0].Root.ActualThemeChanged += (_, _) => RenderTraffic(_traffic);
        _output.IsEnabled = _input.IsEnabled = _outputMute.IsEnabled = _inputMute.IsEnabled = _outputVolume.IsEnabled = _inputVolume.IsEnabled = false;
        _previous.IsEnabled = _play.IsEnabled = _next.IsEnabled = _seek.IsEnabled = _source.IsEnabled = false;
        _timer.Tick += async (_, _) =>
        {
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
        CardLifecycleLog.Write("created cards=3");
        _status.Visibility = Visibility.Collapsed;
        _status.TextWrapping = TextWrapping.Wrap; _status.MaxLines = 3;
        _status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => {
            _status.Visibility = string.IsNullOrWhiteSpace(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (_visible) Position(_anchorX, _anchorY);
        });
        _cards[1].Root.Loaded += (_, _) => { if (_visible) Position(_anchorX, _anchorY); };
        _connection.ConnectionChanged += OnLyricsConnection;
    }

    public void ToggleAtPhysicalPoint(double x, double y, nint entryWindow = 0)
    {
        if (_disposed) return;
        if (_visible) { Hide("toggle"); return; }
        Position(x, y);
        _visible = true;
        _generation++;
        _ticks = 0;
        _visibleLifetime = new CancellationTokenSource();
        _lyrics.Activate();
        foreach (var card in _cards) card.Show();
        _cards[2].Window.Activate();
        var generation = _generation;
        _outsideClicks.Start(entryWindow, click =>
        {
            if (_disposed || !_visible || generation != _generation || !ShouldDismissClick(click)) return;
            CardLifecycleLog.Write($"outside-click button={click.MouseButton} point={click.X},{click.Y} target=0x{click.Target:X} root=0x{click.Root:X} owner=0x{click.RootOwner:X} pid={click.ProcessId} popup={click.IsPopup} flyoutAtClick={click.FlyoutWasOpen}");
            Hide("outside-click");
        });
        CardLifecycleLog.Write($"shown generation={_generation} entry=0x{entryWindow:X}");
        _timer.Start();
        _ = RefreshAsync(true, true);
        _ = SubscribeLyricsAsync(_generation, true);
    }

    public void Hide() => Hide("explicit");

    private void Hide(string reason)
    {
        if (!_visible) return;
        CardLifecycleLog.Write($"hide reason={reason} generation={_generation} flyouts={_flyoutsOpen}");
        _visible = false;
        _generation++;
        _lyrics.HideLyrics();
        _ = SubscribeLyricsAsync(_generation, false);
        _timer.Stop();
        _outsideClicks.Stop();
        _seekTimer.Stop();
        _seekPending = false;
        _visibleLifetime?.Cancel();
        _visibleLifetime?.Dispose();
        _visibleLifetime = null;
        foreach (var flyout in _flyouts) flyout.Hide();
        foreach (var card in _cards) HideCard(card);
    }

    // Hide is a UI callback boundary: unexpected task faults must still reach
    // WinUI's unhandled-exception handler instead of becoming unobserved tasks.
    private static async void HideCard(CardWindow card)
    {
        try { await card.HideAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CardLifecycleLog.Write($"hide-unexpected hwnd=0x{card.Handle:X} type={exception.GetType().Name} hr=0x{exception.HResult:X8}");
            throw;
        }
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
            _pendingVolumes[flow] = (id, args.NewValue / 100, _endpointRevisions[flow]);
            _volumeTimer.Stop(); _volumeTimer.Start();
        };
        mute.Click += async (_, _) => await MuteAsync(flow);
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
        var sourceButton = Columns(new GridLength(20), new GridLength(1, GridUnitType.Star), GridLength.Auto);
        sourceButton.ColumnSpacing = 8;
        var sourceBadge = new Grid(); sourceBadge.Children.Add(_sourceFallback); sourceBadge.Children.Add(_sourceImage);
        Add(sourceButton, sourceBadge, 0); Add(sourceButton, _sourceLabel, 1);
        _sourceLabel.FontSize = 12; _sourceLabel.VerticalAlignment = VerticalAlignment.Center;
        Add(sourceButton, new FontIcon { Glyph = "\uE70D", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, 2);
        _source.Content = sourceButton;
        ToolTipService.SetToolTip(_source, "切换媒体来源");
        var sources = NewFlyout();
        sources.Opening += (_, _) =>
        {
            sources.Items.Clear();
            foreach (var choice in _sources)
            {
                var item = new MenuFlyoutItem { Text = choice.Label + (choice.Id == _selectedSourceId ? " · 当前" : ""), MinHeight = 44, MinWidth = 240, MaxWidth = 352,
                    Icon = new FontIcon { Glyph = "\uE8D6" } };
                _ = SetSourceMenuIconAsync(item, choice.IconDataUrl);
                item.Click += async (_, _) => await CommandAsync("mediaCommand", new { action = "selectSource", sourceId = choice.Id });
                sources.Items.Add(item);
            }
        };
        _source.Flyout = sources;
        metadata.Children.Add(_source);
        Add(summary, metadata, 1);
        var settings = Icon("\uE713", "打开设置");
        settings.VerticalAlignment = VerticalAlignment.Top;
        settings.Click += (_, _) => { Hide("settings"); _openSettings(); };
        Add(summary, settings, 2);
        content.Children.Add(summary);
        Add(content, _lyrics, 0, 1);
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

    // A settings draft owns the live preview until it is saved or reloaded. Poll replies
    // may have been queued before that edit and must not overwrite it.
    internal void SetThemePreview(ElementTheme theme)
    {
        _themePreview = theme;
        ApplyTheme(theme);
    }
    internal void ApplyBackendTheme(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("toolWindowTheme", out var value) || value.ValueKind == JsonValueKind.Null) return;
        ApplyTheme(_themePreview ?? UiThemeStyles.ParseTheme(value.ToString()));
    }
    private void ApplyTheme(ElementTheme theme)
    {
        if (_disposed || (_themeApplied && _requestedTheme == theme)) return;
        _themeApplied = true;
        _requestedTheme = theme;
        foreach (var card in _cards) card.Root.RequestedTheme = theme;
        foreach (var flyout in _flyouts) ThemeFlyout(flyout);
        foreach (var card in _cards)
            if (card.Root.XamlRoot is { } root)
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                    if (popup.Child is FrameworkElement child) child.RequestedTheme = theme;
    }
    private void ThemeFlyout(MenuFlyout flyout)
    {
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, _requestedTheme));
        flyout.MenuFlyoutPresenterStyle = style;
    }

    private MenuFlyout NewFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => { ThemeFlyout(flyout); _flyoutsOpen++; CardLifecycleLog.Write($"flyout-opening count={_flyoutsOpen}"); };
        flyout.Closed += (_, _) =>
        {
            _flyoutsOpen = Math.Max(0, _flyoutsOpen - 1);
            CardLifecycleLog.Write($"flyout-closed count={_flyoutsOpen}");
        };
        _flyouts.Add(flyout);
        return flyout;
    }

    private async Task RefreshAsync(bool audio, bool traffic)
    {
        if (!_visible || _visibleLifetime is null) return;
        // Audio must not wait for media metadata or native traffic-history requests.
        if (audio) _ = RefreshAudioAsync(_visibleLifetime.Token);
        if (_refreshing) { _refreshAgain = true; _audioAgain |= audio; _trafficAgain |= traffic; return; }
        _refreshing = true;
        var generation = _generation;
        var token = _visibleLifetime.Token;
        try
        {
            var state = await _connection.RequestAsync("getState", cancellationToken: token);
            if (!_visible || generation != _generation) return;
            RenderMedia(state);
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
            _sources = Array(state, "sources").Select(value => new SourceChoice(String(value, "id"), String(value, "label"), String(value, "iconDataUrl"))).ToArray();
            _selectedSourceId = String(state, "selectedSourceId");
            var selected = _sources.FirstOrDefault(choice => choice.Id == _selectedSourceId);
            _sourceLabel.Text = selected?.Label ?? source;
            var icon = selected?.IconDataUrl ?? "";
            if (icon != _lastSourceIcon) { _lastSourceIcon = icon; _sourceImage.Source = null; _sourceFallback.Visibility = Visibility.Visible; _ = RenderSelectedSourceIconAsync(icon); }
            _source.IsEnabled = _sources.Length > 1;
            _previous.IsEnabled = Bool(state, "canPrevious"); _next.IsEnabled = Bool(state, "canNext");
            _play.IsEnabled = Bool(state, "canPlayPause");
            ((FontIcon)_play.Content).Glyph = Bool(state, "isPlaying") ? "\uE769" : "\uE768";
            var duration = Number(state, "durationSeconds");
            _seek.IsEnabled = Bool(state, "canSeek") && duration > 0;
            if (!_seekPending) { _seek.Maximum = Math.Max(1, duration); _seek.Value = Math.Clamp(Number(state, "positionSeconds"), 0, _seek.Maximum); }
            _position.Text = Time(Number(state, "positionSeconds")); _duration.Text = Time(duration);
            ApplyBackendTheme(state);
            var cover = String(state, "coverDataUrl");
            if (_lastCover != cover) { _lastCover = cover; _ = RenderCoverAsync(cover); }
        }
        finally { _rendering = false; }
    }

    private async Task<ImageSource?> SourceImageAsync(string data)
    {
        if (_sourceImageCache.TryGetValue(data, out var cached)) return cached;
        if (data.Length == 0 || data.Length > 256 * 1024 || !data.StartsWith("data:image/", StringComparison.Ordinal)) return null;
        try
        {
            var comma = data.IndexOf(','); if (comma < 0) return null;
            var header = data[..comma];
            var bytes = header.EndsWith(";base64", StringComparison.Ordinal) ? Convert.FromBase64String(data[(comma + 1)..]) : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data[(comma + 1)..]));
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            ImageSource image;
            if (header.StartsWith("data:image/svg+xml", StringComparison.Ordinal))
            { var svg = new SvgImageSource(); await svg.SetSourceAsync(stream); image = svg; }
            else { var bitmap = new BitmapImage { DecodePixelWidth = 40 }; await bitmap.SetSourceAsync(stream); image = bitmap; }
            if (_sourceImageCache.Count >= 64) _sourceImageCache.Clear();
            _sourceImageCache[data] = image;
            return image;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
    private async Task RenderSelectedSourceIconAsync(string data)
    {
        var image = await SourceImageAsync(data);
        if (_disposed || _lastSourceIcon != data) return;
        _sourceImage.Source = image;
        _sourceFallback.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task SetSourceMenuIconAsync(MenuFlyoutItem item, string data)
    {
        var image = await SourceImageAsync(data);
        if (!_disposed && image is not null) item.Icon = new ImageIcon { Source = image };
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

    private async Task RefreshAudioAsync(CancellationToken token)
    {
        if (_disposed || _audioRefreshing || _busyAudioFlows.Count > 0) return;
        _audioRefreshing = true;
        var generation = _audioGeneration;
        var sequence = ++_audioRequestSequence;
        try
        {
            var snapshot = await _connection.RequestAsync("getAudio", cancellationToken: token);
            if (!_disposed && !token.IsCancellationRequested && generation == _audioGeneration)
                ApplyAudioReply(snapshot, sequence);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_disposed && !token.IsCancellationRequested) _status.Text = "读取音频设备失败：" + error.Message; }
        finally { _audioRefreshing = false; }
    }

    private void ApplyAudioReply(JsonElement snapshot, long sequence, string? completedFlow = null)
    {
        if (sequence < _audioAppliedSequence) return;
        if (!snapshot.TryGetProperty("outputs", out _) || !snapshot.TryGetProperty("inputs", out _))
            throw new InvalidDataException("音频服务没有返回设备状态。");
        _audioAppliedSequence = sequence;
        RenderAudio(snapshot, completedFlow);
    }

    private void RenderAudio(JsonElement snapshot, string? completedFlow = null)
    {
        _outputs = AudioChoices(snapshot, "outputs"); _inputs = AudioChoices(snapshot, "inputs");
        _rendering = true;
        try
        {
            if (!_busyAudioFlows.Contains("output") || completedFlow == "output") RenderEndpoint(_outputs, false);
            if (!_busyAudioFlows.Contains("input") || completedFlow == "input") RenderEndpoint(_inputs, true);
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
        slider.IsEnabled = mute.IsEnabled = current is { CanControl: true } && !_busyAudioFlows.Contains(flow);
        (input ? _input : _output).IsEnabled = devices.Length > 0 && !_busyAudioFlows.Contains(flow);
        if (!_pendingVolumes.ContainsKey(flow) && !_writingFlows.Contains(flow)) slider.Value = Math.Clamp((current?.Volume ?? 0) * 100, 0, 100);
        if (current is { CanControl: false }) {
            (input ? _inputPercent : _outputPercent).Text = "—";
            _status.Text = (input ? "输入" : "输出") + "设备音量暂不可用，仍可选择其他设备。";
        }
        ((FontIcon)mute.Content).Glyph = current?.IsMuted == true ? "\uE74F" : input ? "\uE720" : "\uE767";
        AutomationProperties.SetName(mute, (input ? "输入" : "输出") + (current?.IsMuted == true ? "取消静音" : "静音"));
        ToolTipService.SetToolTip(mute, current?.IsMuted == true ? "取消静音" : "静音");
        if (input) { _inputId = current?.Id; _inputMuted = current?.IsMuted ?? false; }
        else { _outputId = current?.Id; _outputMuted = current?.IsMuted ?? false; }
    }

    private async Task SelectDeviceAsync(string flow, string id)
    {
        if (_disposed || _busyAudioFlows.Contains(flow)) return;
        ++_endpointRevisions[flow];
        _pendingVolumes.Remove(flow);
        await AudioCommandAsync("selectDevice", flow, new { flow, deviceId = id }, "正在切换设备…");
    }

    private void RenderAudioControls(string flow)
    {
        var rendering = _rendering;
        _rendering = true;
        try { RenderEndpoint(flow == "input" ? _inputs : _outputs, flow == "input"); }
        finally { _rendering = rendering; }
    }

    private async Task MuteAsync(string flow)
    {
        var id = flow == "input" ? _inputId : _outputId;
        if (_disposed || id is null || _busyAudioFlows.Contains(flow)) return;
        var muted = !(flow == "input" ? _inputMuted : _outputMuted);
        await AudioCommandAsync("setMute", flow, new { flow, deviceId = id, muted }, muted ? "正在静音…" : "正在取消静音…");
    }

    private async Task AudioCommandAsync(string action, string flow, object payload, string message, bool lockControls = true)
    {
        if (_disposed) return;
        if (lockControls && !_busyAudioFlows.Add(flow)) return;
        ++_audioGeneration;
        var sequence = ++_audioRequestSequence;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _status.Text = message;
        if (lockControls) RenderAudioControls(flow);
        CardLifecycleLog.Write($"audio-request action={action} flow={flow} sequence={sequence}");
        try
        {
            var reply = await _connection.RequestAsync(action, payload);
            if (_disposed) return;
            ApplyAudioReply(reply, sequence, flow);
            if (reply.TryGetProperty("operationSucceeded", out var succeeded) && succeeded.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException(String(reply, "message", "音频设备仅部分切换，请检查默认设备。"));
            if (sequence == _audioRequestSequence) _status.Text = "";
            CardLifecycleLog.Write($"audio-applied action={action} flow={flow} sequence={sequence} elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_disposed) _status.Text = "音频操作失败：" + error.Message;
            CardLifecycleLog.Write($"audio-error action={action} flow={flow} sequence={sequence} hr=0x{error.HResult:X8}");
        }
        finally
        {
            if (lockControls) _busyAudioFlows.Remove(flow);
            if (!_disposed) RenderAudioControls(flow);
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
                if (value.EndpointRevision != _endpointRevisions[flow]) continue;
                _writingFlows.Add(flow);
                try { await AudioCommandAsync("setVolume", flow, new { flow, deviceId = value.Id, volume = value.Volume }, "正在调整音量…", lockControls: false); }
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

    internal bool ShouldDismissClick(OutsideClickMonitor.Click click)
    {
        // A left click on the validated native entry HWND belongs to its normal
        // toggle command. Other buttons must still dismiss before opening menus.
        if (click.MouseButton == OutsideClickMonitor.Button.Left && click.IsEntryTarget) return false;
        foreach (var card in _cards)
        {
            if (IsWindowVisible(card.Handle) && GetWindowRect(card.Handle, out var bounds) &&
                click.X >= bounds.Left && click.X < bounds.Right && click.Y >= bounds.Top && click.Y < bounds.Bottom)
                return false;
        }
        // The click snapshot survives MenuFlyout.Closed and native popup teardown
        // before the queued classifier runs. Same-process unrelated windows do not qualify.
        if (click.ProcessId == (uint)Environment.ProcessId &&
            (_cards.Any(card => card.Handle == click.RootOwner) || click.FlyoutWasOpen && click.IsPopup)) return false;
        return true;
    }

    internal void Position(double x, double y)
    {
        _anchorX = x; _anchorY = y;
        foreach (var card in _cards) card.MeasureHeight();
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
        CardLifecycleLog.Write($"dispose visible={_visible}");
        _disposed = true; _visible = false; _generation++;
        _visibleLifetime?.Cancel(); _visibleLifetime?.Dispose(); _visibleLifetime = null;
        _volumeTimer.Stop(); _seekTimer.Stop(); _timer.Stop(); _outsideClicks.Dispose();
        _connection.ConnectionChanged -= OnLyricsConnection;
        _lyrics.Dispose();
        foreach (var flyout in _flyouts) flyout.Hide();
        _pendingVolumes.Clear();
        foreach (var card in _cards) card.Close();
        foreach (var glow in _glows.Values) { glow.Shadow.Mask?.Dispose(); glow.Visual.Dispose(); glow.Shadow.Dispose(); }
        _glows.Clear();
    }

    private void OnLyricsConnection(bool connected)
    {
        _cards[2].Root.DispatcherQueue.TryEnqueue(() => {
            if (_disposed || !_visible) return;
            if (connected) { _lyrics.Activate(); _ = SubscribeLyricsAsync(_generation, true); }
            else _lyrics.HideLyrics();
        });
    }
    private async Task SubscribeLyricsAsync(int generation, bool visible)
    {
        try
        {
            var result = await _connection.RequestAsync("subscribeCardLyrics", new { visible, session = generation });
            if (_disposed || !_visible || generation != _generation || !visible) return;
            _lyrics.Initialize(String(result, "html"));
            if (result.TryGetProperty("snapshot", out var snapshot)) _lyrics.Receive(snapshot);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_disposed && generation == _generation) CardLifecycleLog.Write($"card-lyrics subscribe-failed hr=0x{error.HResult:X8}"); }
    }
    internal void ReceiveLyrics(JsonElement message)
    {
        if (_disposed || !_visible || !message.TryGetProperty("session", out var session) || session.GetInt64() != _generation) return;
        if (message.TryGetProperty("payload", out var payload)) _lyrics.Receive(payload);
    }
    private sealed record AudioChoice(string Id, string Name, bool IsDefault, bool IsCommunications, double Volume, bool IsMuted, bool CanControl);
    private sealed record SourceChoice(string Id, string Label, string IconDataUrl);
    private static AudioChoice[] AudioChoices(JsonElement value, string name) => Array(value, name).Select(item => new AudioChoice(
        String(item, "id"), String(item, "name"), Bool(item, "isDefault"), Bool(item, "isCommunications"), Number(item, "volume"), Bool(item, "isMuted"),
        !item.TryGetProperty("canControl", out var control) || control.ValueKind != JsonValueKind.False)).ToArray();
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
    private static string String(JsonElement value, string name, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
    private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static double Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 359999)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static TextBlock Text(string value, double size = 12) => UiThemeStyles.Apply(new TextBlock { Text = value, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 }, "CardTextPrimary");
    private static void Secondary(TextBlock text) => UiThemeStyles.Apply(text, "CardTextSecondary");
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

internal static class CardLifecycleLog
{
    private static readonly object Gate = new();
    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "card-lifecycle.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 128 * 1024) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:o} pid={Environment.ProcessId} {message[..Math.Min(message.Length, 512)]}\n");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
