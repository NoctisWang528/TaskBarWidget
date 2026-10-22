using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using TaskbarLyrics.Core.Utilities;
using Xunit;

namespace TaskbarLyrics.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DataLayoutTests
{
    [Fact]
    public async Task BackupContainsOnlyPersonalDataAndVerifiesItsManifest()
    {
        var root = TempRoot();
        var data = Path.Combine(root, "Data");
        try
        {
            Configure(data);
            Write(Path.Combine(data, "config", "app.ini"), "[general]\r\nlanguage=1\r\n");
            Write(Path.Combine(data, "history", "traffic.dat"), "lines: \"0\"\r\n");
            Write(Path.Combine(data, "lyrics", "resolved-lyrics-v1.json"), "{\"entries\":{}}");
            Write(Path.Combine(data, "cache", "lyrics", "lyric-pipeline-raw-v1.json"), "{\"cached\":true}");
            Write(Path.Combine(data, "logs", "error.log"), "do not back up");
            Write(Path.Combine(data, "migration", "archive", "old", "WeatherPro.ini"),
                "[hfw]\r\nAppKey=legacy-plaintext\r\n");
            DataLayout.WriteJson(Path.Combine(data, "layout.json"),
                new DataLayoutState { State = "ready" });

            var archive = Path.Combine(root, "backup.zip");
            var service = new DataBackupService(data);
            await service.CreateBackupAsync(archive, "test", CancellationToken.None);
            var manifest = await service.ValidateBackupAsync(archive, CancellationToken.None);

            Assert.Contains(manifest.Files, file => file.Path == "config/app.ini");
            Assert.Contains(manifest.Files, file => file.Path == "history/traffic.dat");
            Assert.Contains(manifest.Files, file => file.Path == "lyrics/resolved-lyrics-v1.json");
            Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("cache/", StringComparison.Ordinal));
            Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("logs/", StringComparison.Ordinal));
            Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("migration/", StringComparison.Ordinal));
            Assert.False(manifest.CredentialPortable);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MigrationCopiesLegacyFilesAndArchivesSources()
    {
        var root = TempRoot();
        var install = Path.Combine(root, "install");
        var data = Path.Combine(install, "Data");
        try
        {
            Configure(data);
            Write(Path.Combine(install, "config.ini"), "[general]\r\nlanguage=2\r\n");
            Write(Path.Combine(install, "plugins", "Battery.dll.ini"),
                "[config]\r\nbattery_type=2\r\nshow_percent=1\r\n");
            Write(Path.Combine(install, "history_traffic.dat"), "lines: \"1\"\r\n2026/10/22 1/2\r\n");
            Write(Path.Combine(install, "global_cfg.ini"), "[config]\r\nportable_mode=true\r\n");
            Write(Path.Combine(install, "Data", "media-settings.json"), "{\"FontSize\":42}");
            Write(Path.Combine(install, "Data", "database", "resolved-lyrics-v1.json"), "{\"track\":\"new-layout\"}");
            Write(Path.Combine(install, "Data", "database", "lyric-pipeline-raw-v1.json"), "{\"cached\":true}");
            Directory.CreateDirectory(Path.Combine(install, "unrelated-empty-directory"));

            var service = new DataMigrationService(data,
                [new LegacyRoot(MigrationSourceKind.InstallLegacy, install)]);
            var result = service.MigrateIfNeeded();

            Assert.True(result.Migrated);
            Assert.Equal("[general]\r\nlanguage=2\r\n", File.ReadAllText(Path.Combine(data, "config", "app.ini")));
            Assert.Contains("battery_type=2", File.ReadAllText(Path.Combine(data, "config", "battery.ini")));
            Assert.True(File.Exists(Path.Combine(data, "history", "traffic.dat")));
            Assert.Equal("{\"FontSize\":42}", File.ReadAllText(Path.Combine(data, "config", "media.json")));
            Assert.Contains("new-layout", File.ReadAllText(Path.Combine(data, "lyrics", "resolved-lyrics-v1.json")));
            Assert.Contains("cached", File.ReadAllText(Path.Combine(data, "cache", "lyrics", "lyric-pipeline-raw-v1.json")));
            Assert.False(File.Exists(Path.Combine(install, "config.ini")));
            Assert.False(File.Exists(Path.Combine(install, "global_cfg.ini")));
            Assert.False(Directory.Exists(Path.Combine(install, "Data", "database")));
            Assert.True(Directory.Exists(Path.Combine(install, "unrelated-empty-directory")));
            Assert.True(Directory.Exists(result.ArchiveDirectory));
            Assert.Equal("ready", DataLayout.ReadJson<DataLayoutState>(AppDataPaths.LayoutFile).State);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MigrationRejectsInvalidJsonWithoutCommitting()
    {
        var root = TempRoot();
        var install = Path.Combine(root, "install");
        var data = Path.Combine(install, "Data");
        try
        {
            Configure(data);
            Write(Path.Combine(install, "config.ini"), "[general]\r\nlanguage=2\r\n");
            Write(Path.Combine(install, "Data", "media-settings.json"), "{ invalid");
            var service = new DataMigrationService(data,
                [new LegacyRoot(MigrationSourceKind.InstallLegacy, install)]);

            Assert.Throws<JsonException>(() => service.MigrateIfNeeded());
            Assert.False(File.Exists(Path.Combine(data, "config", "app.ini")));
            Assert.True(File.Exists(Path.Combine(install, "config.ini")));
            Assert.NotEqual("ready", DataLayout.ReadJson<DataLayoutState>(AppDataPaths.LayoutFile).State);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MigrationNeverOverwritesAnExistingTarget()
    {
        var root = TempRoot();
        var install = Path.Combine(root, "install");
        var data = Path.Combine(install, "Data");
        try
        {
            Configure(data);
            Write(Path.Combine(data, "config", "app.ini"), "[general]\r\nlanguage=9\r\n");
            Write(Path.Combine(install, "config.ini"), "[general]\r\nlanguage=2\r\n");
            var service = new DataMigrationService(data,
                [new LegacyRoot(MigrationSourceKind.InstallLegacy, install)]);

            var result = service.MigrateIfNeeded();

            Assert.True(result.Migrated);
            Assert.Equal("[general]\r\nlanguage=9\r\n", File.ReadAllText(Path.Combine(data, "config", "app.ini")));
            Assert.False(File.Exists(Path.Combine(install, "config.ini")));
            Assert.True(File.Exists(Path.Combine(result.ArchiveDirectory, "install-legacy", "config.ini")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MigrationArchivesWeatherPlaintextAfterCommittingProtectedCredential()
    {
        var root = TempRoot();
        var install = Path.Combine(root, "install");
        var data = Path.Combine(install, "Data");
        try
        {
            Configure(data);
            Write(Path.Combine(install, "plugins", "WeatherPro.ini"),
                "[config]\r\ncity_no=101010100\r\n[hfw]\r\nAppKey=test-key-that-must-not-remain\r\n");
            var service = new DataMigrationService(data,
                [new LegacyRoot(MigrationSourceKind.InstallLegacy, install)]);

            var result = service.MigrateIfNeeded();

            Assert.True(result.Migrated);
            Assert.True(File.Exists(Path.Combine(data, "secrets", "weather-appkey.bin")));
            Assert.DoesNotContain("AppKey=test-key-that-must-not-remain",
                File.ReadAllText(Path.Combine(data, "config", "weather.ini")), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(result.Warnings, warning => warning.Contains("明文", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(result.ArchiveDirectory, "install-legacy", "plugins", "WeatherPro.ini")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MigrationKeepsVerifiedArchiveWhenLockedSourceCannotBeDeleted()
    {
        var root = TempRoot();
        var install = Path.Combine(root, "install");
        var data = Path.Combine(install, "Data");
        try
        {
            Configure(data);
            var source = Path.Combine(install, "config.ini");
            Write(source, "[general]\r\nlanguage=3\r\n");
            using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var service = new DataMigrationService(data,
                    [new LegacyRoot(MigrationSourceKind.InstallLegacy, install)]);
                var result = service.MigrateIfNeeded();

                Assert.Equal("[general]\r\nlanguage=3\r\n", File.ReadAllText(Path.Combine(data, "config", "app.ini")));
                Assert.True(File.Exists(source));
                Assert.True(File.Exists(Path.Combine(result.ArchiveDirectory, "install-legacy", "config.ini")));
                Assert.Contains(result.Warnings, warning => warning.Contains("源文件仍被占用", StringComparison.Ordinal));
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MigrationSanitizesExistingActiveWeatherConfigAndPreservesItsKey()
    {
        var root = TempRoot();
        var install = Path.Combine(root, "install");
        var data = Path.Combine(install, "Data");
        try
        {
            Configure(data);
            Write(Path.Combine(install, "config.ini"), "[general]\r\nlanguage=4\r\n");
            Write(Path.Combine(data, "config", "weather.ini"),
                "[config]\r\ncity_no=101010100\r\n[hfw]\r\nAppKey=existing-active-key\r\n");

            var service = new DataMigrationService(data,
                [new LegacyRoot(MigrationSourceKind.InstallLegacy, install)]);
            var result = service.MigrateIfNeeded();

            Assert.True(result.Migrated);
            Assert.DoesNotContain("AppKey", File.ReadAllText(Path.Combine(data, "config", "weather.ini")),
                StringComparison.OrdinalIgnoreCase);
            var credential = new WeatherCredentialStore(Path.Combine(data, "secrets", "weather-appkey.bin"));
            Assert.True(credential.TryRead(out var appKey));
            Assert.Equal("existing-active-key", appKey);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task BackupValidationRejectsSqliteSidecars()
    {
        var root = TempRoot();
        var archive = Path.Combine(root, "sidecar.zip");
        try
        {
            CreateBackupArchive(archive, "lyrics/user_data.db-wal", "not a backup file"u8.ToArray());
            var service = new DataBackupService(Path.Combine(root, "Data"));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ValidateBackupAsync(archive, CancellationToken.None));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task BackupValidationRejectsCorruptSqliteDatabase()
    {
        var root = TempRoot();
        var archive = Path.Combine(root, "corrupt-db.zip");
        try
        {
            CreateBackupArchive(archive, "lyrics/user_data.db", "not a sqlite database"u8.ToArray());
            var service = new DataBackupService(Path.Combine(root, "Data"));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ValidateBackupAsync(archive, CancellationToken.None));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RelativePathValidationRejectsWindowsAliasesAndDeviceNames()
    {
        foreach (var path in new[]
                 {
                     "config/app.ini.", "config/app.ini ", "config/CON", "lyrics/NUL.json",
                     "config/COM1.ini", "config/LPT9", "config/..\\app.ini"
                 })
            Assert.Throws<InvalidDataException>(() => DataLayout.ValidateRelativePath(path));

        DataLayout.ValidateRelativePath("config/weather.ini");
        DataLayout.ValidateRelativePath("lyrics/user_data.db");
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-data-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Configure(string data)
    {
        AppDataPaths.ResetForTests();
        AppDataPaths.ConfigureForDataRoot(data);
        AppDataPaths.EnsureDirectories();
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void CreateBackupArchive(string path, string relative, byte[] bytes)
    {
        var manifest = new BackupManifest
        {
            Files =
            [
                new BackupFile
                {
                    Path = relative,
                    Bytes = bytes.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                }
            ]
        };
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var manifestStream = archive.CreateEntry("backup-manifest.json").Open())
            JsonSerializer.Serialize(manifestStream, manifest);
        using var output = archive.CreateEntry(relative).Open();
        output.Write(bytes);
    }

    private static void Cleanup(string root)
    {
        AppDataPaths.ResetForTests();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
