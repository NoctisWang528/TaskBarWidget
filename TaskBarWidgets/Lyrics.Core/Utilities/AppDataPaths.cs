namespace TaskbarLyrics.Core.Utilities;

/// <summary>
/// Resolves the single product data root. The native host and media host configure
/// this exactly once before any settings, database, cache or log object is created.
/// </summary>
public static class AppDataPaths
{
    public const int LayoutVersion = 1;
    public const string DataDirectoryName = "Data";
    public const string LayoutFileName = "layout.json";

    private static string? _rootDirectory;

    public static string RootDirectory => _rootDirectory
        ?? throw new InvalidOperationException("The product data root has not been configured.");

    public static string LayoutFile => Path.Combine(RootDirectory, LayoutFileName);
    public static string ConfigDirectory => Path.Combine(RootDirectory, "config");
    public static string HistoryDirectory => Path.Combine(RootDirectory, "history");
    public static string LyricsDirectory => Path.Combine(RootDirectory, "lyrics");
    public static string CacheDirectory => Path.Combine(RootDirectory, "cache");
    public static string LyricCacheDirectory => Path.Combine(CacheDirectory, "lyrics");
    public static string SecretsDirectory => Path.Combine(RootDirectory, "secrets");
    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public static string MigrationDirectory => Path.Combine(RootDirectory, "migration");
    public static string MigrationArchiveDirectory => Path.Combine(MigrationDirectory, "archive");
    public static string MigrationReportFile => Path.Combine(MigrationDirectory, "report.json");
    public static string BackupsDirectory => Path.Combine(RootDirectory, "backups");
    public static string PendingRestoreFile => Path.Combine(MigrationDirectory, "pending-restore.zip");

    public static string NativeConfigPath => Path.Combine(ConfigDirectory, "app.ini");
    public static string BatteryConfigPath => Path.Combine(ConfigDirectory, "battery.ini");
    public static string PowerConfigPath => Path.Combine(ConfigDirectory, "power.ini");
    public static string WeatherConfigPath => Path.Combine(ConfigDirectory, "weather.ini");
    public static string MediaSettingsPath => Path.Combine(ConfigDirectory, "media.json");
    public static string WeatherCredentialPath => Path.Combine(SecretsDirectory, "weather-appkey.bin");
    public static string TrafficHistoryPath => Path.Combine(HistoryDirectory, "traffic.dat");
    public static string TrafficHistoryBackupPath => TrafficHistoryPath + ".bak";
    public static string ErrorLogPath => Path.Combine(LogsDirectory, "error.log");
    public static string NativeMediaLogPath => Path.Combine(LogsDirectory, "native-media.log");

    public static string ResolveForApplicationRoot(string applicationRoot) =>
        Path.Combine(Path.GetFullPath(applicationRoot), DataDirectoryName);

    public static void ConfigureForApplicationRoot(string applicationRoot) =>
        ConfigureForDataRoot(ResolveForApplicationRoot(applicationRoot));

    public static void ConfigureForDataRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("The product data root cannot be empty.", nameof(dataRoot));
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (!string.Equals(Path.GetFileName(full), DataDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The product data directory must be named Data.", nameof(dataRoot));
        }

        if (_rootDirectory is not null &&
            !string.Equals(_rootDirectory, full, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The product data root is already configured.");
        }

        _rootDirectory = full;
    }

    public static void EnsureDirectories()
    {
        DataLayout.VerifyNoReparsePoint(RootDirectory);
        foreach (var directory in new[]
                 {
                     RootDirectory, ConfigDirectory, HistoryDirectory, LyricsDirectory,
                     CacheDirectory, LyricCacheDirectory, SecretsDirectory, LogsDirectory,
                     MigrationDirectory, MigrationArchiveDirectory, BackupsDirectory
                 })
        {
            DataLayout.VerifyNoReparsePoint(directory);
            Directory.CreateDirectory(directory);
        }
    }

    internal static void ResetForTests() => _rootDirectory = null;
}
