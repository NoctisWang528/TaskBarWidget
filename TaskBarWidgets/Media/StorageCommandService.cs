using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.IO;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.App;

internal sealed class StorageCommandService
{
    private sealed class RestoredFile
    {
        internal string Target { get; init; } = "";
        internal string? Backup { get; init; }
        internal List<(string Target, string Backup)> Sidecars { get; } = [];
    }

    private readonly DataBackupService _backups;
    private readonly Lazy<DataMigrationService> _migration;

    internal StorageCommandService(string installRoot, string dataRoot)
    {
        _backups = new DataBackupService(dataRoot);
        _migration = new Lazy<DataMigrationService>(() => DataStartup.Migration(dataRoot, installRoot));
    }

    internal DataMigrationResult MigrateIfNeeded() => _migration.Value.MigrateIfNeeded();

    internal void MigrateForRequest() =>
        _migration.Value.Migrate(DataLayout.LoadOrInitialize(), File.Exists(AppDataPaths.LayoutFile));

    internal DataStatus Status() => _backups.GetStatus();

    internal object Backup(string? destination)
    {
        return BackupAsync(destination, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal async Task<object> BackupAsync(string? destination, CancellationToken token)
    {
        var path = DefaultBackupPath(destination);
        await _backups.CreateBackupAsync(path, CurrentVersion(), token);
        return new { path, status = _backups.GetStatus() };
    }

    internal object ClearCache()
    {
        _backups.ClearCache();
        return new { status = _backups.GetStatus() };
    }

    internal object ScheduleRestore(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Backup ZIP not found.", path);
        _backups.ValidateBackupAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        AppDataPaths.EnsureDirectories();
        var temporary = AppDataPaths.PendingRestoreFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(path, temporary, true);
            File.Move(temporary, AppDataPaths.PendingRestoreFile, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new { pendingRestore = true, status = _backups.GetStatus() };
    }

    internal object ScheduleLegacyImport()
    {
        var layout = DataLayout.LoadOrInitialize();
        layout.State = "migration-requested";
        DataLayout.Save(layout);
        return new { pendingMigration = true };
    }

    internal static void ApplyPendingRestore(string dataRoot)
    {
        var pending = AppDataPaths.PendingRestoreFile;
        if (!File.Exists(pending)) return;
        var root = Path.GetFullPath(dataRoot);
        var staging = Path.Combine(AppDataPaths.MigrationDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
        var backupPath = Path.Combine(AppDataPaths.BackupsDirectory, "pre-restore-latest.zip");
        var moved = new List<RestoredFile>();
        string? temporaryBackup = null;
        try
        {
            DataBackupService backup = new(root);
            backup.ValidateBackupAsync(pending, CancellationToken.None).GetAwaiter().GetResult();
            temporaryBackup = Path.Combine(AppDataPaths.MigrationDirectory,
                ".pre-restore-" + Guid.NewGuid().ToString("N") + ".zip");
            backup.CreateBackupAsync(temporaryBackup, CurrentVersion(), CancellationToken.None)
                .GetAwaiter().GetResult();
            File.Move(temporaryBackup, backupPath, true);
            temporaryBackup = null;

            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(pending))
            {
                var manifestEntry = archive.GetEntry("backup-manifest.json")
                    ?? throw new InvalidDataException("Backup manifest is missing.");
                using (var manifestStream = manifestEntry.Open())
                {
                    var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestStream)
                        ?? throw new InvalidDataException("Backup manifest is invalid.");
                    foreach (var file in manifest.Files)
                    {
                        DataLayout.ValidateRelativePath(file.Path);
                        var entry = archive.GetEntry(file.Path)
                            ?? throw new InvalidDataException("Backup file is missing: " + file.Path);
                        var staged = DataLayout.ResolveUnder(staging, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                        entry.ExtractToFile(staged, true);
                        if (!DataLayout.Sha256(staged).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Backup hash mismatch: " + file.Path);
                        var target = DataLayout.ResolveUnder(root, file.Path);
                        var restored = new RestoredFile { Target = target };
                        if (File.Exists(target))
                        {
                            var backupFile = target + ".restore-" + Guid.NewGuid().ToString("N") + ".bak";
                            File.Move(target, backupFile);
                            restored = new RestoredFile { Target = target, Backup = backupFile };
                        }
                        moved.Add(restored);
                        if (IsDatabase(file.Path))
                        {
                            foreach (var suffix in new[] { "-wal", "-shm" })
                            {
                                var sidecar = target + suffix;
                                if (!File.Exists(sidecar)) continue;
                                var backupFile = sidecar + ".restore-" + Guid.NewGuid().ToString("N") + ".bak";
                                File.Move(sidecar, backupFile);
                                restored.Sidecars.Add((sidecar, backupFile));
                            }
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Move(staged, target);
                    }
                }
            }
            File.Delete(pending);
            foreach (var restored in moved)
            {
                foreach (var backupFile in restored.Sidecars.Select(item => item.Backup)
                             .Append(restored.Backup).Where(path => path is not null))
                {
                    try { if (File.Exists(backupFile)) File.Delete(backupFile); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn("恢复成功但旧备份文件未能删除：" + backupFile);
                    }
                }
            }
        }
        catch
        {
            foreach (var restored in moved.AsEnumerable().Reverse())
            {
                TryRollbackRestoredFile(restored.Target, restored.Backup);
                foreach (var (sidecar, sidecarBackup) in restored.Sidecars)
                    TryRollbackRestoredFile(sidecar, sidecarBackup);
            }
            throw;
        }
        finally
        {
            try { if (temporaryBackup is not null && File.Exists(temporaryBackup)) File.Delete(temporaryBackup); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warn("恢复失败后无法删除临时备份：" + temporaryBackup);
            }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warn("恢复失败后无法删除暂存目录：" + staging);
            }
        }
    }

    private static void TryRollbackRestoredFile(string target, string? backup)
    {
        try
        {
            if (File.Exists(target)) File.Delete(target);
            if (backup is not null && File.Exists(backup)) File.Move(backup, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Error("恢复失败后无法回滚：" + target + "（" + error.Message + "）");
        }
    }

    private static string CurrentVersion() =>
        typeof(StorageCommandService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?
            .Split('+')[0] ??
        typeof(StorageCommandService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private static bool IsDatabase(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".db", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(relativePath).Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(relativePath).Equals(".sqlite3", StringComparison.OrdinalIgnoreCase);

    private static string DefaultBackupPath(string? destination)
    {
        if (!string.IsNullOrWhiteSpace(destination)) return Path.GetFullPath(destination);

        var stem = Path.Combine(AppDataPaths.BackupsDirectory,
            "TaskBarWidgets-backup-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        var path = stem + ".zip";
        for (var suffix = 2; File.Exists(path); suffix++)
            path = stem + "-" + suffix + ".zip";
        return path;
    }

}
