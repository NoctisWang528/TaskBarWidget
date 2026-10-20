using System.Text.Json;

namespace TaskBarWidgets.UI.Cards;

internal readonly record struct CardMicaTuning(double TintOpacity, double LuminosityOpacity)
{
    internal const double MinimumTintOpacity = 0;
    internal const double MaximumTintOpacity = 1;
    internal const double MinimumLuminosityOpacity = 0;
    internal const double MaximumLuminosityOpacity = 1;
    internal const double DefaultTintOpacity = 0.45;
    internal const double DefaultLuminosityOpacity = 0.85;
    internal const double DefaultDarkLuminosityOpacity = 0.50;

    private const double ThemeOffset = 0.02;

    internal static CardMicaTuning Default => DefaultFor(false);

    internal static CardMicaTuning DefaultFor(bool dark) =>
        new(DefaultTintOpacity, dark ? DefaultDarkLuminosityOpacity : DefaultLuminosityOpacity);

    internal double Transparency => 1 - LuminosityOpacity;

    internal double TintOpacityFor(bool dark) =>
        dark ? Math.Clamp(TintOpacity - ThemeOffset, MinimumTintOpacity, MaximumTintOpacity) : TintOpacity;

    internal double LuminosityOpacityFor(bool dark) =>
        dark ? Math.Clamp(LuminosityOpacity - ThemeOffset, MinimumLuminosityOpacity, MaximumLuminosityOpacity) : LuminosityOpacity;

    internal static CardMicaTuning FromUserValues(double tintPercent, double transparencyPercent) =>
        FromValues(tintPercent / 100, 1 - transparencyPercent / 100);

    internal static CardMicaTuning FromValues(double tintOpacity, double luminosityOpacity)
    {
        tintOpacity = double.IsFinite(tintOpacity) ? tintOpacity : DefaultTintOpacity;
        luminosityOpacity = double.IsFinite(luminosityOpacity) ? luminosityOpacity : DefaultLuminosityOpacity;
        return new CardMicaTuning(
            Math.Clamp(tintOpacity, MinimumTintOpacity, MaximumTintOpacity),
            Math.Clamp(luminosityOpacity, MinimumLuminosityOpacity, MaximumLuminosityOpacity));
    }

    internal static CardMicaTuning FromJson(JsonElement value, bool dark = false)
    {
        var fallback = DefaultFor(dark);
        if (value.ValueKind != JsonValueKind.Object) return fallback;
        return FromValues(
            ReadNumber(value, "tintOpacity", fallback.TintOpacity),
            ReadNumber(value, "luminosityOpacity", fallback.LuminosityOpacity));
    }

    internal bool Matches(CardMicaTuning other) =>
        Math.Abs(TintOpacity - other.TintOpacity) < 0.0005 &&
        Math.Abs(LuminosityOpacity - other.LuminosityOpacity) < 0.0005;

    private static double ReadNumber(JsonElement value, string name, double fallback) =>
        value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.Number &&
        item.TryGetDouble(out var number) &&
        double.IsFinite(number)
            ? number
            : fallback;
}

internal readonly record struct CardMicaPreviewState(
    CardMicaTuning Committed,
    CardMicaTuning? Preview);
