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
    // Preserve existing custom defaults until the desktop comparison is accepted.
    // System mode obtains defaults from the runtime, never from these constants.
    internal const double DefaultLightTintOpacity = 0.10;
    internal const double DefaultLightLuminosityOpacity = 0.85;
    internal const double DefaultDarkTintOpacity = 0.15;
    internal const double DefaultDarkLuminosityOpacity = 0.90;
    internal const bool DefaultVivid = true;
    private const double LegacyThemeOffset = 0.02;

    internal static CardMicaTuning Default => new(
        DefaultLightTintOpacity,
        DefaultLightLuminosityOpacity,
        DefaultDarkTintOpacity,
        DefaultDarkLuminosityOpacity);

    internal double TintOpacityFor(bool dark) => dark ? DarkTintOpacity : LightTintOpacity;

    internal double LuminosityOpacityFor(bool dark) => dark ? DarkLuminosityOpacity : LightLuminosityOpacity;

    internal CardMicaTuning Normalized() => new(
        Normalize(LightTintOpacity, DefaultLightTintOpacity),
        Normalize(LightLuminosityOpacity, DefaultLightLuminosityOpacity),
        Normalize(DarkTintOpacity, DefaultDarkTintOpacity),
        Normalize(DarkLuminosityOpacity, DefaultDarkLuminosityOpacity));

    internal static CardMicaTuning FromUserValues(
        double lightTintPercent,
        double lightLuminosityPercent,
        double darkTintPercent,
        double darkLuminosityPercent) =>
        FromThemeValues(
            lightTintPercent / 100,
            lightLuminosityPercent / 100,
            darkTintPercent / 100,
            darkLuminosityPercent / 100);

    internal static CardMicaTuning FromUserValues(double tintPercent, double luminosityPercent) =>
        FromValues(tintPercent / 100, luminosityPercent / 100);

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

internal readonly record struct CardMicaAppearance(bool Vivid, CardMicaTuning Tuning)
{
    internal static CardMicaAppearance Default => new(CardMicaTuning.DefaultVivid, CardMicaTuning.Default);
}

internal readonly record struct CardMicaPreviewState(
    CardMicaAppearance Committed,
    CardMicaAppearance? Preview);
