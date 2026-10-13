using AFMediaBar.Classes.Settings;

namespace AFMediaBar.Classes.Services;

/// <summary>按应用来源执行既有允许列表判断，来源别名由调用方提供。 / Applies the existing source allow-list, using the supplied source identity rules.</summary>
public static class MediaSourceFilterPolicy
{
    public static bool IsAllowed(string? sourceId, SmtcSourceFilterSettings settings, Func<string, string>? normalizeSourceId = null)
    {
        settings = settings.Normalize();
        if (!settings.Enabled)
            return true;
        if (string.IsNullOrWhiteSpace(sourceId))
            return false;
        normalizeSourceId ??= static id => id;
        var normalized = normalizeSourceId(sourceId.Trim());
        return settings.AllowedSourceIds!.Any(allowed => string.Equals(
            normalizeSourceId(allowed), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
