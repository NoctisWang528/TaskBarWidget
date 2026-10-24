using System.IO;
using Microsoft.Data.Sqlite;
using TaskbarLyrics.Core.Utilities;
using Xunit;

namespace TaskbarLyrics.App.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DataStorageTests
{
    [Fact]
    public async Task RestoreReplacesPreRestoreSnapshotAndClearsSqliteSidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-storage-tests-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "install", "Data");
        var database = Path.Combine(data, "lyrics", "user_data.db");
        var config = Path.Combine(data, "config", "app.ini");
        var backup = Path.Combine(root, "restore-source.zip");
        try
        {
            AppDataPaths.ResetForTests();
            AppDataPaths.ConfigureForDataRoot(data);
            AppDataPaths.EnsureDirectories();
            DataLayout.WriteJson(AppDataPaths.LayoutFile, new DataLayoutState { State = "ready" });
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "[general]\r\nlanguage=1\r\n");
            SetDatabaseValue(database, "backup");

            var backups = new DataBackupService(data);
            await backups.CreateBackupAsync(backup, "test", CancellationToken.None);

            File.WriteAllText(config, "[general]\r\nlanguage=2\r\n");
            SetDatabaseValue(database, "current");
            File.WriteAllBytes(database + "-wal", []);
            File.WriteAllBytes(database + "-shm", []);
            File.Copy(backup, AppDataPaths.PendingRestoreFile, true);

            StorageCommandService.ApplyPendingRestore(data);

            Assert.Equal("[general]\r\nlanguage=1\r\n", File.ReadAllText(config));
            Assert.Equal("backup", ReadDatabaseValue(database));
            Assert.False(File.Exists(database + "-wal"));
            Assert.False(File.Exists(database + "-shm"));
            Assert.False(File.Exists(AppDataPaths.PendingRestoreFile));
            Assert.True(File.Exists(Path.Combine(data, "backups", "pre-restore-latest.zip")));

            File.WriteAllText(config, "[general]\r\nlanguage=3\r\n");
            SetDatabaseValue(database, "second-current");
            File.Copy(backup, AppDataPaths.PendingRestoreFile, true);

            StorageCommandService.ApplyPendingRestore(data);

            Assert.Equal("[general]\r\nlanguage=1\r\n", File.ReadAllText(config));
            Assert.Equal("backup", ReadDatabaseValue(database));
            Assert.True(File.Exists(Path.Combine(data, "backups", "pre-restore-latest.zip")));
        }
        finally
        {
            AppDataPaths.ResetForTests();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void SetDatabaseValue(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS state(value TEXT); DELETE FROM state; INSERT INTO state(value) VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string ReadDatabaseValue(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM state;";
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
}
