using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Text;

namespace TaskBarWidgets.UI.Cards;

internal enum CardLyricsLineRole
{
    Primary,
    Secondary,
    Translation
}

internal sealed record CardLyricsLineVisualStyle(
    FontFamily FontFamily,
    double PrimaryFontSize,
    double SecondaryFontSize,
    FontWeight FontWeight,
    FontStyle FontStyle,
    TextDecorations TextDecorations,
    SolidColorBrush PrimaryBrush,
    SolidColorBrush SecondaryBrush,
    SolidColorBrush TranslationBrush,
    SolidColorBrush ScanBrush,
    SolidColorBrush ShadowBrush,
    bool TextShadow,
    string Alignment);

// One reusable line contains a shadow, a base copy, and a scan copy. All three
// copies receive identical typography and measured width; only the scan copy's
// clip and horizontal scroll offset change while word scanning progresses.
internal sealed class CardLyricsTextLine : Canvas
{
    private readonly TextBlock _shadow = NewTextBlock();
    private readonly TextBlock _base = NewTextBlock();
    private readonly TextBlock _scan = NewTextBlock();
    private readonly RectangleGeometry _scanClip = new();
    private CardLyricsLineVisualStyle? _style;
    private string _text = "";
    private bool _layoutDirty = true;
    private bool _scanning;
    private double? _scanProgress;
    private double _contentWidth;
    private double _viewportWidth;
    private double _lineHeight;
    private double _rowHeight;
    private double _childY;
    private string _alignment = "Left";
    private bool _horizontalOverflow;
    private double _scrollOffset;
    private bool _roleInitialized;
    private bool _placeholder;
    private CardLyricsLineRole _role = CardLyricsLineRole.Primary;

    internal CardLyricsTextLine()
    {
        IsHitTestVisible = false;
        Background = null;
        Height = 1;
        _scan.Clip = _scanClip;
        _scan.Visibility = Visibility.Collapsed;
        _shadow.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAccessibilityView(_shadow, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_scan, AccessibilityView.Raw);
        Children.Add(_shadow);
        Children.Add(_base);
        Children.Add(_scan);
    }

    internal TextBlock BaseText => _base;
    internal TextBlock ScanText => _scan;
    internal TextBlock ShadowText => _shadow;
    internal string Text => _text;
    internal bool IsScanning => _scanning;
    internal double ScanClipWidth => _scanClip.Rect.Width;
    internal double ContentWidth => _contentWidth;
    internal double HorizontalOffset => _scrollOffset;
    internal bool HasHorizontalOverflow => _horizontalOverflow;
    internal int MeasureCount { get; private set; }
    internal Visual Visual => ElementCompositionPreview.GetElementVisual(this);

    internal void SetText(string value)
    {
        value ??= "";
        if (string.Equals(_text, value, StringComparison.Ordinal)) return;
        _text = value;
        var display = value.Length > 0 ? value : " ";
        _shadow.Text = display;
        _base.Text = display;
        _scan.Text = display;
        _layoutDirty = true;
    }

