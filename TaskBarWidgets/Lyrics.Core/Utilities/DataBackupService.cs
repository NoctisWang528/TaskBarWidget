using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TaskbarLyrics.Core.Utilities;

public sealed record DataStatus(
    string DataRoot,
    int LayoutVersion,
    string LayoutState,
    string MigrationId,
    string? MigrationCompletedAtUtc,
    long PersonalBytes,
    long CacheBytes,
    long ArchiveBytes,
    bool CredentialPresent,
    bool CredentialDecryptable,
    bool PendingRestore,
    string? LastBackup,
    bool ContainsLegacyPlaintextCredential,
    IReadOnlyList<string> MigrationWarnings);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DataBackupService
{
    private const long MaxSqliteBackupBytes = 16L * 1024 * 1024 * 1024;

    private static readonly string[] PersonalFiles =
    [
        "layout.json",
        "config/app.ini",
        "config/battery.ini",
        "config/power.ini",
        "config/weather.ini",
        "config/media.json",
        "history/traffic.dat",
        "history/traffic.dat.bak"
    ];

    private static readonly string[] PersonalLyricFiles =
    [
        "user_data.db",
        "song_maps.db",
        "resolved-lyrics-v1.json",
        "user-lyric-bindings-v1.json"
    ];

    private static readonly string[] Excluded =
    [
        "cache/", "logs/", "migration/", "backups/", "secrets/ except weather-appkey.bin",
        "*.db-wal", "*.db-shm", "*.tmp"
    ];

    private readonly string _dataRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DataBackupService(string? dataRoot = null) => _dataRoot = Path.GetFullPath(dataRoot ?? AppDataPaths.RootDirectory);

    public async Task<string> CreateBackupAsync(string destination, string appVersion, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            DataLayout.VerifyNoReparsePoint(_dataRoot);
            destination = Path.GetFullPath(destination);
            if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Backup must be a .zip file.", nameof(destination));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) throw new IOException("Backup destination already exists.");

            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var staging = Path.Combine(Path.GetTempPath(), "tbw-backup-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                var manifest = new BackupManifest { AppVersion = appVersion, Excluded = Excluded.ToList() };
                foreach (var relative in PersonalFiles.Concat(PersonalLyricFiles.Select(name => "lyrics/" + name)))
                {
                    var source = DataLayout.ResolveUnder(_dataRoot, relative);
                    if (!File.Exists(source)) continue;
                    var staged = DataLayout.ResolveUnder(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    if (Path.GetExtension(source).Equals(".db", StringComparison.OrdinalIgnoreCase))
                    {
                        SnapshotSqlite(source, staged);
                        manifest.Files.Add(new BackupFile
                        {
                            Path = relative,
                            Bytes = new FileInfo(staged).Length,
                            Sha256 = DataLayout.Sha256(staged),
                            Method = "sqlite-online-backup"
                        });
                    }
                    else
                    {
                        await CopyStableAsync(source, staged, token);
                        manifest.Files.Add(new BackupFile
                        {
                            Path = relative,
                            Bytes = new FileInfo(staged).Length,
                            Sha256 = DataLayout.Sha256(staged),
                            Method = "copy"
                        });
                    }
                }

                var credential = Path.Combine(_dataRoot, "secrets", "weather-appkey.bin");
                if (File.Exists(credential))
                {
                    var relative = "secrets/weather-appkey.bin";
                    var staged = DataLayout.ResolveUnder(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    await CopyStableAsync(credential, staged, token);
                    manifest.ContainsCredentialCiphertext = true;
                    manifest.CredentialPortable = false;
                    manifest.Files.Add(new BackupFile
                    {
                        Path = relative,
                        Bytes = new FileInfo(staged).Length,
                        Sha256 = DataLayout.Sha256(staged),
                        Method = "copy"
                    });
                }

                DataLayout.WriteJson(Path.Combine(staging, "backup-manifest.json"), manifest);
                using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
                        archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                    }
                }
                File.Move(temporary, destination, false);
                return destination;
            }
            finally
            {
                TryDelete(temporary);
                TryDeleteTree(staging);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackupManifest> ValidateBackupAsync(string archivePath, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            using var archive = ZipFile.OpenRead(Path.GetFullPath(archivePath));
            var manifestEntry = archive.GetEntry("backup-manifest.json")
                ?? throw new InvalidDataException("Backup manifest is missing.");
            if (manifestEntry.Length is <= 0 or > 4 * 1024 * 1024)
                throw new InvalidDataException("Backup manifest is too large.");
            using var stream = manifestEntry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: token)
                ?? throw new InvalidDataException("Backup manifest is invalid.");
            if (manifest.ProtocolVersion != BackupManifest.CurrentVersion)
                throw new InvalidDataException("Unsupported backup format.");
            if (manifest.LayoutVersion != DataLayoutState.CurrentVersion)
                throw new InvalidDataException("Unsupported backup layout version.");
            if (manifest.CredentialPortable)
                throw new InvalidDataException("Backup credential portability must be false.");
            if (manifest.Files.Count > 8192)
                throw new InvalidDataException("Backup contains too many files.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                DataLayout.ValidateRelativePath(file.Path);
                if (!AllowedBackupPath(file.Path))
                    throw new InvalidDataException("Backup contains an unsupported path: " + file.Path);
                if (!paths.Add(file.Path))
                    throw new InvalidDataException("Backup contains duplicate paths: " + file.Path);
                var entry = archive.GetEntry(file.Path) ?? throw new InvalidDataException("Backup file is missing: " + file.Path);
                if (entry.FullName.EndsWith('/')) throw new InvalidDataException("Backup path is not a file: " + file.Path);
                if (entry.Length != file.Bytes)
                    throw new InvalidDataException("Backup file size mismatch: " + file.Path);
                await using var input = entry.Open();
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token))
                    .ToLowerInvariant();
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backup file hash mismatch: " + file.Path);
                if (file.Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                {
                    await using var ini = entry.Open();
                    if (entry.Length > 16 * 1024 * 1024)
                        throw new InvalidDataException("Backup INI file is too large: " + file.Path);
                    var bytes = new byte[checked((int)entry.Length)];
                    await ini.ReadExactlyAsync(bytes, token);
                    if (bytes.Length == 0) throw new InvalidDataException("Backup INI file is empty: " + file.Path);
                    if (file.Path is "config/battery.ini" or "config/power.ini" &&
                        bytes.Any(value => value > 0x7f))
                        throw new InvalidDataException("Battery and Power INI files must remain ASCII.");
                }
                else if (file.Path is "history/traffic.dat" or "history/traffic.dat.bak")
                {
                    await using var traffic = entry.Open();
                    if (!await ValidateTraffic(traffic, token))
                        throw new InvalidDataException("Backup traffic history is invalid.");
                }
                else if (file.Path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Length is <= 0 or > MaxSqliteBackupBytes)
                        throw new InvalidDataException("Backup SQLite file has an unsupported size: " + file.Path);
                    await ValidateSqliteEntryAsync(entry, token);
                }
                else if (file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Length > 64 * 1024 * 1024)
                        throw new InvalidDataException("Backup JSON file is too large: " + file.Path);
                    await using var json = entry.Open();
                    using var document = await JsonDocument.ParseAsync(json, cancellationToken: token);
                    if (file.Path == "layout.json")
                    {
                        var layout = document.RootElement.Deserialize<DataLayoutState>()
                            ?? throw new InvalidDataException("Invalid layout.json in backup.");
                        if (layout.LayoutVersion != DataLayoutState.CurrentVersion)
                            throw new InvalidDataException("Unsupported layout.json version in backup.");
                    }
                }
            }
            return manifest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public DataStatus GetStatus()
    {
        var layoutPath = Path.Combine(_dataRoot, "layout.json");
        var layout = File.Exists(layoutPath)
            ? DataLayout.ReadJson<DataLayoutState>(layoutPath)
            : new DataLayoutState();
        var credential = new WeatherCredentialStore(Path.Combine(_dataRoot, "secrets", "weather-appkey.bin"));
        var credentialPresent = credential.Exists;
        var credentialReadable = credentialPresent && credential.TryRead(out _);
        MigrationReport? report = null;
        var reportPath = Path.Combine(_dataRoot, "migration", "report.json");
        if (File.Exists(reportPath))
        {
            try { report = DataLayout.ReadJson<MigrationReport>(reportPath); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn("迁移报告无法读取：" + error.Message);
            }
        }
        return new DataStatus(
            _dataRoot,
            layout.LayoutVersion,
            layout.State,
            layout.MigrationId,
            layout.MigratedAtUtc?.ToString("o"),
            SizeOf("config") + SizeOf("history") + SizeOf("lyrics") +
            (credentialPresent ? new FileInfo(Path.Combine(_dataRoot, "secrets", "weather-appkey.bin")).Length : 0),
            SizeOf("cache"),
            SizeOf("migration/archive"),
            credentialPresent,
            credentialReadable,
            File.Exists(Path.Combine(_dataRoot, "migration", "pending-restore.zip")),
            Directory.Exists(Path.Combine(_dataRoot, "backups"))
                ? Directory.EnumerateFiles(Path.Combine(_dataRoot, "backups"), "*.zip")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null,
            report?.ContainsLegacyPlaintextCredential == true,
            report?.Warnings ?? []);
    }

    public void ClearCache()
    {
        var cache = Path.Combine(_dataRoot, "cache");
        if (!Directory.Exists(cache)) return;
        DataLayout.VerifyNoReparsePointsInTree(cache);
        foreach (var directory in Directory.EnumerateDirectories(cache))
            Directory.Delete(directory, true);
        foreach (var file in Directory.EnumerateFiles(cache))
            File.Delete(file);
    }

    private long SizeOf(string relative)
    {
        var path = DataLayout.ResolveUnder(_dataRoot, relative);
        if (File.Exists(path)) return new FileInfo(path).Length;
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
            : 0;
    }

    private static void SnapshotSqlite(string source, string target)
    {
        using var input = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        input.Open();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = target,
            Pooling = false
        }.ToString());
        destination.Open();
        input.BackupDatabase(destination);
        using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite integrity check failed.");
    }

    private static async Task<bool> ValidateTraffic(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true);
        var header = await reader.ReadLineAsync(token);
        if (header is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(header, "^lines:\\s*\"(?<count>\\d+)\"\\s*$"))
            return false;
        var expected = int.Parse(System.Text.RegularExpressions.Regex.Match(header, "\\d+").Value);
        var count = 0;
        while (await reader.ReadLineAsync(token) is not null) count++;
        return count == expected;
    }

    private static async Task ValidateSqliteEntryAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "tbw-backup-sqlite-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var input = entry.Open())
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, 64 * 1024, token);
                await output.FlushAsync(token);
                output.Flush(true);
            }

            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = temporary,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backup SQLite integrity check failed.");
            }
            catch (SqliteException error)
            {
                throw new InvalidDataException("Backup SQLite file is invalid.", error);
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task CopyStableAsync(string source, string target, CancellationToken token)
    {
        IOException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, 64 * 1024, token);
                await output.FlushAsync(token);
                output.Flush(true);
                return;
            }
            catch (IOException error) when (attempt < 4)
            {
                last = error;
                if (File.Exists(target)) File.Delete(target);
                await Task.Delay(150, token);
            }
        }
        throw new IOException("Stable data file copy failed: " + source, last);
    }

    private static bool AllowedBackupPath(string path)
    {
        path = path.Replace('\\', '/');
        if (path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Equals("layout.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("secrets/weather-appkey.bin", StringComparison.OrdinalIgnoreCase))
            return true;
        return path.StartsWith("config/", StringComparison.OrdinalIgnoreCase) && path.Count(character => character == '/') == 1 ||
               path.StartsWith("history/", StringComparison.OrdinalIgnoreCase) && path.Count(character => character == '/') == 1 ||
               path.StartsWith("lyrics/", StringComparison.OrdinalIgnoreCase) && path.Count(character => character == '/') == 1;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
