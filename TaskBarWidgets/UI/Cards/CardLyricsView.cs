using System.Numerics;
using System.Text.Json;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace TaskBarWidgets.UI.Cards;

// Native card lyrics renderer. Four reusable line elements cover the current,
// next/translation, and incoming roll rows. Frame updates only replace text,
// brushes, clips, and composition animations; the visual tree is never rebuilt.
internal sealed class CardLyricsView : Grid, IDisposable
{
    private const int LineCount = 4;
    private static readonly TimeSpan SingleRollDuration = TimeSpan.FromMilliseconds(560);
    private static readonly TimeSpan PairRollDuration = TimeSpan.FromMilliseconds(760);
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly DispatcherTimer _animationTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly List<CardLyricsTextLine> _lines = [];
    private readonly SolidColorBrush _primaryBrush = new(Microsoft.UI.Colors.White);
    private readonly SolidColorBrush _secondaryBrush = new(Microsoft.UI.Colors.White);
    private readonly SolidColorBrush _translationBrush = new(Microsoft.UI.Colors.White);
    private readonly SolidColorBrush _scanBrush = new(Microsoft.UI.Colors.White);
    private readonly SolidColorBrush _shadowBrush = new(Color.FromArgb(92, 0, 0, 0));
    private readonly RectangleGeometry _viewportClip = new();
    private Grid? _viewport;
    private Canvas? _track;
    private Visual? _trackVisual;
    private CardLyricsFrame? _lastFrame;
    private CardLyricsFrame? _appliedFrame;
    private CardLyricsFrame? _pendingFrame;
    private CardLyricsFrame _lastStyleFrame = DefaultFrame;
    private FontFamily _fontFamily = new("Segoe UI Variable Text");
    private string _fontFamilyName = "Segoe UI Variable Text";
    private string _cardFallbackFontFamily = "";
    private FontWeight _fontWeight = new() { Weight = 500 };
    private FontStyle _fontStyle = FontStyle.Normal;
    private TextDecorations _textDecorations = TextDecorations.None;
    private double _requestedFontSize = 13;
    private double _layoutScale = 1;
    private double _currentFontSize = 13;
    private double _secondaryFontSize = 12;
    private double _rowHeight = 14;
    private double _rowGap = 1;
    private double _rowPitch = 15;
    private string _alignment = "Left";
    private bool _singleLine;
    private bool _textShadow;
    private double _layoutWidth;
    private double _layoutHeight;
    private int _primaryIndex;
    private int _secondaryIndex = 1;
    private int _layoutBuilds;
    private long _revision = -1;
    private bool _active;
    private bool _disposed;
    private bool _hasAppliedFrame;
    private bool _transitionActive;
    private int _transitionRows = 1;
    private double _transitionDistance;
    private DateTimeOffset _transitionEndsAt;
    private CardLyricsFrame? _transitionTarget;
    private int _transitionPrimaryIndex;
    private int _transitionSecondaryIndex;
    private double? _scanTarget;
    private double? _displayedScan;
    private string _scanTrackId = "";
    private int _scanLineIndex = -1;
    private static readonly CardLyricsFrame DefaultFrame = new(
        0,
        "",
        false,
        false,
        new CardLyricsContent("", "", 0, -1, "", false, false, "", "", false, false, null, "message"),
        new CardLyricsStyle("Segoe UI Variable Text", 100, 13, 500, "Left", false, 2, 1, 2),
        new CardLyricsNativeAppearance(null, null, null, null, null, null));

    internal CardLyricsView()
    {
        IsHitTestVisible = false;
        // The card itself owns the visible material. The lyrics host must remain
        // completely transparent so Mica and the card wash show through.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Margin = new Thickness(0, 6, 0, 6);
        ActualThemeChanged += OnActualThemeChanged;
        try { _accessibility.HighContrastChanged += OnHighContrastChanged; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            CardLifecycleLog.Write($"card-lyrics accessibility-unavailable hr=0x{error.HResult:X8}");
        }
        _animationTimer.Tick += OnAnimationTimerTick;
        UpdateBrushColors();
    }

