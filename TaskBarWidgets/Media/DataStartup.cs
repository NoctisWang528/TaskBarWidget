using System.IO;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.App;

internal static class DataStartup
{
    internal static DataMigrationResult Prepare(string dataRoot, string applicationRoot)
    {
        if (string.IsNullOrWhiteSpace(applicationRoot))
            throw new ArgumentException("The application root cannot be empty.", nameof(applicationRoot));

        var installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot));
        var expectedRoot = Path.Combine(installRoot, AppDataPaths.DataDirectoryName);
        var actualRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (!string.Equals(actualRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The product data root must be the Data directory under the install root.",
                nameof(dataRoot));

        AppDataPaths.ConfigureForDataRoot(actualRoot);
        DataLayout.VerifyNoReparsePoint(actualRoot);
        DataLayout.VerifyNoReparsePointsInTree(actualRoot);
        AppDataPaths.EnsureDirectories();
        VerifyWritable(actualRoot);
        StorageCommandService.ApplyPendingRestore(actualRoot);
        return Migration(actualRoot, installRoot).MigrateIfNeeded();
    }

    internal static DataMigrationService Migration(string dataRoot, string installRoot)
    {
        var roots = new List<LegacyRoot>
        {
            new(MigrationSourceKind.InstallLegacy, installRoot)
        };
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            roots.Add(new LegacyRoot(MigrationSourceKind.AppData, Path.Combine(appData, "TaskBarWidgets")));
        return new DataMigrationService(dataRoot, roots);
    }

    private static void VerifyWritable(string dataRoot)
    {
        var probe = Path.Combine(dataRoot, ".write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1, FileOptions.WriteThrough))
            {
                stream.WriteByte(0);
                stream.Flush(true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The product data directory is not writable: " + dataRoot, error);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException("The product data directory cannot remove its write probe.", error);
            }
        }
    }
}
