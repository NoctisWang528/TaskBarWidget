using System.Globalization;
using System.Windows.Media;
using TaskBarWidgets.Media.Integration;

namespace TaskbarLyrics.App;

internal static class NativeMediaPayloadFactory
{
    public static MediaStyleFrame CreateStyle(AppSettings settings, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
        }
        settings.NormalizeLyricsTextAlignment();
        settings.NormalizeCoverPosition();
        var foregroundColor = ParseColor(settings.ForegroundColor);
        var primaryColor = ForegroundColorPolicy.CreatePrimaryColor(foregroundColor);
        var secondaryColor = ForegroundColorPolicy.CreateSecondaryColor(foregroundColor);
        var translationColor = ForegroundColorPolicy.CreateTranslationColor(foregroundColor);
        var wordScanOverlayColor = ForegroundColorPolicy.CreateWordScanOverlayColor(foregroundColor);
        var metrics = LyricsLayoutMetrics.Create(settings, pixelsPerDip);
        return new MediaStyleFrame(
            fontFamily: AppSettings.NormalizeFontFamily(settings.FontFamily),
            layoutScalePercent: metrics.ScalePercent,
            fontSize: metrics.FontSize,
            showCover: settings.ShowCover,
            coverPosition: settings.CoverPosition.ToString(),
            enableControlPanel: settings.EnableControlPanel && !settings.UseFloatingWindow,
            coverSize: metrics.CoverSize,
            coverGap: metrics.CoverGap,
            coverCornerRadius: metrics.CoverCornerRadius,
            viewportDescenderBuffer: metrics.ViewportDescenderBuffer,
            layoutHorizontalPadding: metrics.LayoutHorizontalPadding,
            lyricsPaneTopPadding: metrics.LyricsPaneTopPadding,
            lyricsPaneRightPadding: metrics.LyricsPaneRightPadding,
            lyricsPaneLeftPadding: metrics.LyricsPaneLeftPadding,
            primaryOffsetY: metrics.PrimaryOffsetY,
            secondaryOffsetY: metrics.SecondaryOffsetY,
            lineTextBottomPadding: metrics.LineTextBottomPadding,
            surfaceRadius: metrics.SurfaceRadius,
            layerTransitionOffset: metrics.LayerTransitionOffset,
            coverFallbackFontSize: metrics.CoverFallbackFontSize,
            spectrumWidth: metrics.SpectrumWidth,
            spectrumHeight: metrics.SpectrumHeight,
            spectrumGap: metrics.SpectrumGap,
            spectrumBarWidth: metrics.SpectrumBarWidth,
            spectrumBarHeight: metrics.SpectrumBarHeight,
            spectrumLowHeight: metrics.SpectrumLowHeight,
            spectrumHighHeight: metrics.SpectrumHighHeight,
            spectrumMiddleHeight: metrics.SpectrumMiddleHeight,
            fontWeight: settings.FontWeight,
            primaryColor: ToCssColor(primaryColor),
            secondaryColor: ToCssColor(secondaryColor),
            translationColor: ToCssColor(translationColor),
            wordScanOverlayColor: ToCssColor(wordScanOverlayColor),
            surfaceColor: settings.ShowBackground
                ? $"rgba(18, 18, 24, {Math.Clamp(settings.BackgroundOpacity, 0, 1).ToString("0.####", CultureInfo.InvariantCulture)})"
                : "transparent",
            surfaceShadow: settings.ShowBorder
                ? "inset 0 0 0 1px rgba(255, 255, 255, 0.16)"
                : "none",
            textShadow: settings.ShowTextShadow
                ? "0 1px 2px rgba(0, 0, 0, 0.36)"
                : "none",
            textAlignment: settings.LyricsTextAlignment.ToString());
    }

    public static MediaCoverFrame CreateCover(
        string? dataUri,
        string fallbackText,
        string fallbackColor,
        string? fallbackIconDataUri,
        string? trackId) =>
        new(
            dataUri ?? string.Empty,
            fallbackText,
            fallbackColor,
            fallbackIconDataUri ?? string.Empty,
            trackId ?? string.Empty);

    public static MediaCoverFrame EmptyCover() =>
        CreateCover("", "", "", "", "");

    public static MediaSpectrumTuningFrame CreateSpectrumTuning(SpectrumTuningSettings settings) =>
        new(
            settings.FrontendRise,
            settings.FrontendFall,
            settings.MinBarHeight,
            settings.BarHeightRange,
            settings.BarOpacity,
            settings.BarCount);

    private static System.Windows.Media.Color ParseColor(string color)
    {
        try
        {
            return new BrushConverter().ConvertFromString(color) is SolidColorBrush brush
                ? brush.Color
                : Colors.White;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        {
            return Colors.White;
        }
    }

    private static string ToCssColor(System.Windows.Media.Color color) =>
        $"rgba({color.R}, {color.G}, {color.B}, {(color.A / 255.0).ToString("0.###", CultureInfo.InvariantCulture)})";
}