    internal bool? ProbeHighContrast { get; set; }
    internal bool? ProbeReducedMotion { get; set; }
    internal CardLyricsFrame? CurrentFrame => _lastFrame;
    internal long Revision => _revision;
    internal bool ContentVisible => _viewport?.Visibility == Visibility.Visible;
    internal bool TransitionActive => _transitionActive;
    internal bool AnimationTimerActive => _animationTimer.IsEnabled;
    internal bool ScanSmoothing => _displayedScan.HasValue && _scanTarget.HasValue &&
        Math.Abs(_displayedScan.Value - _scanTarget.Value) > .002;
    internal double? DisplayedWordScan => _displayedScan;
    internal double? TargetWordScan => _scanTarget;
    internal int LayoutBuilds => _layoutBuilds;
    internal IReadOnlyList<CardLyricsTextLine> Lines => _lines;
    internal CardLyricsTextLine? PrimaryLine => _viewport is null ? null : _lines[_primaryIndex];
    internal CardLyricsTextLine? SecondaryLine => _viewport is null ? null : _lines[_secondaryIndex];
    internal double RowHeight => _rowHeight;
    internal double RowPitch => _rowPitch;
    internal double TransitionDistance => _transitionDistance;
    internal bool SingleLine => _singleLine;
    internal bool TextShadowEnabled => _textShadow;
    internal ElementTheme EffectiveTheme => ActualTheme;
    internal Color PrimaryColor => _primaryBrush.Color;
    internal Color SecondaryColor => _secondaryBrush.Color;
    internal Color TranslationColor => _translationBrush.Color;
    internal Color ScanColor => _scanBrush.Color;

    private bool HighContrast => ProbeHighContrast ?? SystemHighContrast();
    private bool ReducedMotion => ProbeReducedMotion ?? !SystemAnimationsEnabled();

