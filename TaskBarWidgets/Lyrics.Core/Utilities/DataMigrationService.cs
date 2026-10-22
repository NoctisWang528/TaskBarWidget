using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TaskbarLyrics.Core.Utilities;

public sealed record LegacyRoot(MigrationSourceKind Kind, string Directory);

public sealed record DataMigrationResult(
    bool Migrated,
    string MigrationId,
    string ArchiveDirectory,
    int Copied,
    int Conflicts,
    IReadOnlyList<string> Warnings);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DataMigrationService
{
    private static readonly (string Legacy, string Target)[] FileMap =
    [
        ("config.ini", "config/app.ini"),
        ("plugins/Battery.dll.ini", "config/battery.ini"),
        ("plugins/PowerMonPlugin.dll.ini", "config/power.ini"),
        ("plugins/WeatherPro.ini", "config/weather.ini"),
        ("history_traffic.dat", "history/traffic.dat"),
        ("history_traffic.dat.bak", "history/traffic.dat.bak"),
        ("Data/media-settings.json", "config/media.json"),
        ("media-settings.json", "config/media.json"),
        ("Data/database/user_data.db", "lyrics/user_data.db"),
        ("database/user_data.db", "lyrics/user_data.db"),
        ("Data/database/song_maps.db", "lyrics/song_maps.db"),
        ("database/song_maps.db", "lyrics/song_maps.db"),
        ("Data/database/resolved-lyrics-v1.json", "lyrics/resolved-lyrics-v1.json"),
        ("database/resolved-lyrics-v1.json", "lyrics/resolved-lyrics-v1.json"),
        ("Data/database/user-lyric-bindings-v1.json", "lyrics/user-lyric-bindings-v1.json"),
        ("database/user-lyric-bindings-v1.json", "lyrics/user-lyric-bindings-v1.json"),
        ("Data/database/lyric-pipeline-raw-v1.json", "cache/lyrics/lyric-pipeline-raw-v1.json"),
        ("database/lyric-pipeline-raw-v1.json", "cache/lyrics/lyric-pipeline-raw-v1.json"),
        ("Data/database/lyric-pipeline-parsed-v1.json", "cache/lyrics/lyric-pipeline-parsed-v1.json"),
        ("database/lyric-pipeline-parsed-v1.json", "cache/lyrics/lyric-pipeline-parsed-v1.json"),
        ("Data/database/smtc-generic-lyrics.json", "cache/lyrics/smtc-generic-lyrics.json"),
        ("database/smtc-generic-lyrics.json", "cache/lyrics/smtc-generic-lyrics.json"),
        ("error.log", "logs/error.log"),
        ("native-media.log", "logs/native-media.log")
    ];

    private static readonly string[] LegacySidecars =
    [
        "Data/database/user_data.db-wal",
        "Data/database/user_data.db-shm",
        "Data/database/song_maps.db-wal",
        "Data/database/song_maps.db-shm",
        "database/user_data.db-wal",
        "database/user_data.db-shm",
        "database/song_maps.db-wal",
        "database/song_maps.db-shm"
    ];

    private readonly string _dataRoot;
    private readonly LegacyRoot[] _roots;
    private readonly Func<string, string?> _credentialFromWeatherIni;
    private readonly HashSet<string> _stagedTargets = new(StringComparer.OrdinalIgnoreCase);

    public DataMigrationService(
        string dataRoot,
        IEnumerable<LegacyRoot> roots,
        Func<string, string?>? credentialFromWeatherIni = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _roots = roots.Where(root => !string.IsNullOrWhiteSpace(root.Directory))
            .Select(root => root with { Directory = Path.GetFullPath(root.Directory) })
            .Where(root => !string.Equals(root.Directory, _dataRoot, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(root => root.Directory + "|" + root.Kind)
            .ToArray();
        _credentialFromWeatherIni = credentialFromWeatherIni ?? ReadWeatherKey;
    }

    public bool HasLegacyCandidates()
    {
        if (_roots.Any(root =>
                Directory.Exists(root.Directory) &&
                (FileMap.Any(item => File.Exists(Path.Combine(root.Directory,
                    item.Legacy.Replace('/', Path.DirectorySeparatorChar)))) ||
                 File.Exists(Path.Combine(root.Directory, "global_cfg.ini")))))
            return true;

        try
        {
            return File.Exists(AppDataPaths.WeatherConfigPath) &&
                   !string.IsNullOrWhiteSpace(ReadWeatherKey(AppDataPaths.WeatherConfigPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn("无法检查活动天气配置中的旧明文密钥：" + error.Message);
            return false;
        }
    }

    public DataMigrationResult MigrateIfNeeded()
    {
        AppDataPaths.ConfigureForDataRoot(_dataRoot);
        AppDataPaths.EnsureDirectories();
        DataLayout.VerifyNoReparsePoint(_dataRoot);
        var existed = File.Exists(AppDataPaths.LayoutFile);
        var layout = DataLayout.LoadOrInitialize();
        if (!existed && !HasLegacyCandidates())
        {
            layout.State = "ready";
            DataLayout.Save(layout);
        }
        if (layout.State == "ready")
        {
            CleanupStaleMigrationBackups();
            return new DataMigrationResult(false, layout.MigrationId, "", 0, 0, []);
        }
        if (layout.State is not ("initializing" or "migration-requested"))
            throw new InvalidDataException("Data/layout.json contains an unsupported migration state: " + layout.State);

        return Migrate(layout, existed);
    }

    public DataMigrationResult Migrate(DataLayoutState layout, bool layoutExisted = true)
    {
        AppDataPaths.ConfigureForDataRoot(_dataRoot);
        var migrationId = DataLayout.NewMigrationId();
        var staging = Path.Combine(AppDataPaths.MigrationDirectory, ".staging-" + migrationId);
        var archive = Path.Combine(AppDataPaths.MigrationArchiveDirectory, migrationId);
        var report = new MigrationReport { MigrationId = migrationId, ArchiveDirectory = archive };
        var warnings = new List<string>();
        var copied = 0;
        var conflicts = 0;
        var committed = false;
        var targets = new Dictionary<string, (string? Backup, bool Existed)>(StringComparer.OrdinalIgnoreCase);
        var previousLayout = new DataLayoutState
        {
            LayoutVersion = layout.LayoutVersion,
            State = layout.State,
            CreatedAtUtc = layout.CreatedAtUtc,
            MigratedAtUtc = layout.MigratedAtUtc,
            MigrationId = layout.MigrationId
        };
        try
        {
            _stagedTargets.Clear();
            Directory.CreateDirectory(staging);
            foreach (var root in _roots)
            {
                MigrateRoot(root, staging, archive, targets, report, warnings, ref copied, ref conflicts);
            }

            MigrateCredential(staging, targets, report, warnings);
            committed = true;
            Commit(staging, targets);
            SanitizeActiveWeatherTarget(staging, targets, report, warnings);

            MoveLegacySourcesToArchive(archive, warnings);
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            report.Files = report.Files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            report.Warnings = warnings;
            DataLayout.WriteJson(AppDataPaths.MigrationReportFile, report);
            layout.State = "ready";
            layout.MigrationId = migrationId;
            layout.MigratedAtUtc = DateTimeOffset.UtcNow;
            DataLayout.Save(layout);
            CleanupTargetBackups(targets);
            CleanupOldArchives(archive);
            return new DataMigrationResult(true, migrationId, archive, copied, conflicts, warnings);
        }
        catch
        {
            if (committed)
            {
                RestoreBackups(targets);
                RestoreArchivedSources(archive);
                TryRestoreLayout(previousLayout, layoutExisted);
            }
            throw;
        }
        finally
        {
            TryDeleteTree(staging);
        }
    }

    private void MigrateRoot(
        LegacyRoot root,
        string staging,
        string archive,
        Dictionary<string, (string? Backup, bool Existed)> targets,
        MigrationReport report,
        List<string> warnings,
        ref int copied,
        ref int conflicts)
    {
        if (!Directory.Exists(root.Directory)) return;
        DataLayout.VerifyNoReparsePoint(root.Directory);
        foreach (var (legacy, targetRelative) in FileMap)
        {
            var source = Path.Combine(root.Directory, legacy.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) continue;
            DataLayout.VerifyNoReparsePoint(source);
            var target = DataLayout.ResolveUnder(_dataRoot, targetRelative);
            var staged = DataLayout.ResolveUnder(staging, targetRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);

            if (File.Exists(target))
            {
                Validate(targetRelative, source);
                Validate(targetRelative, target);
                if (DataLayout.Sha256(target) == DataLayout.Sha256(source))
                {
                    report.Files.Add(new MigrationCopy(root.Directory, targetRelative,
                        DataLayout.Sha256(source), new FileInfo(source).Length, false, "kept-target"));
                    continue;
                }

                conflicts++;
                ArchiveFile(source, root.Kind, legacy, archive, targetRelative);
                report.Files.Add(new MigrationCopy(root.Directory, targetRelative,
                    DataLayout.Sha256(source), new FileInfo(source).Length, true, "archived"));
                warnings.Add("目标文件已存在，保留新数据并归档旧文件：" + targetRelative);
                continue;
            }
            if (Directory.Exists(target))
                throw new InvalidDataException("A directory occupies the target file path: " + targetRelative);
            if (_stagedTargets.Contains(targetRelative))
            {
                conflicts++;
                ArchiveFile(source, root.Kind, legacy, archive, targetRelative);
                report.Files.Add(new MigrationCopy(root.Directory, targetRelative,
                    DataLayout.Sha256(source), new FileInfo(source).Length, true, "archived"));
                warnings.Add("多个旧来源包含同一文件，已采用较高优先级来源并归档其余文件：" + targetRelative);
                continue;
            }

            if (targetRelative is "lyrics/user_data.db" or "lyrics/song_maps.db")
            {
                SnapshotSqlite(source, staged);
            }
            else
            {
                File.Copy(source, staged, true);
            }
            if (targetRelative == "config/weather.ini")
                SanitizeWeatherIni(staged);

            Validate(targetRelative, staged);
            var sourceHash = DataLayout.Sha256(staged);
            var bytes = new FileInfo(staged).Length;
            targets[target] = (null, false);
            _stagedTargets.Add(targetRelative);
            copied++;

            report.Files.Add(new MigrationCopy(root.Directory, targetRelative, sourceHash, bytes, false, "copied"));
        }

        var legacyMarker = Path.Combine(root.Directory, "global_cfg.ini");
        if (File.Exists(legacyMarker))
        {
            ArchiveFile(legacyMarker, root.Kind, "global_cfg.ini", archive, null);
            warnings.Add("旧 global_cfg.ini 已归档；新版本不再使用模式标记。");
        }
    }

    private void MigrateCredential(
        string staging,
        Dictionary<string, (string? Backup, bool Existed)> targets,
        MigrationReport report,
        List<string> warnings)
    {
        foreach (var root in _roots)
        {
            foreach (var weather in new[]
                     {
                         Path.Combine(root.Directory, "plugins", "WeatherPro.ini"),
                         Path.Combine(root.Directory, "Data", "config", "weather.ini"),
                         Path.Combine(root.Directory, "config", "weather.ini")
                     })
            {
                if (!File.Exists(weather)) continue;
                var credential = _credentialFromWeatherIni(weather);
                if (string.IsNullOrWhiteSpace(credential)) continue;
                report.ContainsLegacyPlaintextCredential = true;
                if (File.Exists(AppDataPaths.WeatherCredentialPath))
                {
                    warnings.Add("已有 DPAPI 天气密钥；旧 WeatherPro.ini 中的明文将随归档保留，请勿分享归档目录。");
                    return;
                }
                var stagedCredential = Path.Combine(staging, "secrets", "weather-appkey.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(stagedCredential)!);
                var store = new WeatherCredentialStore(stagedCredential);
                store.Write(credential);
                if (!store.TryRead(out var verified) || !string.Equals(verified, credential, StringComparison.Ordinal))
                    throw new InvalidDataException("Weather credential encryption could not be verified.");
                targets[AppDataPaths.WeatherCredentialPath] = (null, false);
                report.Files.Add(new MigrationCopy(weather, "secrets/weather-appkey.bin",
                    DataLayout.Sha256(stagedCredential), new FileInfo(stagedCredential).Length, false, "encrypted"));
                warnings.Add("检测到旧天气明文密钥，已加密导入；旧 WeatherPro.ini 按原样归档，请勿分享归档目录。");
                return;
            }
        }
    }

    private static string? ReadWeatherKey(string iniPath)
    {
        var inSection = false;
        foreach (var line in File.ReadLines(iniPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                inSection = trimmed.Equals("[hfw]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            var separator = trimmed.IndexOf('=');
            if (separator > 0 && trimmed[..separator].Trim().Equals("AppKey", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(separator + 1)..].Trim();
            }
        }
        return null;
    }

    private static void SanitizeActiveWeatherTarget(
        string staging,
        Dictionary<string, (string? Backup, bool Existed)> targets,
        MigrationReport report,
        List<string> warnings)
    {
        var target = AppDataPaths.WeatherConfigPath;
        if (!File.Exists(target)) return;

        var originalHash = DataLayout.Sha256(target);
        var temporary = Path.Combine(staging, "config", ".weather-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        File.Copy(target, temporary, false);
        SanitizeWeatherIni(temporary);
        var sanitizedHash = DataLayout.Sha256(temporary);
        if (string.Equals(originalHash, sanitizedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporary);
            return;
        }

        if (ContainsWeatherKey(target) && !File.Exists(AppDataPaths.WeatherCredentialPath))
        {
            File.Delete(temporary);
            throw new InvalidDataException(
                "The active weather.ini contains a plaintext AppKey that could not be migrated to DPAPI.");
        }

        var backup = target + ".migration-" + Guid.NewGuid().ToString("N") + ".bak";
        targets[target] = (backup, true);
        File.Move(target, backup, false);
        File.Move(temporary, target, false);
        report.Files.Add(new MigrationCopy(target, "config/weather.ini",
            sanitizedHash, new FileInfo(target).Length, false, "sanitized"));
        warnings.Add("已从活动 weather.ini 移除明文 AppKey；迁移归档中的旧 INI 仍可能含明文。");
    }

    private static void SnapshotSqlite(string source, string target)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var input = new SqliteConnection(builder.ToString());
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
        var result = command.ExecuteScalar()?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SQLite integrity check failed for " + Path.GetFileName(source));
        }
    }

    private static void Validate(string targetRelative, string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var _ = JsonDocument.Parse(File.ReadAllText(path));
        }
        else if (extension.Equals(".ini", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) throw new InvalidDataException("Empty INI file: " + Path.GetFileName(path));
            if (targetRelative is "config/battery.ini" or "config/power.ini" &&
                bytes.Any(value => value > 0x7f))
            {
                throw new InvalidDataException("Battery and Power INI files must remain ASCII.");
            }
        }
        else if (targetRelative is "history/traffic.dat" or "history/traffic.dat.bak")
        {
            ValidateTraffic(path);
        }
        else if (extension.Equals(".db", StringComparison.OrdinalIgnoreCase))
        {
            ValidateSqlite(path);
        }
    }

    private static void SanitizeWeatherIni(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) return;
        var encoding = bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe
            ? Encoding.Unicode
            : Encoding.Latin1;
        var text = encoding.GetString(bytes);
        var sanitized = Regex.Replace(text,
            @"(?im)^[ \t]*AppKey[ \t]*=.*(?:\r\n|\n|\r)?", "");
        if (!string.Equals(text, sanitized, StringComparison.Ordinal))
            File.WriteAllBytes(path, encoding.GetBytes(sanitized));
    }

    private static bool ContainsWeatherKey(string path) =>
        Regex.IsMatch(File.ReadAllText(path), @"(?im)^[ \t]*AppKey[ \t]*=");

    private static void ValidateSqlite(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite integrity check failed: " + Path.GetFileName(path));
    }

    private static void ValidateTraffic(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || !Regex.IsMatch(lines[0], "^lines:\\s*\"(?<count>\\d+)\"\\s*$"))
            throw new InvalidDataException("Invalid traffic history header.");
        var count = int.Parse(Regex.Match(lines[0], "\\d+").Value);
        if (lines.Length - 1 != count) throw new InvalidDataException("Invalid traffic history record count.");
    }

    private static void Commit(string staging, Dictionary<string, (string? Backup, bool Existed)> targets)
    {
        var ordered = targets.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var target in ordered)
        {
            var staged = Path.Combine(staging, Path.GetRelativePath(AppDataPaths.RootDirectory, target));
            if (!File.Exists(staged)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                var backup = target + ".migration-" + Guid.NewGuid().ToString("N") + ".bak";
                targets[target] = (backup, true);
                File.Move(target, backup, false);
            }
            File.Move(staged, target, false);
        }
    }

    private static void RestoreBackups(Dictionary<string, (string? Backup, bool Existed)> targets)
    {
        foreach (var (target, state) in targets)
        {
            try
            {
                if (state.Backup is not null)
                {
                    if (!File.Exists(state.Backup))
                    {
                        if (!File.Exists(target) && state.Existed)
                            Log.Error("迁移回滚缺少原文件备份：" + target);
                        continue;
                    }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(state.Backup, target);
                }
                else if (!state.Existed && File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Error("迁移回滚失败：" + target);
            }
        }
    }

    private static void CleanupTargetBackups(Dictionary<string, (string? Backup, bool Existed)> targets)
    {
        foreach (var backup in targets.Values.Select(state => state.Backup).Where(path => path is not null))
        {
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warn("迁移完成后无法清理临时备份：" + backup + "（" + error.Message + "）");
            }
        }
    }

    private static void CleanupStaleMigrationBackups()
    {
        try
        {
            DataLayout.VerifyNoReparsePointsInTree(AppDataPaths.RootDirectory);
            foreach (var backup in Directory.EnumerateFiles(AppDataPaths.RootDirectory,
                         "*.migration-*.bak", SearchOption.AllDirectories))
            {
                try { File.Delete(backup); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("无法清理旧迁移临时备份：" + backup);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn("无法检查旧迁移临时备份：" + error.Message);
        }
    }

    private static void TryRestoreLayout(DataLayoutState layout, bool existed)
    {
        try
        {
            if (existed) DataLayout.Save(layout);
            else if (File.Exists(AppDataPaths.LayoutFile)) File.Delete(AppDataPaths.LayoutFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("迁移失败后无法恢复布局状态：" + error.Message);
        }
    }

    private void RestoreArchivedSources(string archive)
    {
        foreach (var root in _roots)
        {
            foreach (var (legacy, _) in FileMap)
            {
                var destination = Path.Combine(archive, SourceName(root.Kind),
                    legacy.Replace('/', Path.DirectorySeparatorChar));
                var source = Path.Combine(root.Directory, legacy.Replace('/', Path.DirectorySeparatorChar));
                RestoreSource(destination, source);
            }
            var markerDestination = Path.Combine(archive, SourceName(root.Kind), "global_cfg.ini");
            RestoreSource(markerDestination, Path.Combine(root.Directory, "global_cfg.ini"));
            if (root.Kind != MigrationSourceKind.AppData) continue;
            var appDataArchive = Path.Combine(archive, "appdata");
            if (!Directory.Exists(appDataArchive)) continue;
            foreach (var file in Directory.EnumerateFiles(appDataArchive, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(appDataArchive, file);
                RestoreSource(file, Path.Combine(root.Directory, relative));
            }
        }
    }

    private static void RestoreSource(string archived, string source)
    {
        if (!File.Exists(archived) || File.Exists(source)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(archived, source, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Error("迁移失败后无法恢复旧源文件：" + source + "（" + error.Message + "）");
        }
    }

    private static void ArchiveFile(string source, MigrationSourceKind kind, string relative, string archive,
        string? targetRelative)
    {
        var sourceName = kind switch
        {
            MigrationSourceKind.InstallLegacy => "install-legacy",
            MigrationSourceKind.InstallDataLegacy => "install-data",
            _ => "appdata"
        };
        var destination = Path.Combine(archive, sourceName, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            File.Copy(source, destination, false);
            if (DataLayout.Sha256(source) != DataLayout.Sha256(destination))
            {
                File.Delete(destination);
                throw new IOException("Legacy archive copy failed verification: " + relative);
            }
        }
    }

    private void MoveLegacySourcesToArchive(string archive, List<string> warnings)
    {
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root.Directory)) continue;
            foreach (var (legacy, _) in FileMap)
            {
                var source = Path.Combine(root.Directory, legacy.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source)) continue;
                var destination = Path.Combine(archive, SourceName(root.Kind),
                    legacy.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ArchiveAndRemoveSource(source, destination, warnings);
            }

            foreach (var legacy in LegacySidecars)
            {
                var source = Path.Combine(root.Directory, legacy.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source)) continue;
                var destination = Path.Combine(archive, SourceName(root.Kind),
                    legacy.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ArchiveAndRemoveSource(source, destination, warnings);
            }

            var marker = Path.Combine(root.Directory, "global_cfg.ini");
            if (File.Exists(marker))
            {
                var destination = Path.Combine(archive, SourceName(root.Kind), "global_cfg.ini");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ArchiveAndRemoveSource(marker, destination, warnings);
            }

            if (root.Kind == MigrationSourceKind.AppData)
                ArchiveRemainingAppData(root, archive, warnings);
            CleanupEmptyLegacyDirectories(root, warnings);
        }
    }

    private static void ArchiveRemainingAppData(LegacyRoot root, string archive, List<string> warnings)
    {
        DataLayout.VerifyNoReparsePointsInTree(root.Directory);
        foreach (var file in Directory.EnumerateFiles(root.Directory, "*", SearchOption.AllDirectories))
        {
            DataLayout.VerifyNoReparsePoint(file);
            var relative = Path.GetRelativePath(root.Directory, file);
            var destination = Path.Combine(archive, "appdata", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ArchiveAndRemoveSource(file, destination, warnings);
        }
    }

    private static void ArchiveAndRemoveSource(string source, string destination, List<string> warnings)
    {
        var archiveVerified = false;
        try
        {
            if (!File.Exists(destination))
            {
                try
                {
                    File.Move(source, destination);
                    return;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A locked SQLite/log file can still be copied through a read handle.
                    File.Copy(source, destination, false);
                }
            }
            if (DataLayout.Sha256(source) != DataLayout.Sha256(destination))
                throw new IOException("Legacy archive copy failed verification.");
            archiveVerified = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!archiveVerified)
            {
                try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            }
            AddWarning(warnings, "旧文件暂未归档：" + Path.GetFileName(source) + "（" + error.Message + "）");
            return;
        }

        try
        {
            File.Delete(source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AddWarning(warnings, "旧文件已归档，但源文件仍被占用：" + Path.GetFileName(source));
        }
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < 32) warnings.Add(warning);
        else if (warnings.Count == 32) warnings.Add("还有更多旧文件未能清理，详见后续启动或手动检查。");
    }

    private static void CleanupEmptyLegacyDirectories(LegacyRoot root, List<string> warnings)
    {
        if (!Directory.Exists(root.Directory)) return;

        if (root.Kind == MigrationSourceKind.AppData)
        {
            var directories = Directory.EnumerateDirectories(root.Directory, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length).ToList();
            foreach (var directory in directories)
            {
                TryDeleteEmptyDirectory(directory, warnings);
            }
        }

        if (root.Kind == MigrationSourceKind.InstallLegacy)
        {
            // Never walk the whole installation tree: it may contain unrelated
            // empty directories, and the installation itself may be a drive root.
            foreach (var relative in new[]
                     {
                         "plugins", "database", "Data/database", "Media/Logs"
                     })
            {
                TryDeleteEmptyDirectory(Path.Combine(root.Directory,
                    relative.Replace('/', Path.DirectorySeparatorChar)), warnings);
            }
        }

        if (root.Kind is MigrationSourceKind.AppData or MigrationSourceKind.InstallDataLegacy)
            TryDeleteEmptyDirectory(root.Directory, warnings);
    }

    private static void TryDeleteEmptyDirectory(string directory, List<string> warnings)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return;
            Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AddWarning(warnings, "旧目录暂未清理：" + directory + "（" + error.Message + "）");
        }
    }

    private static string SourceName(MigrationSourceKind kind) => kind switch
    {
        MigrationSourceKind.InstallLegacy => "install-legacy",
        MigrationSourceKind.InstallDataLegacy => "install-data",
        _ => "appdata"
    };

    private void CleanupOldArchives(string keep)
    {
        if (!Directory.Exists(AppDataPaths.MigrationArchiveDirectory)) return;
        foreach (var directory in Directory.EnumerateDirectories(AppDataPaths.MigrationArchiveDirectory))
        {
            if (!string.Equals(directory, keep, StringComparison.OrdinalIgnoreCase)) TryDeleteTree(directory);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn("无法删除临时数据目录：" + path);
        }
    }
}
