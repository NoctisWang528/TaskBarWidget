using System.Text.Json;

namespace TaskBarWidgets.UI.Cards;

// The native card consumes only this versioned structured frame. It deliberately
// has no script/HTML representation so a frame can never execute markup.
internal sealed record CardLyricsFrame(
    long Revision,
    string TrackId,
    bool Available,
    bool IsPlaying,
    CardLyricsContent Lyrics,
    CardLyricsStyle Style,
    CardLyricsNativeAppearance NativeAppearance)
{
    internal bool IsDisplayable =>
        Available &&
        !Lyrics.IsPureMusic &&
        (!string.IsNullOrWhiteSpace(Lyrics.Current) ||
         !string.IsNullOrWhiteSpace(Lyrics.Next) ||
         Lyrics.CurrentLineIndex >= 0);
}

internal sealed record CardLyricsContent(
    string Current,
    string Next,
    double Progress,
    int CurrentLineIndex,
    string TrackId,
    bool IsPureMusic,
    bool IsPlaying,
    string CurrentTranslation,
    string NextTranslation,
    bool TranslationMode,
    bool AnimateTransition,
    double? WordScanProgress,
    string Scene);

internal sealed record CardLyricsStyle(
    string FontFamily,
    double LayoutScalePercent,
    double FontSize,
    int FontWeight,
    string TextAlignment,
    bool TextShadow,
    double ViewportDescenderBuffer,
    double PrimaryOffsetY,
    double SecondaryOffsetY);

internal sealed record CardLyricsNativeAppearance(
    string? FontFamily,
    double? FontSize,
    int? FontWeight,
    bool? FontItalic,
    bool? FontUnderline,
    bool? FontStrikeOut);

internal static class CardLyricsFrameParser
{
    private const int RenderVersion = 2;

    internal static bool TryParse(JsonElement value, out CardLyricsFrame frame)
    {
        frame = null!;
        if (value.ValueKind != JsonValueKind.Object ||
            !TryLong(value, "revision", out var revision) ||
            revision < 0 ||
            !TryBool(value, "available", out var available) ||
            !TryBool(value, "isPlaying", out var topLevelPlaying) ||
            !TryString(value, "trackId", out var topLevelTrackId))
            return false;

        // The structured contract is versioned; legacy v1 script frames have no
        // v2 marker and can never enter the native renderer.
        if (!value.TryGetProperty("renderVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) ||
            versionNumber != RenderVersion)
            return false;

        if (value.TryGetProperty("scripts", out _) || value.TryGetProperty("html", out _))
            return false;

        var lyrics = value.TryGetProperty("lyrics", out var lyricsValue) && lyricsValue.ValueKind == JsonValueKind.Object
            ? lyricsValue
            : default;
        var style = value.TryGetProperty("style", out var styleValue) && styleValue.ValueKind == JsonValueKind.Object
            ? styleValue
            : default;
        var native = value.TryGetProperty("nativeAppearance", out var nativeValue) &&
            nativeValue.ValueKind == JsonValueKind.Object
            ? nativeValue
            : default;

        var current = String(lyrics, "current");
        var next = String(lyrics, "next");
        var innerTrack = String(lyrics, "trackId");
        var trackId = innerTrack.Length > 0 ? innerTrack : topLevelTrackId;
        var progress = Clamp01(Double(lyrics, "progress"));
        var currentLineIndex = Integer(lyrics, "currentLineIndex", -1);
        var pureMusic = Bool(lyrics, "isPureMusic");
        var playing = lyrics.ValueKind == JsonValueKind.Object &&
            lyrics.TryGetProperty("isPlaying", out var isPlayingValue) &&
            isPlayingValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? isPlayingValue.GetBoolean()
            : topLevelPlaying;
        var scene = String(lyrics, "scene");
        if (scene.Length == 0)
            scene = pureMusic ? "spectrum" : currentLineIndex >= 0 ? "lyrics" : "message";

        var wordScan = OptionalClamp01(lyrics, "wordScanProgress");
        var content = new CardLyricsContent(
            current,
            next,
            progress,
            currentLineIndex,
            trackId,
            pureMusic,
            playing,
            String(lyrics, "currentTranslation"),
            String(lyrics, "nextTranslation"),
            Bool(lyrics, "translationMode"),
            !lyrics.TryGetProperty("animateTransition", out var animated) || animated.ValueKind != JsonValueKind.False,
            wordScan,
            scene);

        var styleModel = new CardLyricsStyle(
            NonEmpty(String(style, "fontFamily"), "Segoe UI Variable Text"),
            Clamp(Double(style, "layoutScalePercent", 100), 25, 400),
            Clamp(Double(style, "fontSize", 13), 5, 72),
            FontWeight(style, 600),
            NormalizeAlignment(String(style, "textAlignment")),
            String(style, "textShadow").Length > 0 &&
                !string.Equals(String(style, "textShadow"), "none", StringComparison.OrdinalIgnoreCase),
            Clamp(Double(style, "viewportDescenderBuffer", 2), 0, 24),
            Clamp(Double(style, "primaryOffsetY", 1), -12, 12),
            Clamp(Double(style, "secondaryOffsetY", 2), -12, 12));

        var appearance = new CardLyricsNativeAppearance(
            OptionalString(native, "fontFamily"),
            OptionalDouble(native, "fontSize") is { } nativeSize ? Clamp(nativeSize, 5, 72) : null,
            OptionalInteger(native, "fontWeight") is { } nativeWeight ? Clamp(nativeWeight, 100, 990) : null,
            OptionalBool(native, "fontItalic"),
            OptionalBool(native, "fontUnderline"),
            OptionalBool(native, "fontStrikeOut"));

        frame = new CardLyricsFrame(revision, trackId, available, playing, content, styleModel, appearance);
        return true;
    }

    private static string NormalizeAlignment(string value) => value switch
    {
        "Center" => "Center",
        "Right" => "Right",
        _ => "Left"
    };

    private static string String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? ""
            : "";

    private static string? OptionalString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()
            : null;

    private static bool Bool(JsonElement value, string name) => OptionalBool(value, name) == true;

    private static bool? OptionalBool(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var item))
            return null;
        return item.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static double Double(JsonElement value, string name, double fallback = 0) =>
        OptionalDouble(value, name) ?? fallback;