    private bool SystemHighContrast()
    {
        try { return _accessibility.HighContrast; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private bool SystemAnimationsEnabled()
    {
        try { return _uiSettings.AnimationsEnabled; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return true;
        }
    }

    internal void Activate()
    {
        if (_disposed) return;
        EnsureLayout();
        _active = true;
        _revision = -1;
        _hasAppliedFrame = false;
        _lastFrame = null;
        _appliedFrame = null;
        _pendingFrame = null;
        CancelTransition();
        ClearScanTarget();
        _animationTimer.Stop();
        if (_viewport is not null) _viewport.Visibility = Visibility.Collapsed;
    }

    internal void HideLyrics()
    {
        _active = false;
        _pendingFrame = null;
        CancelTransition();
        ClearScanTarget();
        _animationTimer.Stop();
        if (_viewport is not null) _viewport.Visibility = Visibility.Collapsed;
    }

    internal void ReleaseResources()
    {
        HideLyrics();
        _pendingFrame = null;
        _transitionRows = 1;
        _transitionPrimaryIndex = 0;
        _transitionSecondaryIndex = 1;
        _primaryIndex = 0;
        _secondaryIndex = 1;
        _lastFrame = null;
        _appliedFrame = null;
        _revision = -1;
        _hasAppliedFrame = false;
        foreach (var line in _lines) line.Reset();
        _lines.Clear();
        if (_viewport is not null)
        {
            _viewport.SizeChanged -= OnViewportSizeChanged;
            _viewport.Loaded -= OnViewportLoaded;
            _viewport.Children.Clear();
            Children.Remove(_viewport);
        }
        _viewport = null;
        _track = null;
        _trackVisual = null;
        _layoutWidth = 0;
        _layoutHeight = 0;
    }

    // CardGroup validates backend session/visibility/generation before forwarding.
    internal void Receive(JsonElement frame)
    {
        if (!_active || _disposed || !CardLyricsFrameParser.TryParse(frame, out var parsed))
        {
            if (_active && !_disposed) CardLifecycleLog.Write("card-lyrics rejected-frame");
            return;
        }
        Receive(parsed);
    }

    internal void Receive(CardLyricsFrame frame)
    {
        if (!_active || _disposed || frame.Revision < _revision) return;
        _revision = frame.Revision;

        if (_transitionActive)
        {
            if (CanContinueTransition(frame))
            {
                _lastFrame = frame;
                _pendingFrame = frame;
                UpdatePromotingScan(frame);
                return;
            }
            CancelTransition();
        }

        var applied = _appliedFrame;
        if (!frame.IsDisplayable)
        {
            _lastFrame = frame;
            ApplyUnavailable(frame);
            return;
        }

        var immediate = !_hasAppliedFrame ||
            frame.Lyrics.Scene != "lyrics" ||
            applied is null ||
            !applied.IsDisplayable ||
            applied.TrackId != frame.TrackId ||
            applied.Lyrics.TrackId != frame.Lyrics.TrackId ||
            applied.Lyrics.TranslationMode != frame.Lyrics.TranslationMode ||
            applied.IsPlaying != frame.IsPlaying ||
            IsSeekOrJump(applied, frame) ||
            !frame.Lyrics.AnimateTransition ||
            ReducedMotion;

        if (!immediate && applied is not null && TryStartRoll(frame, applied))
        {
            _lastFrame = frame;
            return;
        }

        _lastFrame = frame;
        ApplyImmediate(frame, animateScan: CanSmoothScan(applied, frame));
    }

    internal void RefreshAppearance()
    {
        if (_disposed || !_active) return;
        if (_transitionActive)
        {
            CancelTransition();
            if (_lastFrame is { IsDisplayable: true } latest)
            {
                ApplyImmediate(latest, animateScan: false);
                return;
            }
        }
        if (_lastFrame is { IsDisplayable: true } frame)
            ApplyImmediate(frame, animateScan: false);
    }

    // Card-level typography is the fallback when a source supplies no native
    // appearance fields. Keep it on the view rather than mutating realized
    // TextBlocks, so the renderer preserves its single style owner.
    internal void SetCardFallbackFont(FontFamily family)
    {
        if (_disposed) return;
        var name = family.Source;
        if (!string.IsNullOrWhiteSpace(name)) _cardFallbackFontFamily = name;
        // ApplyStyle consumes this fallback on the next complete snapshot. Never
        // rewrite realized text from a settings change while a frame owns style.
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_disposed) return;
        UpdateBrushColors();
        RefreshAppearance();
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args)
    {
        if (_disposed) return;
        UpdateBrushColors();
        RefreshAppearance();
    }

    private void EnsureLayout()
    {
        if (_viewport is not null) return;
        _viewport = new Grid
        {
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Clip = _viewportClip,
            Visibility = Visibility.Collapsed
        };
        _viewport.SizeChanged += OnViewportSizeChanged;
        _viewport.Loaded += OnViewportLoaded;
        _track = new Canvas
        {
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _lines.Clear();
        for (var index = 0; index < LineCount; index++)
        {
            var line = new CardLyricsTextLine { Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(line.BaseText, $"card-lyrics-line-{index}-base");
            AutomationProperties.SetAutomationId(line.ScanText, $"card-lyrics-line-{index}-scan");
            _lines.Add(line);
            _track.Children.Add(line);
        }
        _viewport.Children.Add(_track);
        Children.Add(_viewport);
        _trackVisual = ElementCompositionPreview.GetElementVisual(_track);
        _layoutBuilds++;
        UpdateBrushColors();
    }

    private void OnViewportLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_active || _viewport is null) return;
        if (_viewport.ActualWidth > 0 && _viewport.ActualHeight > 0)
        {
            _layoutWidth = _viewport.ActualWidth;
            _layoutHeight = _viewport.ActualHeight;
        }
        if (_transitionActive)
        {
            CancelTransition();
            if (_lastFrame is { IsDisplayable: true } latest) ApplyImmediate(latest, animateScan: false);
        }
        else if (_lastFrame is { IsDisplayable: true } frame)
        {
            ApplyImmediate(frame, animateScan: false);
        }
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || !_active || _viewport is null) return;
        _layoutWidth = e.NewSize.Width;
        _layoutHeight = e.NewSize.Height;
        if (_transitionActive)
        {
            CancelTransition();
            if (_lastFrame is { IsDisplayable: true } latest)
            {
                ApplyImmediate(latest, animateScan: false);
                return;
            }
        }
        if (_lastFrame is { IsDisplayable: true } frame)
            ApplyImmediate(frame, animateScan: false);
    }

    private void ApplyUnavailable(CardLyricsFrame frame)
    {
        _appliedFrame = frame;
        _hasAppliedFrame = false;
        CancelTransition();
        ClearScanTarget();
        _animationTimer.Stop();
        if (_viewport is not null) _viewport.Visibility = Visibility.Collapsed;
    }

