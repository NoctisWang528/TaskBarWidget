using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskBarWidgets.Media.Updates;

namespace TaskbarLyrics.App;

// Explicit diagnostic mode: no native host, media sessions, audio, user settings or window activation.
internal static class PackageProbe
{
    internal static async Task<int> ValidateUpdateAsync(string archive, string metadataPath, string stage, string report)
    {
        try
        {
            if (new FileInfo(metadataPath).Length > 1024 * 1024) throw new InvalidDataException("Update metadata too large.");
            var metadata = UpdatePackage.ParseJson<UpdateMetadata>(await File.ReadAllBytesAsync(metadataPath));
            if (Directory.Exists(stage)) throw new IOException("Update probe requires a fresh stage.");
            await UpdatePackage.VerifyHash(archive, metadata.Sha256, CancellationToken.None);
            await UpdatePackage.ExtractAndVerify(archive, stage, metadata, CancellationToken.None);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = true, version = metadata.Version, noExecution = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = false, error = error.Message }));
            return 1;
        }
    }
    internal static int Run(string resultPath)
    {
        try
        {
            // The published helper must answer settings requests without a window.
            using var adapter = new MediaSettingsService(new AppSettings(), null,
                () => Task.FromResult<CurrentTrackLyricsContext?>(null),
                () => throw new NotSupportedException(), (_, _, _) => throw new NotSupportedException(),
                (_, _, _) => throw new NotSupportedException(), () => { }, _ => true);
            var settings = adapter.ReadServiceSettings(new AppSettings { ShowLyricTranslation = true });
            if (!settings.GetProperty("showLyricTranslation").GetBoolean() ||
                typeof(MediaSettingsService).IsSubclassOf(typeof(System.Windows.Window)))
                throw new InvalidOperationException("Headless settings adapter failed.");
            using var database = new SqliteConnection("Data Source=:memory:");
            database.Open();
            using var query = database.CreateCommand(); query.CommandText = "SELECT 42";
            if (Convert.ToInt32(query.ExecuteScalar()) != 42) throw new InvalidOperationException("SQLite native dependency did not load.");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { success = true,
                runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
                desktopAssembly = typeof(System.Windows.Window).Assembly.Location,
                settingsWithoutWindow = true, sqliteInMemory = true, systemFonts = FontCatalogService.GetOptions().Count,
                oldDefaultFont = AppSettings.NormalizeFontFamily(AppSettings.BundledFontFamily) }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { success = false, error = error.ToString() }));
            return 1;
        }
    }
}
