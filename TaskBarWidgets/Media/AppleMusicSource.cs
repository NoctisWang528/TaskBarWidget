namespace TaskbarLyrics.App;

// Identity aliases only: metadata, artwork and commands continue through SMTC.
// Do not use substring/prefix matching for arbitrary packaged application IDs.
internal static class AppleMusicSource
{
    internal const string PackagedAppId = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";

    internal static bool IsAppleMusic(string? source)
    {
        var value = source?.Trim();
        return string.Equals(value, PackagedAppId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "AppleMusic.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "AppleMusic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Apple Music", StringComparison.OrdinalIgnoreCase);
    }
}