    private void ApplyImmediate(CardLyricsFrame frame, bool animateScan)
    {
        EnsureLayout();
        if (_viewport is null) return;
        if (_pendingFrame is { } pending && pending.Revision > frame.Revision)
            frame = pending;
        _pendingFrame = null;

        ApplyStyle(frame);
        if (!UpdateMetricsAndLayout())
        {
            _lastFrame = frame;
            _appliedFrame = frame;
            _hasAppliedFrame = true;
            return;
        }
        _viewport.Visibility = Visibility.Visible;

        if (_primaryIndex is < 0 or >= LineCount) _primaryIndex = 0;
        if (_secondaryIndex is < 0 or >= LineCount) _secondaryIndex = 1;
        if (_primaryIndex == _secondaryIndex) _secondaryIndex = (_primaryIndex + 1) % LineCount;

        var primary = _lines[_primaryIndex];
        var secondary = _lines[_secondaryIndex];
        var translation = frame.Lyrics.TranslationMode && !_singleLine;
        var secondaryText = translation
            ? (string.IsNullOrWhiteSpace(frame.Lyrics.CurrentTranslation) ? "…" : frame.Lyrics.CurrentTranslation)
            : frame.Lyrics.Next;

        primary.SetText(frame.Lyrics.Current);
        primary.SetRole(CardLyricsLineRole.Primary);
        primary.Visibility = Visibility.Visible;
        secondary.SetText(secondaryText);
        secondary.SetRole(
            translation ? CardLyricsLineRole.Translation : CardLyricsLineRole.Secondary,
            translation && string.IsNullOrWhiteSpace(frame.Lyrics.CurrentTranslation));
        secondary.Visibility = _singleLine || string.IsNullOrWhiteSpace(secondaryText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        for (var index = 0; index < _lines.Count; index++)
        {
            if (index != _primaryIndex && index != _secondaryIndex)
            {
                _lines[index].Visibility = Visibility.Collapsed;
                _lines[index].Visual.StopAnimation(nameof(Visual.Opacity));
                _lines[index].Opacity = 1;
            }
        }
        primary.Visual.StopAnimation(nameof(Visual.Opacity));
        secondary.Visual.StopAnimation(nameof(Visual.Opacity));

        LayoutSteadyRows();
        primary.Opacity = HighContrast ? 1 : .98;
        secondary.Opacity = HighContrast
            ? 1
            : translation
                ? (string.IsNullOrWhiteSpace(frame.Lyrics.CurrentTranslation) ? .55 : 1)
                : SecondaryOpacity(frame.Lyrics.Progress);
        SetScanTarget(frame, snap: !animateScan);
        _appliedFrame = frame;
        _lastFrame = frame;
        _hasAppliedFrame = true;
        if (!_transitionActive && !ScanSmoothing) _animationTimer.Stop();
    }

    private bool TryStartRoll(CardLyricsFrame frame, CardLyricsFrame applied)
    {
        if (_transitionActive) return false;
        ApplyStyle(frame);
        if (!UpdateMetricsAndLayout() || _viewport is null || _singleLine ||
            frame.Lyrics.CurrentLineIndex != applied.Lyrics.CurrentLineIndex + 1)
            return false;

        var inactive = Enumerable.Range(0, LineCount)
            .Where(index => index != _primaryIndex && index != _secondaryIndex)
            .ToArray();
        if (inactive.Length < (frame.Lyrics.TranslationMode ? 2 : 1)) return false;

        var oldPrimary = _lines[_primaryIndex];
        var oldSecondary = _lines[_secondaryIndex];
        int incomingPrimary;
        int incomingSecondary;
        double durationMs;
        if (frame.Lyrics.TranslationMode)
        {
            incomingPrimary = inactive[0];
            incomingSecondary = inactive[1];
            var incomingOriginal = _lines[incomingPrimary];
            var incomingTranslation = _lines[incomingSecondary];
            incomingOriginal.SetText(frame.Lyrics.Current);
            incomingOriginal.SetRole(CardLyricsLineRole.Secondary);
            incomingOriginal.Visibility = Visibility.Visible;
            incomingTranslation.SetText(string.IsNullOrWhiteSpace(frame.Lyrics.CurrentTranslation)
                ? "…"
                : frame.Lyrics.CurrentTranslation);
            incomingTranslation.SetRole(
                CardLyricsLineRole.Translation,
                string.IsNullOrWhiteSpace(frame.Lyrics.CurrentTranslation));
            incomingTranslation.Visibility = Visibility.Visible;
            _transitionRows = 2;
            durationMs = PairRollDuration.TotalMilliseconds;
        }
        else
        {
            // A sequential line change promotes the already-visible next line.
            // If the backend's current text differs, use the immediate path so
            // a seek/track edit can never be presented as a fake roll.
            if (!string.Equals(oldSecondary.Text, frame.Lyrics.Current, StringComparison.Ordinal))
                return false;
            incomingPrimary = _secondaryIndex;
            incomingSecondary = inactive[0];
            var incoming = _lines[incomingSecondary];
            incoming.SetText(frame.Lyrics.Next);
            incoming.SetRole(CardLyricsLineRole.Secondary);
            incoming.Visibility = string.IsNullOrWhiteSpace(frame.Lyrics.Next)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _transitionRows = 1;
            durationMs = SingleRollDuration.TotalMilliseconds;
        }

        foreach (var line in _lines)
        {
            line.Visual.StopAnimation(nameof(Visual.Opacity));
            if (line.Visibility != Visibility.Visible) line.Opacity = 1;
        }
        oldPrimary.Opacity = .98;
        oldSecondary.Opacity = SecondaryOpacity(applied.Lyrics.Progress);
        var incomingLine = _lines[incomingPrimary];
        var incomingSecondaryLine = _lines[incomingSecondary];
        incomingLine.Opacity = frame.Lyrics.TranslationMode ? .72 : SecondaryOpacity(applied.Lyrics.Progress);
        incomingSecondaryLine.Opacity = 0;

        _primaryIndex = incomingPrimary;
        _secondaryIndex = incomingSecondary;
        _transitionPrimaryIndex = incomingPrimary;
        _transitionSecondaryIndex = incomingSecondary;
        var width = Math.Max(1, _layoutWidth > 0 ? _layoutWidth : _viewport.ActualWidth);
        var primaryY = PrimaryOffset(frame.Style);
        var secondaryY = RowTop(1, frame.Style);
        oldPrimary.Arrange(width, _rowHeight, _alignment, primaryY);
        // A single roll promotes the visible second row; a translation roll
        // promotes the incoming pair. Position each so its post-roll top equals
        // the steady-state top and the final commit does not jump.
        var promotedPrimaryTop = frame.Lyrics.TranslationMode
            ? RowTop(2, frame.Style) + (primaryY - Clamp(frame.Style.SecondaryOffsetY, 0, 8))
            : RowTop(1, frame.Style) + (primaryY - Clamp(frame.Style.SecondaryOffsetY, 0, 8));
        var incomingSecondaryTop = frame.Lyrics.TranslationMode
            ? RowTop(3, frame.Style)
            : RowTop(2, frame.Style);
        oldSecondary.Arrange(
            width,
            _rowHeight,
            _alignment,
            frame.Lyrics.TranslationMode ? secondaryY : promotedPrimaryTop);
        incomingLine.Arrange(width, _rowHeight, _alignment, promotedPrimaryTop);
        incomingSecondaryLine.Arrange(
            width,
            _rowHeight,
            _alignment,
            incomingSecondaryTop);

        var distance = _rowPitch * _transitionRows;
        _transitionDistance = distance;
        _transitionActive = true;
        _transitionTarget = frame;
        _transitionEndsAt = DateTimeOffset.UtcNow.AddMilliseconds(durationMs);
        StartTrackRoll(distance, TimeSpan.FromMilliseconds(durationMs));
        AnimateOpacity(oldPrimary, .98, .16, durationMs);
        AnimateOpacity(oldSecondary, oldSecondary.Opacity, .98, durationMs);
        AnimateOpacity(incomingLine, incomingLine.Opacity, .98, durationMs);
        if (incomingSecondaryLine.Visibility == Visibility.Visible)
            AnimateOpacity(
                incomingSecondaryLine,
                0,
                frame.Lyrics.TranslationMode ? 1 : SecondaryOpacity(frame.Lyrics.Progress),
                durationMs);
        UpdatePromotingScan(frame);
        _animationTimer.Start();
        return true;
    }

    private static void AnimateOpacity(CardLyricsTextLine line, double from, double to, double durationMs)
    {
        var compositor = line.Visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        animation.InsertKeyFrame(0, (float)from);
        animation.InsertKeyFrame(1, (float)to);
        line.Visual.StopAnimation(nameof(Visual.Opacity));
        line.Visual.Opacity = (float)from;
        line.Visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private void StartTrackRoll(double distance, TimeSpan duration)
    {
        if (_trackVisual is null) return;
        var compositor = _trackVisual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = duration;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(.22f, .72f),
            new Vector2(.24f, 1f));
        animation.InsertKeyFrame(0, Vector3.Zero);
        animation.InsertKeyFrame(1, new Vector3(0, (float)-distance, 0), easing);
        _trackVisual.StopAnimation(nameof(Visual.Offset));
        _trackVisual.Offset = Vector3.Zero;
        _trackVisual.StartAnimation(nameof(Visual.Offset), animation);
    }

    private void OnAnimationTimerTick(object? sender, object args)
    {
        if (!_active || _disposed)
        {
            _animationTimer.Stop();
            return;
        }

        if (_transitionActive && DateTimeOffset.UtcNow >= _transitionEndsAt)
        {
            CompleteTransition();
            return;
        }

        var smoothing = false;
        if (!_transitionActive && _scanTarget is { } target && _displayedScan is { } displayed)
        {
            if (displayed < target)
            {
                displayed += (target - displayed) * .3;
                if (target - displayed < .002) displayed = target;
                _displayedScan = displayed;
                ApplyDisplayedScan();
                smoothing = displayed != target;
            }
            else if (displayed > target)
            {
                _displayedScan = target;
                ApplyDisplayedScan();
            }
        }
        if (!_transitionActive && !smoothing) _animationTimer.Stop();
    }

    private void CompleteTransition()
    {
        var target = _pendingFrame ?? _transitionTarget ?? _lastFrame;
        _transitionActive = false;
        _transitionTarget = null;
        _transitionEndsAt = default;
        _transitionDistance = 0;
        if (target is null || !_active)
        {
            CancelTransition();
            return;
        }
        if (_trackVisual is not null)
        {
            _trackVisual.StopAnimation(nameof(Visual.Offset));
            _trackVisual.Offset = Vector3.Zero;
        }
        foreach (var line in _lines)
        {
            line.Visual.StopAnimation(nameof(Visual.Opacity));
            line.Opacity = 1;
        }
        _pendingFrame = null;
        _lastFrame = target;
        ApplyImmediate(target, animateScan: false);
    }

    private void CancelTransition()
    {
        _transitionActive = false;
        _transitionTarget = null;
        _transitionEndsAt = default;
        _transitionDistance = 0;
        if (_trackVisual is not null)
        {
            _trackVisual.StopAnimation(nameof(Visual.Offset));
            _trackVisual.Offset = Vector3.Zero;
        }
        foreach (var line in _lines) line.Visual.StopAnimation(nameof(Visual.Opacity));
    }

    private bool CanContinueTransition(CardLyricsFrame frame)
    {
        var target = _transitionTarget;
        return target is not null &&
            frame.IsDisplayable &&
            frame.TrackId == target.TrackId &&
            frame.Lyrics.TrackId == target.Lyrics.TrackId &&
            frame.Lyrics.CurrentLineIndex == target.Lyrics.CurrentLineIndex &&
            frame.Lyrics.TranslationMode == target.Lyrics.TranslationMode &&
            frame.IsPlaying == target.IsPlaying;
    }

    private static bool IsSeekOrJump(CardLyricsFrame applied, CardLyricsFrame frame)
    {
        if (applied.Lyrics.CurrentLineIndex < 0 || frame.Lyrics.CurrentLineIndex < 0)
            return frame.Lyrics.CurrentLineIndex != applied.Lyrics.CurrentLineIndex;
        var delta = frame.Lyrics.CurrentLineIndex - applied.Lyrics.CurrentLineIndex;
        if (delta == 0)
            return Math.Abs(frame.Lyrics.Progress - applied.Lyrics.Progress) > .34;
        return delta != 1;
    }

    private static bool CanSmoothScan(CardLyricsFrame? applied, CardLyricsFrame frame) =>
        applied is not null &&
        frame.IsPlaying &&
        applied.IsPlaying &&
        frame.TrackId == applied.TrackId &&
        frame.Lyrics.CurrentLineIndex == applied.Lyrics.CurrentLineIndex;

    private void SetScanTarget(CardLyricsFrame frame, bool snap)
    {
        if (_viewport is null) return;
        if (_transitionActive) return;
        if (_scanTrackId != frame.TrackId || _scanLineIndex != frame.Lyrics.CurrentLineIndex)
            snap = true;
        _scanTrackId = frame.TrackId;
        _scanLineIndex = frame.Lyrics.CurrentLineIndex;
        _scanTarget = frame.Lyrics.WordScanProgress;
        if (_scanTarget is null)
        {
            _displayedScan = null;
            ApplyDisplayedScan();
            return;
        }
        if (!frame.IsPlaying || ReducedMotion || snap || !_displayedScan.HasValue ||
            Math.Abs(_displayedScan.Value - _scanTarget.Value) > .2)
            _displayedScan = _scanTarget;
        ApplyDisplayedScan();
        if (!_transitionActive && ScanSmoothing) _animationTimer.Start();
    }

    private void ClearScanTarget()
    {
        _scanTarget = null;
        _displayedScan = null;
        _scanTrackId = "";
        _scanLineIndex = -1;
        ApplyDisplayedScan();
    }

    private void ApplyDisplayedScan()
    {
        if (_viewport is null || _lines.Count == 0) return;
        var scanning = _scanTarget.HasValue;
        for (var index = 0; index < _lines.Count; index++)
        {
            if (index == _primaryIndex && !_transitionActive && _lines[index].Visibility == Visibility.Visible)
                _lines[index].SetScan(_displayedScan, scanning);
            else if (_transitionActive && index == _transitionPrimaryIndex &&
                _lines[index].Visibility == Visibility.Visible)
                _lines[index].SetScan(_lines[index].ScanClipWidth > 0 ? _displayedScan : null, scanning);
            else
                _lines[index].SetScan(null, false);
        }
    }

    private void UpdatePromotingScan(CardLyricsFrame frame)
    {
        if (_viewport is null) return;
        var index = _transitionActive
            ? _transitionPrimaryIndex
            : _primaryIndex;
        if (index < 0 || index >= _lines.Count) return;
        var line = _lines[index];
        if (line.Visibility == Visibility.Visible && frame.Lyrics.WordScanProgress is { } progress)
            line.SetScan(progress, true);
    }

    private void ApplyStyle(CardLyricsFrame frame)
    {
        var style = frame.Style;
        var native = frame.NativeAppearance;
        _lastStyleFrame = frame;
        _requestedFontSize = native.FontSize ?? style.FontSize;
        _layoutScale = Math.Clamp(style.LayoutScalePercent / 100.0, .25, 4);
        _alignment = style.TextAlignment;
        _textShadow = style.TextShadow && !HighContrast;
        var family = native.FontFamily ??
            (!string.IsNullOrWhiteSpace(_cardFallbackFontFamily) ? _cardFallbackFontFamily : style.FontFamily);
        if (string.IsNullOrWhiteSpace(family)) family = "Segoe UI Variable Text";
        if (!string.Equals(family, _fontFamilyName, StringComparison.Ordinal))
        {
            try
            {
                _fontFamily = new FontFamily(family);
                _fontFamilyName = family;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                _fontFamily = new FontFamily("Segoe UI Variable Text");
                _fontFamilyName = "Segoe UI Variable Text";
            }
        }
        _fontWeight = new FontWeight
        {
            Weight = (ushort)Math.Clamp(native.FontWeight ?? style.FontWeight, 100, 990)
        };
        _fontStyle = native.FontItalic == true ? FontStyle.Italic : FontStyle.Normal;
        _textDecorations = TextDecorations.None;
        if (native.FontUnderline == true) _textDecorations |= TextDecorations.Underline;
        if (native.FontStrikeOut == true) _textDecorations |= TextDecorations.Strikethrough;
        UpdateBrushColors();
        // A playback confirmation or unrelated progress tick can revive a v2
        // frame whose animateTransition flag is stale. Never start a line roll
        // from that flag alone; the sequence and timing checks below decide.
    }

    private void UpdateBrushColors()
    {
        Color primary, secondary, translation, scan;
        if (HighContrast)
        {
            var foreground = SystemColor(UIColorType.Foreground, Color.FromArgb(255, 255, 255, 255));
            var accent = SystemColor(UIColorType.Accent, Color.FromArgb(255, 0, 120, 215));
            primary = Color.FromArgb(255, foreground.R, foreground.G, foreground.B);
            secondary = primary;
            translation = primary;
            scan = Color.FromArgb(255, accent.R, accent.G, accent.B);
            if (scan == primary) scan = Color.FromArgb(255, 0, 120, 215);
        }
        else if (ActualTheme == ElementTheme.Dark)
        {
            primary = Color.FromArgb(242, 255, 255, 255);
            secondary = Color.FromArgb(166, 255, 255, 255);
            translation = Color.FromArgb(179, 255, 255, 255);
            scan = Color.FromArgb(255, 142, 216, 255);
        }
        else
        {
            primary = Color.FromArgb(242, 20, 20, 20);
            secondary = Color.FromArgb(166, 20, 20, 20);
            translation = Color.FromArgb(179, 20, 20, 20);
            scan = Color.FromArgb(255, 0, 95, 184);
        }
        _primaryBrush.Color = primary;
        _secondaryBrush.Color = secondary;
        _translationBrush.Color = translation;
        _scanBrush.Color = scan;
        _shadowBrush.Color = Color.FromArgb(HighContrast ? (byte)0 : (byte)92, 0, 0, 0);
        ApplyLineStyles();
    }

    private void ApplyLineStyles()
    {
        if (_lines.Count == 0) return;
        var style = new CardLyricsLineVisualStyle(
            _fontFamily,
            Math.Max(1, _currentFontSize),
            Math.Max(1, _secondaryFontSize),
            _fontWeight,
            _fontStyle,
            _textDecorations,
            _primaryBrush,
            _secondaryBrush,
            _translationBrush,
            _scanBrush,
            _shadowBrush,
            _textShadow,
            _alignment);
        foreach (var line in _lines) line.SetStyle(style);
    }

    private bool UpdateMetricsAndLayout()
    {
        if (_viewport is null || _lines.Count == 0) return false;
        var width = _layoutWidth > 0 ? _layoutWidth : Math.Max(_viewport.ActualWidth, ActualWidth);
        var height = _layoutHeight > 0 ? _layoutHeight : Math.Max(_viewport.ActualHeight, ActualHeight);
        if (!(width > 0) || !(height > 0)) return false;

        var usableHeight = Math.Max(1, height - Clamp(_lastStyleFrame.Style.ViewportDescenderBuffer, 0, 24));
        _singleLine = usableHeight < _requestedFontSize * 2.4;
        _rowHeight = Math.Max(1, Math.Floor(usableHeight / (_singleLine ? 1 : 2)));
        _rowGap = _singleLine ? 0 : Math.Max(0, usableHeight - (_rowHeight * 2));
        _rowPitch = _rowHeight + _rowGap;
        var currentMaximum = Math.Max(11.2 * _layoutScale, _rowHeight * .92);
        _currentFontSize = Math.Min(_requestedFontSize, currentMaximum);
        _secondaryFontSize = Math.Max(9 * _layoutScale, _currentFontSize * .92);
        _viewportClip.Rect = new Rect(0, 0, width, height);
        _track!.Width = width;
        _track.Height = Math.Max(height, (LineCount * _rowPitch) + _rowHeight);
        Canvas.SetLeft(_track, 0);
        Canvas.SetTop(_track, 0);
        ApplyLineStyles();
        LayoutSteadyRows();
        return true;
    }

    private void LayoutSteadyRows()
    {
        if (_viewport is null || _lines.Count == 0) return;
        var width = Math.Max(1, _layoutWidth > 0 ? _layoutWidth : _viewport.ActualWidth);
        for (var index = 0; index < _lines.Count; index++)
        {
            if (index != _primaryIndex && index != _secondaryIndex)
            {
                _lines[index].Visibility = Visibility.Collapsed;
                continue;
            }
            var role = index == _primaryIndex
                ? CardLyricsLineRole.Primary
                : _lastStyleFrame.Lyrics.TranslationMode
                    ? CardLyricsLineRole.Translation
                    : CardLyricsLineRole.Secondary;
            var top = index == _primaryIndex
                ? PrimaryOffset(_lastStyleFrame.Style)
                : RowTop(1, _lastStyleFrame.Style);
            _lines[index].SetRole(role, role == CardLyricsLineRole.Translation &&
                string.IsNullOrWhiteSpace(_lastStyleFrame.Lyrics.CurrentTranslation));
            _lines[index].Arrange(width, _rowHeight, _alignment, top);
            _lines[index].Visibility = index == _secondaryIndex && _singleLine
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private static double PrimaryOffset(CardLyricsStyle style) =>
        Clamp(style.PrimaryOffsetY, -8, 8);

    private double RowTop(int row, CardLyricsStyle style) =>
        row * _rowPitch + Clamp(style.SecondaryOffsetY, -8, 8);

    private static double SecondaryOpacity(double progress) =>
        .58 + (1 - Math.Clamp(progress, 0, 1)) * .16;

    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        CancelTransition();
        _animationTimer.Stop();
        ActualThemeChanged -= OnActualThemeChanged;
        try { _accessibility.HighContrastChanged -= OnHighContrastChanged; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException) { }
        ReleaseResources();
        Children.Clear();
    }

    private Color SystemColor(UIColorType kind, Color fallback)
    {
        try { return _uiSettings.GetColorValue(kind); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return fallback;
        }
    }
}
