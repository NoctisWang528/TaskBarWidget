using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using Microsoft.Data.Sqlite;

namespace TaskbarLyrics.App;

// Explicit diagnostic mode: no native host, media sessions, audio, user settings or window activation.
internal static class PackageProbe
{
    internal static int Run(string resultPath)
    {
        try
        {
            var adapter = new SettingsWindow(new AppSettings(), null!, () => Task.FromResult<CurrentTrackLyricsContext?>(null),
                () => throw new NotSupportedException(), (_, _, _) => throw new NotSupportedException(),
                (_, _, _) => throw new NotSupportedException(), () => { });
            var settings = adapter.ReadServiceSettings(new AppSettings { ShowLyricTranslation = true });
            if (!settings.GetProperty("showLyricTranslation").GetBoolean() || new WindowInteropHelper(adapter).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Headless settings adapter failed.");
            adapter.Close();
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