    private static double? OptionalDouble(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out var item) ||
            item.ValueKind != JsonValueKind.Number ||
            !item.TryGetDouble(out var number) ||
            !double.IsFinite(number))
            return null;
        return number;
    }

    private static int Integer(JsonElement value, string name, int fallback) =>
        OptionalInteger(value, name) ?? fallback;

    private static int? OptionalInteger(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out var item) ||
            item.ValueKind != JsonValueKind.Number)
            return null;
        if (item.TryGetInt32(out var number)) return number;
        if (item.TryGetDouble(out var decimalNumber) && double.IsFinite(decimalNumber))
            return (int)Math.Round(decimalNumber, MidpointRounding.AwayFromZero);
        return null;
    }

    private static int FontWeight(JsonElement value, int fallback)
    {
        if (OptionalInteger(value, "fontWeight") is { } number)
            return Clamp(number, 100, 990);
        return String(value, "fontWeight").Trim().ToLowerInvariant() switch
        {
            "light" => 300,
            "normal" => 400,
            "medium" => 500,
            "semibold" => 600,
            "bold" => 700,
            _ => fallback
        };
    }

    private static double? OptionalClamp01(JsonElement value, string name) =>
        OptionalDouble(value, name) is { } number ? Clamp(number, 0, 1) : null;

    private static bool TryLong(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.TryGetProperty(name, out var item) &&
            item.ValueKind == JsonValueKind.Number &&
            item.TryGetInt64(out number);
    }

    private static bool TryBool(JsonElement value, string name, out bool result)
    {
        result = false;
        if (!value.TryGetProperty(name, out var item) ||
            item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        result = item.GetBoolean();
        return true;
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = "";
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String)
            return false;
        result = item.GetString() ?? "";
        return true;
    }

    private static string NonEmpty(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;

    private static double Clamp01(double value) => Clamp(value, 0, 1);

    private static int Clamp(int value, int minimum, int maximum) =>
        Math.Clamp(value, minimum, maximum);
}
