using System.Text.Json;

namespace TaskBarWidgets.UI.Cards;

internal readonly record struct CardMicaTuning(
    double LightTintOpacity,
    double LightLuminosityOpacity,
    double DarkTintOpacity,
    double DarkLuminosityOpacity)
{
    internal const double MinimumTintOpacity = 0;
    internal const double MaximumTintOpacity = 1;
    internal const double MinimumLuminosityOpacity = 0;
    internal const double MaximumLuminosityOpacity = 1;
    internal const double DefaultLightTintOpacity = 0.45;
    internal const double DefaultLightLuminosityOpacity = 0.95;
    internal const double DefaultDarkTintOpacity = 0.45;
    internal const double DefaultDarkLuminosityOpacity = 0.50;

    private const double LegacyThemeOffset = 0.02;

    internal static CardMicaTuning Default => new(
        DefaultLightTintOpacity,
        DefaultLightLuminosityOpacity,
        DefaultDarkTintOpacity,
        DefaultDarkLuminosityOpacity);

    internal double LightTransparency => 1 - LightLuminosityOpacity;

    internal double DarkTransparency => 1 - DarkLuminosityOpacity;

    internal double TintOpacityFor(bool dark) => dark ? DarkTintOpacity : LightTintOpacity;

    internal double LuminosityOpacityFor(bool dark) => dark ? DarkLuminosityOpacity : LightLuminosityOpacity;

    internal CardMicaTuning Normalized() => new(
        Normalize(LightTintOpacity, DefaultLightTintOpacity),
        Normalize(LightLuminosityOpacity, DefaultLightLuminosityOpacity),
        Normalize(DarkTintOpacity, DefaultDarkTintOpacity),
        Normalize(DarkLuminosityOpacity, DefaultDarkLuminosityOpacity));

    internal static CardMicaTuning FromUserValues(
        double lightTintPercent,
        double lightTransparencyPercent,
        double darkTintPercent,
        double darkTransparencyPercent) =>
        FromThemeValues(
            lightTintPercent / 100,
            1 - lightTransparencyPercent / 100,
            darkTintPercent / 100,
            1 - darkTransparencyPercent / 100);

    internal static CardMicaTuning FromUserValues(double tintPercent, double transparencyPercent) =>
        FromValues(tintPercent / 100, 1 - transparencyPercent / 100);

    internal static CardMicaTuning FromValues(double tintOpacity, double luminosityOpacity)
        => FromThemeValues(tintOpacity, luminosityOpacity, tintOpacity, luminosityOpacity);

    internal static CardMicaTuning FromThemeValues(
        double lightTintOpacity,
        double lightLuminosityOpacity,
        double darkTintOpacity,
        double darkLuminosityOpacity) =>
        new(
            Normalize(lightTintOpacity, DefaultLightTintOpacity),
            Normalize(lightLuminosityOpacity, DefaultLightLuminosityOpacity),
            Normalize(darkTintOpacity, DefaultDarkTintOpacity),
            Normalize(darkLuminosityOpacity, DefaultDarkLuminosityOpacity));

    private static double Normalize(double value, double fallback)
    {
        if (!double.IsFinite(value)) return fallback;
        return Math.Clamp(value, 0, 1);
    }

    internal static CardMicaTuning FromJson(JsonElement value, bool dark = false)
    {
        if (value.ValueKind != JsonValueKind.Object) return Default;
        var lightTint = ReadNumber(value, "tintOpacity", DefaultLightTintOpacity);
        var lightLuminosity = ReadNumber(value, "luminosityOpacity", DefaultLightLuminosityOpacity);
        double darkTint;
        double darkLuminosity;
        if (value.TryGetProperty("darkTintOpacity", out _) ||
            value.TryGetProperty("darkLuminosityOpacity", out _))
        {
            darkTint = ReadNumber(value, "darkTintOpacity", DefaultDarkTintOpacity);
            darkLuminosity = ReadNumber(value, "darkLuminosityOpacity", DefaultDarkLuminosityOpacity);
        }
        else
        {
            // Payloads from before split light/dark tuning inherited the old
            // implicit dark-theme offset from their two light values.
            darkTint = lightTint - LegacyThemeOffset;
            darkLuminosity = lightLuminosity - LegacyThemeOffset;
        }
        return FromThemeValues(lightTint, lightLuminosity, darkTint, darkLuminosity);
    }

    internal bool Matches(CardMicaTuning other) =>
        Math.Abs(LightTintOpacity - other.LightTintOpacity) < 0.0005 &&
        Math.Abs(LightLuminosityOpacity - other.LightLuminosityOpacity) < 0.0005 &&
        Math.Abs(DarkTintOpacity - other.DarkTintOpacity) < 0.0005 &&
        Math.Abs(DarkLuminosityOpacity - other.DarkLuminosityOpacity) < 0.0005;

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
