using System.IO;
using System.Text.Json;

using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.App;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public SettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public AppSettings Load(bool systemUsesLightTheme = true)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppSettings.CreateDefault(systemUsesLightTheme);
            }

            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            using var document = JsonDocument.Parse(json);
            ApplyMicaSettings(document.RootElement, settings);
            MigrateLegacySpectrumSettings(document.RootElement, settings);
            MigrateSpectrumAudioAccess(document.RootElement, settings);
            MigrateTaskbarPresentation(document.RootElement, settings);
            MigrateQuickCards(document.RootElement, settings);
            settings.NormalizePlayerSources();
            settings.NormalizeLyricsLayout();
            settings.NormalizeDisplaySelection();
            settings.NormalizeWindowLayout();
            settings.NormalizeQuickCards();
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Failed to load settings from '{_filePath}': {exception.Message}");
            return AppSettings.CreateDefault(systemUsesLightTheme);
        }
    }

    public bool Save(AppSettings settings)
    {
        string? temporaryPath = null;
        try
        {
            settings.NormalizePlayerSources();
            settings.NormalizeLyricsLayout();
            settings.NormalizeDisplaySelection();
            settings.NormalizeWindowLayout();
            settings.NormalizeQuickCards();
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var settingsDirectory = string.IsNullOrWhiteSpace(directory)
                ? AppContext.BaseDirectory
                : directory;
            temporaryPath = Path.Combine(
                settingsDirectory,
                $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            var serializedSettings = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(serializedSettings);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Error($"Failed to save settings to '{_filePath}': {exception}");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Failed to remove temporary settings file '{temporaryPath}': {exception.Message}");
                }
            }
        }
    }

    private static void MigrateLegacySpectrumSettings(JsonElement root, AppSettings settings)
    {
        if (!root.TryGetProperty("EnableSpectrum", out var enabledElement) ||
            enabledElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return;
        }

        if (!enabledElement.GetBoolean())
        {
            settings.SpectrumDisplayMode = SpectrumDisplayMode.Disabled;
        }
    }

    private static void MigrateQuickCards(JsonElement root, AppSettings settings)
    {
        if (root.TryGetProperty(nameof(AppSettings.QuickCardOrder), out _))
        {
            return;
        }

        // Existing installations receive the new weather card enabled at the top;
        // fresh installations use AppSettings' disabled default.
        settings.QuickCardEnabled ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        settings.QuickCardEnabled["weather"] = true;
    }

    private static void ApplyMicaSettings(JsonElement root, AppSettings settings)
    {
        // The switch arrived with the vivid material. A file without it was tuned
        // against the old, almost opaque wash, so it receives the vivid defaults
        // once; the stored switch then keeps whatever the user tunes later.
        if (!root.TryGetProperty(nameof(AppSettings.CardMicaVivid), out var vivid) ||
            vivid.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            settings.CardMicaVivid = AppSettings.DefaultCardMicaVivid;
            settings.CardMicaTintOpacity = AppSettings.DefaultCardMicaTintOpacity;
            settings.CardMicaLuminosityOpacity = AppSettings.DefaultCardMicaLuminosityOpacity;
            settings.CardMicaDarkTintOpacity = AppSettings.DefaultCardMicaDarkTintOpacity;
            settings.CardMicaDarkLuminosityOpacity = AppSettings.DefaultCardMicaDarkLuminosityOpacity;
            return;
        }

        settings.CardMicaVivid = vivid.GetBoolean();
        var hasLegacyTint = TryReadDouble(
            root, nameof(AppSettings.CardMicaTintOpacity), out var legacyTint);
        var hasLegacyLuminosity = TryReadDouble(
            root, nameof(AppSettings.CardMicaLuminosityOpacity), out var legacyLuminosity);

        if (hasLegacyTint && hasLegacyLuminosity)
        {
            if (legacyTint == 1.0 && legacyLuminosity == 1.0)
            {
                settings.CardMicaTintOpacity = AppSettings.DefaultCardMicaTintOpacity;
                settings.CardMicaLuminosityOpacity = AppSettings.DefaultCardMicaLuminosityOpacity;
                settings.CardMicaDarkTintOpacity = AppSettings.DefaultCardMicaDarkTintOpacity;
                settings.CardMicaDarkLuminosityOpacity = AppSettings.DefaultCardMicaDarkLuminosityOpacity;
            }
            else
            {
                settings.CardMicaTintOpacity = ClampMicaTint(legacyTint);
                settings.CardMicaLuminosityOpacity = ClampMicaLuminosity(legacyLuminosity);
                // These were the colors the old implicit dark offset produced.
                settings.CardMicaDarkTintOpacity = ClampMicaTint(legacyTint - 0.02);
                settings.CardMicaDarkLuminosityOpacity = ClampMicaLuminosity(legacyLuminosity - 0.02);
            }
        }
        else
        {
            settings.CardMicaTintOpacity = AppSettings.DefaultCardMicaTintOpacity;
            settings.CardMicaLuminosityOpacity = AppSettings.DefaultCardMicaLuminosityOpacity;
            settings.CardMicaDarkTintOpacity = AppSettings.DefaultCardMicaDarkTintOpacity;
            settings.CardMicaDarkLuminosityOpacity = AppSettings.DefaultCardMicaDarkLuminosityOpacity;
        }

        // Explicit split-theme fields always win over both defaults and the
        // legacy two-field migration. They are only written on the next save.
        if (TryReadDouble(root, nameof(AppSettings.CardMicaDarkTintOpacity), out var darkTint))
            settings.CardMicaDarkTintOpacity = ClampMicaTint(darkTint);
        if (TryReadDouble(root, nameof(AppSettings.CardMicaDarkLuminosityOpacity), out var darkLuminosity))
            settings.CardMicaDarkLuminosityOpacity = ClampMicaLuminosity(darkLuminosity);
    }

    private static double ClampMicaTint(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, AppSettings.MinimumCardMicaTintOpacity, AppSettings.MaximumCardMicaTintOpacity)
            : AppSettings.DefaultCardMicaTintOpacity;

    private static double ClampMicaLuminosity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, AppSettings.MinimumCardMicaLuminosityOpacity, AppSettings.MaximumCardMicaLuminosityOpacity)
            : AppSettings.DefaultCardMicaLuminosityOpacity;

    private static void MigrateSpectrumAudioAccess(JsonElement root, AppSettings settings)
    {
        if (root.TryGetProperty(nameof(AppSettings.SpectrumAudioAccessGranted), out _))
        {
            return;
        }

        settings.SpectrumAudioAccessGranted = false;
        settings.SpectrumDisplayMode = SpectrumDisplayMode.Disabled;
    }

    private static void MigrateTaskbarPresentation(JsonElement root, AppSettings settings)
    {
        if (TryReadBoolean(root, nameof(AppSettings.UseFloatingWindow), out _))
        {
            return;
        }

        if (!TryReadBoolean(root, "TaskbarEmbeddingEnabled", out var legacyEmbeddingEnabled))
        {
            // New installations intentionally keep AppSettings' embedded default.
            return;
        }

        settings.UseFloatingWindow = !legacyEmbeddingEnabled;
        if (!legacyEmbeddingEnabled)
        {
            return;
        }

        if (TryReadDouble(root, "EmbeddedTaskbarWidth", out var embeddedWidth))
        {
            settings.WindowWidth = ConvertLegacyEmbeddedWidthToBaseWidth(
                embeddedWidth,
                settings.LyricsLayoutScalePercent);
        }

        if (TryReadDouble(root, "EmbeddedTaskbarHorizontalOffset", out var horizontalOffset))
        {
            settings.XOffset = horizontalOffset;
        }

        if (TryReadDouble(root, "EmbeddedTaskbarVerticalOffset", out var verticalOffset))
        {
            settings.YOffset = verticalOffset;
        }
    }

    private static double ConvertLegacyEmbeddedWidthToBaseWidth(
        double legacyEffectiveWidth,
        double scalePercent)
    {
        var scale = AppSettings.ClampLyricsLayoutScalePercent(scalePercent) / 100.0;
        var effectiveWidth = double.IsFinite(legacyEffectiveWidth)
            ? legacyEffectiveWidth
            : AppSettings.DefaultWindowWidth;
        return Math.Clamp(
            effectiveWidth / scale,
            AppSettings.MinimumWindowWidth,
            AppSettings.MaximumWindowWidth);
    }

    private static bool TryReadBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        return element.ValueKind == JsonValueKind.String &&
            bool.TryParse(element.GetString(), out value);
    }

    private static bool TryReadDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value))
        {
            return true;
        }

        return element.ValueKind == JsonValueKind.String &&
            double.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value) &&
            double.IsFinite(value);
    }
}
