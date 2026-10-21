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
    internal const double UpdateCardWidth = 220;
    private readonly UiConnection _connection;
    private readonly Action<string?> _openSettings;
    internal readonly CardWindow _updateCard;
    private readonly Button _updateInstall = new() { Content = "更新并重启", MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _updateLabel = Text("更新已下载", 13);
    private readonly ProgressBar _updateProgress = new() { Minimum = 0, Maximum = 1, Height = 3, Visibility = Visibility.Collapsed };
    private bool _updateCardVisible;
    private bool _updateActionEnabled;
    private string[] _quickCardOrder = ["weather", "traffic", "device", "media"];
    private Dictionary<string, int> _quickCardColumns = new(StringComparer.Ordinal)
        { ["weather"] = 0, ["traffic"] = 1, ["device"] = 0, ["media"] = 1 };
    private Dictionary<string, bool> _quickCardEnabled = new(StringComparer.Ordinal)
        { ["weather"] = false, ["traffic"] = true, ["device"] = true, ["media"] = true };
    private bool _onDemand;
    private FontFamily _cardFontFamily = new("Microsoft YaHei UI");
    private string? _appliedCardFont;
    private CardMicaAppearance _cardMica = CardMicaAppearance.Default;
    private CardMicaAppearance? _micaPreview;
    private readonly Dictionary<string, CardSlot> _slots;
    private readonly Dictionary<string, CardWindow> _cardsById = new(StringComparer.Ordinal);
    private IEnumerable<CardWindow> AllCards => _cardsById.Values.Append(_updateCard);
    private IEnumerable<CardWindow> EnabledCards => _quickCardOrder
        .Where(id => _quickCardEnabled.GetValueOrDefault(id))
        .Select(id => _slots.GetValueOrDefault(id)?.Window)
        .OfType<CardWindow>();
    private IEnumerable<CardWindow> DisplayedCards => _updateCardVisible ? EnabledCards.Append(_updateCard) : EnabledCards;
    internal IReadOnlyList<CardWindow> CardWindows => DisplayedCards.ToArray();
    internal event Action? UpdateActionRequested;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _seekTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly OutsideClickMonitor _outsideClicks;
    private readonly CardLyricsView _lyrics = new();
    private double _anchorX, _anchorY;
    private bool _placedAnchorSet;
    private System.Diagnostics.Stopwatch? _openTiming;
    private TimeSpan _openQueued;
    private bool _firstOpenAfterColdStart;
    private bool _prewarmCompleted;
    private readonly List<MenuFlyout> _flyouts = [];
    private readonly Dictionary<string, (string Id, double Volume, int EndpointRevision)> _pendingVolumes = [];
    private readonly Dictionary<string, int> _endpointRevisions = new() { ["input"] = 0, ["output"] = 0 };
    private readonly TextBlock _title = Text("暂无媒体", 15), _artist = Text("", 12), _sourceLabel = Text("暂无来源", 11);
    private readonly Image _cover = new() { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _position = Text("0:00", 10), _duration = Text("0:00", 10), _status = Text("", 10);
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _previous = CardIconArt.CreateButton(CardIconKind.Previous, "上一首"),
        _play = CardIconArt.CreateButton(CardIconKind.Play, "播放 / 暂停", 42),
        _next = CardIconArt.CreateButton(CardIconKind.Next, "下一首"),
        _settingsButton = CardIconArt.CreateButton(CardIconKind.Settings, "打开设置");
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
    private readonly Button _outputMute = CardIconArt.CreateButton(CardIconKind.Speaker, "输出静音"),
        _inputMute = CardIconArt.CreateButton(CardIconKind.Microphone, "输入静音");
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
    private string? _lastWeatherSnapshotKey;
    private long _lastWeatherRevision = -1;
    private bool _weatherRefreshing;
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastHiddenAt = DateTimeOffset.UtcNow;
    private bool _residentResourcesReleased;
    private readonly DispatcherTimer _idleReleaseTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _memoryNotificationsSubscribed;
    private sealed class CardSlot(string id, string title, FrameworkElement content, double height, bool autoHeight, double width)
    {
        internal string Id { get; } = id;
        internal string Title { get; } = title;
        internal FrameworkElement Content { get; } = content;
        internal double Height { get; } = height;
        internal bool AutoHeight { get; } = autoHeight;
        internal double Width { get; } = width;
        internal CardWindow? Window { get; set; }
        internal bool AllowClose { get; set; }
    }

    public CardGroup(UiConnection connection, Action<string?> openSettings)
    {
        _connection = connection;
        _openSettings = openSettings;
        _slots = new Dictionary<string, CardSlot>(StringComparer.Ordinal)
        {
            ["traffic"] = new("traffic", "流量消耗", BuildTraffic(), 284, false, CardWidth),
            ["device"] = new("device", "音频设备", BuildAudio(), 244, true, CardWidth),
            ["media"] = new("media", "媒体", BuildMedia(), 248, false, CardWidth),
            ["weather"] = new("weather", "天气", BuildWeather(), 280, true, CardWidth)
        };
        _residentResourcesReleased = false;
        var updateContent = new StackPanel { Spacing = 8 };
        _updateLabel.TextWrapping = TextWrapping.Wrap; _updateLabel.MaxLines = 3;
        updateContent.Children.Add(_updateLabel); updateContent.Children.Add(_updateProgress); updateContent.Children.Add(_updateInstall);
        AutomationProperties.SetAutomationId(_updateInstall, "card-install-update");
        _updateInstall.Click += (_, _) => { if (_updateActionEnabled) UpdateActionRequested?.Invoke(); };
        _updateCard = new CardWindow("应用更新", updateContent, 180, width: UpdateCardWidth);
        _updateCard.SetMicaAppearance(_cardMica);
        _outsideClicks = new OutsideClickMonitor(_slots["traffic"].Content.DispatcherQueue, () => _flyoutsOpen > 0);
        _output.IsEnabled = _input.IsEnabled = _outputMute.IsEnabled = _inputMute.IsEnabled = _outputVolume.IsEnabled = _inputVolume.IsEnabled = false;
        _previous.IsEnabled = _play.IsEnabled = _next.IsEnabled = _seek.IsEnabled = _source.IsEnabled = false;
        _timer.Tick += async (_, _) =>
        {
            if (_visible) await RefreshAsync(++_ticks % 8 == 0, _ticks % 25 == 0);
        };
        _volumeTimer.Tick += async (_, _) => await FlushVolumesAsync();
        _idleReleaseTimer.Tick += (_, _) =>
        {
            if (_visible) _idleReleaseTimer.Stop();
            else if (!_onDemand && SystemMemoryPressure()) _lyrics.SetMemoryLevel(true);
            else ReleaseResidentResourcesIfNeeded();
        };
        _seekTimer.Tick += async (_, _) =>
        {
            _seekTimer.Stop();
            try { await CommandAsync("mediaCommand", new { action = "seek", positionSeconds = _seek.Value }); }
            finally { _seekPending = false; }
        };
        RenderTraffic(_traffic);
        _trafficTotal.Text = "—";
        _trafficStatus.Text = "打开后读取流量历史";
        CardLifecycleLog.Write($"created cards={_cardsById.Count} ordinary; update=1");
        _status.Visibility = Visibility.Collapsed;
        _status.TextWrapping = TextWrapping.Wrap; _status.MaxLines = 3;
        _status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => {
            _status.Visibility = string.IsNullOrWhiteSpace(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (_visible) Position(_anchorX, _anchorY);
        });
        ReconcileCardWindows();
        _connection.ConnectionChanged += OnLyricsConnection;
        TrySubscribeMemoryNotifications();
    }

    private void ReconcileCardWindows()
    {
        foreach (var slot in _slots.Values)
        {
            var enabled = _quickCardEnabled.GetValueOrDefault(slot.Id);
            if (enabled && slot.Window is null) CreateCardWindow(slot);
            else if (!enabled && slot.Window is not null) DestroyCardWindow(slot);
        }
    }

    private void CreateCardWindow(CardSlot slot)
    {
        var card = new CardWindow(slot.Title, slot.Content, slot.Height, slot.AutoHeight, slot.Width);
        card.SetMicaAppearance(_micaPreview ?? _cardMica);
        slot.Window = card;
        _cardsById[slot.Id] = card;
        card.Window.AppWindow.Closing += (_, args) =>
        {
            if (_disposed || slot.AllowClose) return;
            args.Cancel = true;
            Hide("native-close");
        };
        card.Root.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) { Hide("escape"); args.Handled = true; }
        };
        if (slot.Id == "traffic")
            card.Root.ActualThemeChanged += (_, _) => RenderTraffic(_traffic);
        if (slot.Id == "weather")
            card.Root.ActualThemeChanged += (_, _) => ApplyWeatherTheme(ResolveWeatherTheme(card));
        if (slot.Id == "device")
            card.Root.Loaded += (_, _) => { if (_visible) Position(_anchorX, _anchorY); };
        if (_themePreview is { } preview) card.Root.RequestedTheme = preview;
        else if (_requestedTheme != ElementTheme.Default) card.Root.RequestedTheme = _requestedTheme;
        if (slot.Id == "weather") ApplyWeatherTheme(ResolveWeatherTheme(card));
    }

    private static ElementTheme ResolveWeatherTheme(CardWindow card) => card.Root.RequestedTheme switch
    {
        ElementTheme.Light => ElementTheme.Light,
        ElementTheme.Dark => ElementTheme.Dark,
        _ => card.Root.ActualTheme
    };

    private void DestroyCardWindow(CardSlot slot)
    {
        var card = slot.Window;
        if (card is null) return;
        slot.AllowClose = true;
        slot.Window = null;
        _cardsById.Remove(slot.Id);
        card.Close();
        slot.AllowClose = false;
        if (slot.Id == "media")
        {
            _lyrics.ReleaseRenderer();
            _sourceImageCache.Clear();
            _lastCover = null;
            _lastSourceIcon = "";
        }
        else if (slot.Id == "traffic")
        {
            foreach (var glow in _glows.Values) { glow.Shadow.Mask?.Dispose(); glow.Visual.Dispose(); glow.Shadow.Dispose(); }
            _glows.Clear();
        }
    }

    // Create and render every enabled card while hidden so the first click does
    // not pay for window creation, XAML realization, or the first composition
    // pass. No weather, traffic, audio, or lyric work is started here.
    internal async Task PrewarmAsync()
    {
        if (_disposed || _visible) return;
        var timing = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ReconcileCardWindows();
            foreach (var slot in _slots.Values) slot.Window?.MeasureHeight();
            var createdMs = timing.Elapsed.TotalMilliseconds;
            var prewarmed = 0;
            // Each card waits for its own composition frame, so the UI thread
            // yields between cards and an early click never waits for the set.
            // The set is snapshotted: a click during prewarm may reconcile windows.
            foreach (var card in AllCards.ToArray())
            {
                await card.PrewarmAsync();
                if (card.Prewarmed) prewarmed++;
                if (IsWindowVisible(card.Handle)) throw new InvalidOperationException("Prewarm left a card visible.");
            }
            _prewarmCompleted = true;
            CardTimingLog.Write($"prewarm-finished cards={prewarmed} createdMs={createdMs:0.0} totalMs={timing.Elapsed.TotalMilliseconds:0.0}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Prewarm is an optimization only; a failure must fall back to the
            // original lazy creation path without touching user-visible state.
            CardLifecycleLog.Write($"prewarm-failed hr=0x{exception.HResult:X8}");
        }
    }

    public void ToggleAtPhysicalPoint(double x, double y, nint entryWindow = 0)
    {
        if (_disposed) return;
        if (_visible) { Hide("toggle"); return; }
        var cards = DisplayedCards.ToArray();
        if (cards.Length == 0) return;
        var timing = _openTiming ??= System.Diagnostics.Stopwatch.StartNew();
        _firstOpenAfterColdStart = !_prewarmCompleted;
        Position(x, y);
        var anchorDpi = GetDpiForWindow((cards.FirstOrDefault() ?? _updateCard).Handle);
        CardTimingLog.Write($"positioned cards={cards.Length} elapsedMs={timing.Elapsed.TotalMilliseconds:0.0} " +
            $"dpi={anchorDpi} theme={_requestedTheme} prewarmed={AllCards.Count(card => card.Prewarmed)}");
        _visible = true;
        _generation++;
        _ticks = 0;
        if (_residentResourcesReleased) RestoreResidentResources();
        _visibleLifetime = new CancellationTokenSource();
        if (_quickCardEnabled.GetValueOrDefault("media")) _lyrics.Activate();
        var openElapsed = timing.Elapsed;
        var queuedWait = _openQueued;
        _openTiming = null;
        _openQueued = TimeSpan.Zero;
        foreach (var card in cards) card.Show();
        _ = ObserveFirstFrameAsync(cards[0], timing, openElapsed, queuedWait);
        CardTimingLog.Write($"shown cards={cards.Length} elapsedMs={timing.Elapsed.TotalMilliseconds:0.0} firstAfterColdStart={_firstOpenAfterColdStart}");
        var activationStarted = timing.Elapsed;
        (EnabledCards.LastOrDefault() ?? _updateCard).Window.Activate();
        CardTimingLog.Write($"activated elapsedMs={timing.Elapsed.TotalMilliseconds:0.0} operationMs={(timing.Elapsed - activationStarted).TotalMilliseconds:0.0}");
        var generation = _generation;
        _outsideClicks.Start(entryWindow, click =>
        {
            if (_disposed || !_visible || generation != _generation || !ShouldDismissClick(click)) return;
            CardLifecycleLog.Write($"outside-click button={click.MouseButton} point={click.X},{click.Y} target=0x{click.Target:X} root=0x{click.Root:X} owner=0x{click.RootOwner:X} pid={click.ProcessId} popup={click.IsPopup} flyoutAtClick={click.FlyoutWasOpen}");
            Hide("outside-click");
        });
        CardLifecycleLog.Write($"shown generation={_generation} entry=0x{entryWindow:X}");
        _timer.Start();
        _idleReleaseTimer.Stop();
        _ = RefreshAsync(_quickCardEnabled.GetValueOrDefault("device"), _quickCardEnabled.GetValueOrDefault("traffic"));
        if (_quickCardEnabled.GetValueOrDefault("media")) _ = SubscribeLyricsAsync(_generation, true);
        if (_quickCardEnabled.GetValueOrDefault("weather")) _ = LoadWeatherAsync();
    }

    // Waits for the card's fade to start on a real composition frame and writes
    // the open timing record.
    private static async Task ObserveFirstFrameAsync(CardWindow card, System.Diagnostics.Stopwatch timing, TimeSpan openElapsed, TimeSpan queuedWait)
    {
        try
        {
            await card.VisibleFrame.WaitAsync(TimeSpan.FromMilliseconds(600));
            CardTimingLog.Write($"first-frame openMs={openElapsed.TotalMilliseconds:0.0} " +
                $"firstFrameMs={timing.Elapsed.TotalMilliseconds:0.0} totalMs={(queuedWait + timing.Elapsed).TotalMilliseconds:0.0}");
        }
        catch (TimeoutException)
        { CardTimingLog.Write($"first-frame-timeout openMs={openElapsed.TotalMilliseconds:0.0} totalMs={(queuedWait + timing.Elapsed).TotalMilliseconds:0.0}"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { CardLifecycleLog.Write($"timing-observe-failed hr=0x{exception.HResult:X8}"); }
    }

    // Called by the UI dispatcher before this toggle runs so the queued time
    // between the backend gesture and the UI thread is part of the record.
    internal void BeginOpenTiming(long gestureId, TimeSpan queued)
    {
        _openTiming = System.Diagnostics.Stopwatch.StartNew();
        _openQueued = queued;
        if (queued.TotalMilliseconds >= 30)
            CardTimingLog.Write($"queued gesture={gestureId} waitMs={queued.TotalMilliseconds:0.0}");
    }

    public void Hide() => Hide("explicit");

    private void Hide(string reason)
    {
        if (!_visible) return;
        CardLifecycleLog.Write($"hide reason={reason} generation={_generation} flyouts={_flyoutsOpen}");
        _visible = false;
        _generation++;
        _lyrics.HideLyrics();
        if (_quickCardEnabled.GetValueOrDefault("media")) _ = SubscribeLyricsAsync(_generation, false);
        _timer.Stop();
        _outsideClicks.Stop();
        _seekTimer.Stop();
        _seekPending = false;
        _visibleLifetime?.Cancel();
        _visibleLifetime?.Dispose();
        _visibleLifetime = null;
        _lastHiddenAt = DateTimeOffset.UtcNow;
        _idleReleaseTimer.Start();
        ReleaseResidentResourcesIfNeeded();
        foreach (var flyout in _flyouts) flyout.Hide();
        foreach (var card in AllCards) HideCard(card);
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
        _settingsButton.VerticalAlignment = VerticalAlignment.Top;
        _settingsButton.Click += (_, _) => { Hide("settings"); _openSettings("quickCards"); };
        Add(summary, _settingsButton, 2);
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

    internal void SetMicaPreview(CardMicaAppearance? preview, CardMicaAppearance? committed = null)
    {
        if (_disposed) return;
        if (committed is { } saved) _cardMica = saved;
        _micaPreview = preview;
        ApplyMicaAppearance();
    }
    internal bool MicaVivid => (_micaPreview ?? _cardMica).Vivid;
    private void ApplyMicaAppearance()
    {
        var effective = _micaPreview ?? _cardMica;
        foreach (var card in AllCards) card.SetMicaAppearance(effective);
    }
    internal void SetUpdateState(UpdateUiState state)
    {
        if (_disposed) return;
        var ready = state.CanInstall && state.Phase == "ready";
        var downloadable = state.CanDownload && state.Phase == "available";
        _updateActionEnabled = ready || downloadable;
        // The update card is an actionable notice, not just a post-download banner:
        // it appears as soon as an update is available so the user can find it.
        var show = state.Phase is "available" or "downloading" or "verifying" or "ready";
        _updateLabel.Text = ready
            ? "版本 " + state.LatestVersion + "\n已下载 · 校验通过"
            : state.Phase switch
            {
                "available" => "版本 " + state.LatestVersion + "\n发现新更新",
                "downloading" => "版本 " + state.LatestVersion + "\n正在下载…",
                "verifying" => "版本 " + state.LatestVersion + "\n正在校验…",
                _ => "应用更新\n下载与校验在关于页进行"
            };
        ToolTipService.SetToolTip(_updateLabel, _updateLabel.Text);
        _updateInstall.Content = state.Phase switch
        {
            "downloading" => "正在下载…",
            "verifying" => "正在校验…",
            _ => ready ? "更新并重启" : downloadable ? "下载并安装" : "更新并重启"
        };
        _updateInstall.IsEnabled = _updateActionEnabled;
        _updateProgress.Visibility = state.TotalBytes > 0 && state.Phase is "downloading" or "verifying" ? Visibility.Visible : Visibility.Collapsed;
        _updateProgress.Value = state.TotalBytes > 0 ? Math.Clamp((double)state.BytesDownloaded / state.TotalBytes, 0, 1) : 0;
        if (_updateCardVisible == show) return;
        _updateCardVisible = show;
        if (!_visible) return;
        if (!show) HideCard(_updateCard);
        Position(_anchorX, _anchorY);
        if (show) _updateCard.Show();
    }
    internal void ApplyBackendTheme(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("toolWindowTheme", out var value) || value.ValueKind == JsonValueKind.Null) return;
        ApplyTheme(_themePreview ?? UiThemeStyles.ParseTheme(value.ToString()));
    }
    internal void ApplyQuickCards(JsonElement value, bool reposition = true)
    {
        if (_disposed || value.ValueKind != JsonValueKind.Object) return;
        var order = ReadStringArray(value, "order");
        if (order is { Length: > 0 }) {
            var normalized = order.Where(id => _slots.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var id in new[] { "weather", "traffic", "device", "media" })
                if (!normalized.Contains(id, StringComparer.Ordinal)) normalized.Add(id);
            if (value.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Object)
                foreach (var item in columns.EnumerateObject())
                    if (_slots.ContainsKey(item.Name) && item.Value.ValueKind == JsonValueKind.Number &&
                        item.Value.TryGetInt32(out var column) && (column == 0 || column == 1))
                        _quickCardColumns[item.Name] = column;
            foreach (var key in _quickCardColumns.Keys.Where(key => !_slots.ContainsKey(key)).ToArray())
                _quickCardColumns.Remove(key);
            for (var index = 0; index < normalized.Count; index++)
                if (!_quickCardColumns.TryGetValue(normalized[index], out var assigned) || (assigned != 0 && assigned != 1))
                    _quickCardColumns[normalized[index]] = index % 2;
            // Cards choose their column freely; the saved order is the left
            // column top-to-bottom followed by the right column.
            _quickCardOrder = normalized
                .Where(id => _quickCardColumns[id] == 0)
                .Concat(normalized.Where(id => _quickCardColumns[id] == 1))
                .ToArray();
        }
        if (value.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.Object) {
            foreach (var id in _slots.Keys)
                _quickCardEnabled[id] = !enabled.TryGetProperty(id, out var flag) || flag.ValueKind != JsonValueKind.False;
        }
        if (value.TryGetProperty("font", out var font) && font.ValueKind == JsonValueKind.String)
        {
            var family = font.GetString();
            if (!string.IsNullOrWhiteSpace(family) && family!.Length < 32 && family != _appliedCardFont)
            {
                _cardFontFamily = new FontFamily(family);
                ApplyCardFont(_slots.Values.Select(slot => slot.Content).Append(_updateCard.Content));
                _appliedCardFont = family;
                FitWeatherTemperature();
                RefreshWeatherLayout();
            }
        }
        _onDemand = value.TryGetProperty("resourceMode", out var mode) && string.Equals(mode.GetString(), "OnDemand", StringComparison.OrdinalIgnoreCase);
        if (value.TryGetProperty("mica", out var mica))
        {
            _cardMica = new CardMicaAppearance(
                !mica.TryGetProperty("vivid", out var vivid) || vivid.ValueKind != JsonValueKind.False,
                CardMicaTuning.FromJson(mica));
            ApplyMicaAppearance();
        }
        ReconcileCardWindows();
        if (reposition)
        {
            if (_visible) { Position(_anchorX, _anchorY); RenderWeatherModules(); }
            else if (_placedAnchorSet) Place(_anchorX, _anchorY);
        }
    }

    // Media owns the quick-card configuration. This shape maps that settings
    // payload onto ApplyQuickCards so startup prewarm matches the real panel.
    internal void ApplyQuickCardsFromSettings(JsonElement settings)
    {
        if (_disposed || settings.ValueKind != JsonValueKind.Object) return;
        var payload = new System.Text.Json.Nodes.JsonObject();
        if (CopyArray(settings, "quickCardOrder") is { } order) payload["order"] = order;
        if (CopyValue(settings, "quickCardColumns") is { } columns) payload["columns"] = columns;
        if (CopyValue(settings, "quickCardEnabled") is { } enabled) payload["enabled"] = enabled;
        if (settings.TryGetProperty("quickCardResourceMode", out var mode) && mode.ValueKind == JsonValueKind.String)
            payload["resourceMode"] = mode.GetString();
        if (settings.TryGetProperty("cardFontFamily", out var font) && font.ValueKind == JsonValueKind.String)
            payload["font"] = font.GetString();
        if (settings.TryGetProperty("cardMicaTintOpacity", out var tint) &&
            tint.ValueKind == JsonValueKind.Number && tint.TryGetDouble(out var tintOpacity) &&
            settings.TryGetProperty("cardMicaLuminosityOpacity", out var luminosity) &&
            luminosity.ValueKind == JsonValueKind.Number && luminosity.TryGetDouble(out var luminosityOpacity))
        {
            var mica = new System.Text.Json.Nodes.JsonObject
            {
                ["tintOpacity"] = tintOpacity,
                ["luminosityOpacity"] = luminosityOpacity
            };
            if (settings.TryGetProperty("cardMicaVivid", out var vividValue) && vividValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                mica["vivid"] = vividValue.GetBoolean();
            if (settings.TryGetProperty("cardMicaDarkTintOpacity", out var darkTint) &&
                darkTint.ValueKind == JsonValueKind.Number && darkTint.TryGetDouble(out var darkTintOpacity))
                mica["darkTintOpacity"] = darkTintOpacity;
            if (settings.TryGetProperty("cardMicaDarkLuminosityOpacity", out var darkLuminosity) &&
                darkLuminosity.ValueKind == JsonValueKind.Number && darkLuminosity.TryGetDouble(out var darkLuminosityOpacity))
                mica["darkLuminosityOpacity"] = darkLuminosityOpacity;
            payload["mica"] = mica;
        }
        ApplyQuickCards(JsonSerializer.SerializeToElement(payload));
    }

    private static System.Text.Json.Nodes.JsonNode? CopyArray(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())
            : null;

    private static System.Text.Json.Nodes.JsonNode? CopyValue(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())
            : null;

    private void ApplyCardFont(IEnumerable<FrameworkElement> roots)
    {
        foreach (var root in roots) ApplyCardFont(root);
    }

    private void ApplyCardFont(DependencyObject root)
    {
        if (_disposed) return;
        if (root is TextBlock text) text.FontFamily = _cardFontFamily;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            ApplyCardFont(VisualTreeHelper.GetChild(root, index));
    }
    internal void SetQuickCardEnabled(string id, bool enabled)
    {
        if (_quickCardEnabled.ContainsKey(id))
        {
            _quickCardEnabled[id] = enabled;
            ReconcileCardWindows();
        }
    }
    private void RefreshWeatherLayout()
    {
        if (_disposed) return;
        if (_cardsById.TryGetValue("weather", out var card)) card.MeasureHeight();
        if (_visible) Position(_anchorX, _anchorY);
    }
    private void ApplyTheme(ElementTheme theme)
    {
        if (_disposed || _requestedTheme == theme) return;
        _requestedTheme = theme;
        foreach (var card in AllCards)
        {
            card.Root.RequestedTheme = theme;
            if (ReferenceEquals(card, _cardsById.GetValueOrDefault("weather")))
                ApplyWeatherTheme(ResolveWeatherTheme(card));
        }
        foreach (var flyout in _flyouts) ThemeFlyout(flyout);
        foreach (var card in AllCards)
            if (card.Root.XamlRoot is { } root)
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                    if (popup.Child is FrameworkElement child) child.RequestedTheme = theme;
    }
    private void ThemeFlyout(MenuFlyout flyout)
    {
        flyout.MenuFlyoutPresenterStyle = UiThemeStyles.CardMenu(_requestedTheme);
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
        if (!audio && !traffic && !_quickCardEnabled.GetValueOrDefault("media")) return;
        // Audio must not wait for media metadata or native traffic-history requests.
        if (audio) _ = RefreshAudioAsync(_visibleLifetime.Token);
        if (_refreshing) { _refreshAgain = true; _audioAgain |= audio; _trafficAgain |= traffic; return; }
        _refreshing = true;
        var generation = _generation;
        var token = _visibleLifetime.Token;
        try
        {
            var stateTask = _connection.RequestAsync("getState", cancellationToken: token);
            var trafficTask = traffic ? _connection.RequestAsync("getTrafficHistory", cancellationToken: token) : Task.FromResult(default(JsonElement));
            await Task.WhenAll(stateTask, trafficTask);
            var state = await stateTask;
            if (!_visible || generation != _generation) return;
            if (_quickCardEnabled.GetValueOrDefault("media")) RenderMedia(state);
            if (traffic) {
                var history = await trafficTask;
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
            CardIconArt.SetKind(_play, Bool(state, "isPlaying") ? CardIconKind.Pause : CardIconKind.Play);
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
        if (_disposed || _audioRefreshing || _busyAudioFlows.Count > 0 || !_quickCardEnabled.GetValueOrDefault("device")) return;
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
        CardIconArt.SetKind(mute, current?.IsMuted == true
            ? input ? CardIconKind.MicrophoneMuted : CardIconKind.SpeakerMuted
            : input ? CardIconKind.Microphone : CardIconKind.Speaker);
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
        foreach (var card in DisplayedCards)
        {
            if (IsWindowVisible(card.Handle) && GetWindowRect(card.Handle, out var bounds) &&
                click.X >= bounds.Left && click.X < bounds.Right && click.Y >= bounds.Top && click.Y < bounds.Bottom)
                return false;
        }
        // The click snapshot survives MenuFlyout.Closed and native popup teardown
        // before the queued classifier runs. Same-process unrelated windows do not qualify.
        if (click.ProcessId == (uint)Environment.ProcessId &&
            (DisplayedCards.Any(card => card.Handle == click.RootOwner) || click.FlyoutWasOpen && click.IsPopup)) return false;
        return true;
    }

    internal void Position(double x, double y)
    {
        _anchorX = x; _anchorY = y;
        _placedAnchorSet = true;
        Place(x, y);
    }

    private void Place(double x, double y)
    {
        // Cards choose their column freely; each column keeps its saved order.
        var left = new List<CardWindow>();
        var right = new List<CardWindow>();
        foreach (var id in _quickCardOrder)
        {
            if (!_quickCardEnabled.GetValueOrDefault(id)) continue;
            var card = _slots.GetValueOrDefault(id)?.Window;
            if (card is null) continue;
            (_quickCardColumns.GetValueOrDefault(id) == 0 ? left : right).Add(card);
        }
        var baseCards = left.Concat(right).ToArray();
        foreach (var card in baseCards.Append(_updateCard)) card.MeasureHeight();
        var monitor = MonitorFromPoint(new NativePoint { X = (int)x, Y = (int)y }, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        foreach (var card in baseCards.Append(_updateCard))
            if (MonitorFromWindow(card.Handle, 2) != monitor) card.Window.AppWindow.Move(new PointInt32(info.Work.Left + 10, info.Work.Top + 10));
        var dpi = GetDpiForWindow((baseCards.FirstOrDefault() ?? _updateCard).Handle);
        var scale = dpi > 0 ? dpi / 96d : 1;
        var gap = (int)Math.Round(12 * scale);
        var baseWidth = Math.Min((int)Math.Ceiling(CardWidth * scale), Math.Max(1, info.Work.Right - info.Work.Left - (int)(20 * scale)));
        var updateWidth = Math.Min((int)Math.Ceiling(UpdateCardWidth * scale), Math.Max(1, info.Work.Right - info.Work.Left - (int)(20 * scale)));

        var columns = new List<List<CardWindow>>();
        if (left.Count > 0) columns.Add(left);
        if (right.Count > 0) columns.Add(right);

        double ColumnHeight(IEnumerable<CardWindow> column) => column.Sum(card => card.Height) + gap * (column.Count() - 1);
        var desiredBaseHeight = columns.Count > 0 ? columns.Max(ColumnHeight) : _updateCardVisible ? _updateCard.Height : 0;
        var baseColumns = columns.Count;
        var updateColumns = _updateCardVisible ? 1 : 0;
        var totalColumns = baseColumns + updateColumns;
        var totalWidth = baseColumns * baseWidth + updateColumns * updateWidth + gap * Math.Max(0, totalColumns - 1);
        var fit = Math.Min(1, Math.Min(
            (info.Work.Right - info.Work.Left - 20 * scale) / (double)Math.Max(1, totalWidth),
            (info.Work.Bottom - info.Work.Top - 20 * scale) / Math.Max(1, desiredBaseHeight * scale)));
        var scaledBaseWidth = Math.Max(1, (int)Math.Floor(baseWidth * fit));
        var scaledUpdateWidth = Math.Max(1, (int)Math.Floor(updateWidth * fit));
        var heights = new Dictionary<CardWindow, int>();
        foreach (var card in baseCards)
            heights[card] = Math.Max(1, (int)Math.Floor(card.Height * scale * fit));
        var updateHeight = Math.Max(1, (int)Math.Floor(_updateCard.Height * scale * fit));

        double ColumnPixelHeight(IEnumerable<CardWindow> column) => column.Sum(card => heights[card]) + gap * (column.Count() - 1);
        var baseBlockHeight = (int)Math.Ceiling(columns.Count > 0 ? columns.Max(ColumnPixelHeight) : 0);
        var totalBlockWidth = scaledBaseWidth * baseColumns + scaledUpdateWidth * updateColumns + gap * Math.Max(0, totalColumns - 1);
        var groupLeft = Math.Clamp((int)Math.Round(x - totalBlockWidth / 2d), info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - totalBlockWidth));
        var groupTop = Math.Clamp((int)Math.Round(y - baseBlockHeight - 10 * scale), info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - baseBlockHeight));

        var columnLeft = groupLeft;
        foreach (var column in columns)
        {
            var columnPixelHeight = ColumnPixelHeight(column);
            var columnTop = groupTop + baseBlockHeight - (int)Math.Round(columnPixelHeight);
            foreach (var card in column)
            {
                card.Window.AppWindow.MoveAndResize(new RectInt32(columnLeft, columnTop, scaledBaseWidth, heights[card]));
                columnTop += heights[card] + gap;
            }
            columnLeft += scaledBaseWidth + gap;
        }
        if (_updateCardVisible)
        {
            var updateLeft = groupLeft + scaledBaseWidth * baseColumns + gap * baseColumns;
            var updateTop = Math.Max(info.Work.Top, groupTop + baseBlockHeight - updateHeight);
            _updateCard.Window.AppWindow.MoveAndResize(new RectInt32(updateLeft, updateTop, scaledUpdateWidth, updateHeight));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CardLifecycleLog.Write($"dispose visible={_visible}");
        _disposed = true; _visible = false; _generation++;
        _visibleLifetime?.Cancel(); _visibleLifetime?.Dispose(); _visibleLifetime = null;
        _volumeTimer.Stop(); _seekTimer.Stop(); _timer.Stop(); _idleReleaseTimer.Stop(); _weatherAlertTimer.Stop(); _outsideClicks.Dispose();
        _connection.ConnectionChanged -= OnLyricsConnection;
        UnsubscribeMemoryNotifications();
        _lyrics.Dispose();
        foreach (var flyout in _flyouts) flyout.Hide();
        _pendingVolumes.Clear();
        foreach (var card in AllCards) card.Close();
        foreach (var glow in _glows.Values) { glow.Shadow.Mask?.Dispose(); glow.Visual.Dispose(); glow.Shadow.Dispose(); }
        _glows.Clear();
    }

    private void OnLyricsConnection(bool connected)
    {
        _slots["media"].Content.DispatcherQueue.TryEnqueue(() => {
            if (_disposed || !_visible) return;
            if (!_quickCardEnabled.GetValueOrDefault("media")) return;
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
    private void ReleaseResidentResourcesIfNeeded()
    {
        if (!_onDemand || _visible || _residentResourcesReleased) return;
        if (DateTimeOffset.UtcNow - _lastHiddenAt < TimeSpan.FromMinutes(10)) return;
        _residentResourcesReleased = true;
        _lyrics.ReleaseRenderer();
        _cover.Source = null;
        _sourceImage.Source = null;
        _sourceImageCache.Clear();
        _lastCover = null;
        _lastSourceIcon = "";
        CardLifecycleLog.Write("resident-resources-released reason=on-demand-idle");
    }
    private void RestoreResidentResources()
    {
        if (!_residentResourcesReleased) return;
        _residentResourcesReleased = false;
        _lyrics.SetMemoryLevel(false);
        CardLifecycleLog.Write("resident-resources-restored");
    }
    private static bool SystemMemoryPressure()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) && status.MemoryLoad >= 90;
    }
    private void OnMemoryUsageChanged(object? sender, object args)
    {
        if (_disposed) return;
        try
        {
            var low = Windows.System.MemoryManager.AppMemoryUsageLevel >= Windows.System.AppMemoryUsageLevel.High;
            _lyrics.SetMemoryLevel(low && !_visible);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException) { }
    }
    private void TrySubscribeMemoryNotifications()
    {
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnMemoryUsageChanged;
            Windows.System.MemoryManager.AppMemoryUsageDecreased += OnMemoryUsageChanged;
            _memoryNotificationsSubscribed = true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            CardLifecycleLog.Write($"memory-notifications-unavailable hr=0x{error.HResult:X8}");
        }
    }
    private void UnsubscribeMemoryNotifications()
    {
        if (!_memoryNotificationsSubscribed) return;
        _memoryNotificationsSubscribed = false;
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnMemoryUsageChanged;
            Windows.System.MemoryManager.AppMemoryUsageDecreased -= OnMemoryUsageChanged;
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException) { }
    }
    private sealed record AudioChoice(string Id, string Name, bool IsDefault, bool IsCommunications, double Volume, bool IsMuted, bool CanControl);
    private sealed record SourceChoice(string Id, string Label, string IconDataUrl);
    private static AudioChoice[] AudioChoices(JsonElement value, string name) => Array(value, name).Select(item => new AudioChoice(
        String(item, "id"), String(item, "name"), Bool(item, "isDefault"), Bool(item, "isCommunications"), Number(item, "volume"), Bool(item, "isMuted"),
        !item.TryGetProperty("canControl", out var control) || control.ValueKind != JsonValueKind.False)).ToArray();
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
    private static string[]? ReadStringArray(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
        ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
        : null;
    private static string String(JsonElement value, string name, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
    private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static double Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 359999)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static TextBlock Text(string value, double size = 12) => UiThemeStyles.Apply(new TextBlock { Text = value, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 }, "CardTextPrimary");
    private static void Secondary(TextBlock text) => UiThemeStyles.Apply(text, "CardTextSecondary");
    private static Grid Columns(params GridLength[] widths)
    {
        var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }); return grid;
    }
    private static void Add(Grid grid, FrameworkElement element, int column, int row = 0) { Grid.SetColumn(element, column); Grid.SetRow(element, row); grid.Children.Add(element); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
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
    internal static void Write(string message) => UiDiagnosticLog.Write("card-lifecycle.log", message);
}

// Phase timing for card opens. Bounded like the lifecycle log and never given
// titles, lyrics, paths, or credentials; used to verify the open budget.
internal static class CardTimingLog
{
    internal static void Write(string message) => UiDiagnosticLog.Write("card-timing.log", message);
}