    internal void SetStyle(CardLyricsLineVisualStyle style)
    {
        if (Equals(_style, style)) return;
        var previous = _style;
        var previousFontSize = _base.FontSize;
        var fontSize = RoleFontSize(style, _role);
        var typographyChanged = previous is null ||
            !Equals(previous.FontFamily, style.FontFamily) ||
            previous.FontWeight != style.FontWeight ||
            previous.FontStyle != style.FontStyle ||
            previous.TextDecorations != style.TextDecorations ||
            Math.Abs(previousFontSize - fontSize) > .01;
        _style = style;
        foreach (var text in new[] { _shadow, _base, _scan })
        {
            text.FontFamily = style.FontFamily;
            text.FontSize = fontSize;
            text.FontWeight = style.FontWeight;
            text.FontStyle = style.FontStyle;
            text.TextDecorations = style.TextDecorations;
            text.IsTextScaleFactorEnabled = false;
            text.TextWrapping = TextWrapping.NoWrap;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        _shadow.Foreground = style.ShadowBrush;
        _shadow.Visibility = style.TextShadow ? Visibility.Visible : Visibility.Collapsed;
        if (typographyChanged) _layoutDirty = true;
        ApplyColors();
    }

    internal void SetRole(CardLyricsLineRole role, bool placeholder = false)
    {
        if (_style is null) return;
        var fontSize = RoleFontSize(_style, role);
        var roleChanged = !_roleInitialized || role != _role || placeholder != _placeholder;
        var fontChanged = Math.Abs(_base.FontSize - fontSize) > .01 ||
            Math.Abs(_scan.FontSize - fontSize) > .01 ||
            Math.Abs(_shadow.FontSize - fontSize) > .01;
        if (!roleChanged && !fontChanged) return;
        _roleInitialized = true;
        _placeholder = placeholder;
        _role = role;
        foreach (var text in new[] { _shadow, _base, _scan })
            text.FontSize = fontSize;
        Opacity = role switch
        {
            CardLyricsLineRole.Primary => .98,
            CardLyricsLineRole.Translation => placeholder ? .55 : 1,
            _ => .72
        };
        if (roleChanged) ApplyColors();
        if (fontChanged) _layoutDirty = true;
    }

    private static double RoleFontSize(CardLyricsLineVisualStyle style, CardLyricsLineRole role) =>
        role == CardLyricsLineRole.Primary ? style.PrimaryFontSize : style.SecondaryFontSize;

    internal void SetScan(double? progress, bool scanning)
    {
        var normalized = progress.HasValue ? Math.Clamp(progress.Value, 0, 1) : (double?)null;
        var modeChanged = scanning != _scanning;
        _scanning = scanning && normalized.HasValue;
        _scanProgress = _scanning ? normalized : null;
        if (modeChanged)
        {
            _scan.Visibility = _scanning ? Visibility.Visible : Visibility.Collapsed;
            _shadow.Visibility = _style is { TextShadow: true } ? Visibility.Visible : Visibility.Collapsed;
            _layoutDirty = true;
            ApplyColors();
        }
        ApplyScanLayout();
    }

    // Measures only when text, typography, row metrics, or scan mode changes.
    // Progress-only frames reuse the measured width and visual tree.
    internal void Arrange(double viewportWidth, double rowHeight, string alignment, double verticalOffset)
    {
        viewportWidth = Math.Max(0, viewportWidth);
        rowHeight = Math.Max(1, rowHeight);
        var metricsChanged = Math.Abs(_viewportWidth - viewportWidth) > .01 ||
            Math.Abs(_rowHeight - rowHeight) > .01;
        var alignmentChanged = !string.Equals(_alignment, alignment, StringComparison.Ordinal);
        _viewportWidth = viewportWidth;
        _rowHeight = rowHeight;
        _alignment = alignment;
        if (!metricsChanged && !alignmentChanged && !_layoutDirty)
        {
            Canvas.SetTop(this, verticalOffset + _childY);
            return;
        }

        Width = viewportWidth;
        Height = rowHeight;
        MeasureCount++;
        var requestedLineHeight = Math.Max(1, _base.FontSize * 1.12);
        foreach (var text in new[] { _shadow, _base, _scan })
        {
            text.Width = double.NaN;
            text.MaxWidth = double.PositiveInfinity;
            text.TextTrimming = TextTrimming.None;
            text.LineHeight = requestedLineHeight;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        _contentWidth = Math.Max(1, Math.Ceiling(_base.DesiredSize.Width));
        _lineHeight = Math.Max(1, Math.Ceiling(_base.DesiredSize.Height));
        _childY = Math.Max(0, Math.Floor((rowHeight - _lineHeight) / 2));
        _layoutDirty = false;
        Canvas.SetTop(this, verticalOffset + _childY);
        ApplyScanLayout();
    }

    private void ApplyScanLayout()
    {
        if (_style is null) return;
        var horizontalScan = _scanning && _scanProgress.HasValue;
        var shortAligned = !horizontalScan && _contentWidth < Math.Max(1, _viewportWidth);
        var width = horizontalScan || shortAligned
            ? _contentWidth
            : Math.Min(_contentWidth, Math.Max(1, _viewportWidth));
        foreach (var text in new[] { _shadow, _base, _scan })
        {
            text.Width = width;
            text.TextTrimming = horizontalScan ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            text.TextAlignment = horizontalScan || shortAligned
                ? TextAlignment.Left
                : Alignment(_alignment);
            text.LineHeight = _lineHeight;
        }

        _horizontalOverflow = _contentWidth - _viewportWidth > .5;
        var baseX = shortAligned
            ? _alignment switch
            {
                "Center" => Math.Max(0, (_viewportWidth - _contentWidth) / 2),
                "Right" => Math.Max(0, _viewportWidth - _contentWidth),
                _ => 0
            }
            : 0d;
        if (horizontalScan)
        {
            if (!_horizontalOverflow)
            {
                baseX = _alignment switch
                {
                    "Center" => Math.Max(0, (_viewportWidth - _contentWidth) / 2),
                    "Right" => Math.Max(0, _viewportWidth - _contentWidth),
                    _ => 0
                };
                _scrollOffset = 0;
            }
            else
            {
                var progress = _scanProgress ?? 0;
                var anchor = _viewportWidth * .65;
                _scrollOffset = Math.Clamp(progress * _contentWidth - anchor, 0, _contentWidth - _viewportWidth);
            }
        }
        else
        {
            _scrollOffset = 0;
        }
        var left = _horizontalOverflow ? -_scrollOffset : baseX;
        Canvas.SetLeft(_shadow, left);
        Canvas.SetLeft(_base, left);
        Canvas.SetLeft(_scan, left);
        Canvas.SetTop(_shadow, 1);
        Canvas.SetTop(_base, 0);
        Canvas.SetTop(_scan, 0);

        if (_scanning && _scanProgress.HasValue)
        {
            var visible = Math.Clamp(_scanProgress.Value, 0, 1) * _contentWidth;
            _scanClip.Rect = new Rect(0, 0, visible, Math.Max(1, _lineHeight));
            _scan.Visibility = visible > .01 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _scanClip.Rect = new Rect(0, 0, 0, Math.Max(1, _lineHeight));
            _scan.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyColors()
    {
        if (_style is null) return;
        _base.Foreground = _scanning
            ? _style.SecondaryBrush
            : _role switch
            {
                CardLyricsLineRole.Primary => _style.PrimaryBrush,
                CardLyricsLineRole.Translation => _style.TranslationBrush,
                _ => _style.SecondaryBrush
            };
        _scan.Foreground = _style.ScanBrush;
        _shadow.Foreground = _style.ShadowBrush;
    }

    private static TextAlignment Alignment(string value) => value switch
    {
        "Center" => TextAlignment.Center,
        "Right" => TextAlignment.Right,
        _ => TextAlignment.Left
    };

    internal void Reset()
    {
        SetText("");
        SetScan(null, false);
        Visibility = Visibility.Collapsed;
        Visual.StopAnimation(nameof(Visual.Offset));
        Visual.Offset = System.Numerics.Vector3.Zero;
        Opacity = 1;
    }

    private static TextBlock NewTextBlock() => new()
    {
        IsHitTestVisible = false,
        IsTextScaleFactorEnabled = false,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };
}
