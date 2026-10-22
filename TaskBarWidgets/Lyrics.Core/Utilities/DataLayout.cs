using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarLyrics.Core.Utilities;

public sealed class DataLayoutState
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("layoutVersion")]
    public int LayoutVersion { get; set; } = CurrentVersion;

    [JsonPropertyName("state")]
    public string State { get; set; } = "initializing";

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("migratedAtUtc")]
    public DateTimeOffset? MigratedAtUtc { get; set; }

    [JsonPropertyName("migrationId")]
    public string MigrationId { get; set; } = "";
}

public enum MigrationSourceKind
{
    InstallLegacy,
    InstallDataLegacy,
    AppData
}

public sealed record MigrationCopy(
    string Source,
    string RelativePath,
    string Sha256,
    long Bytes,
    bool Conflict,
    string Action = "copied");

public sealed class MigrationReport
{
    public const int CurrentVersion = 1;

    public int ProtocolVersion { get; set; } = CurrentVersion;
    public string MigrationId { get; set; } = "";
    public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string ArchiveDirectory { get; set; } = "";
    public List<MigrationCopy> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool ContainsLegacyPlaintextCredential { get; set; }
}

public sealed class BackupFile
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Method { get; set; } = "copy";
}

public sealed class BackupManifest
{
    public const int CurrentVersion = 1;

    public int ProtocolVersion { get; set; } = CurrentVersion;
    public string AppVersion { get; set; } = "";
    public int LayoutVersion { get; set; } = DataLayoutState.CurrentVersion;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool CredentialPortable { get; set; }
    public bool ContainsCredentialCiphertext { get; set; }
    public List<string> Excluded { get; set; } = [];
    public List<BackupFile> Files { get; set; } = [];
}

public static class DataLayout
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static DataLayoutState LoadOrInitialize()
    {
        AppDataPaths.EnsureDirectories();
        var path = AppDataPaths.LayoutFile;
        if (!File.Exists(path))
        {
            var state = new DataLayoutState { State = "initializing" };
            return state;
        }

        var loaded = JsonSerializer.Deserialize<DataLayoutState>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Data/layout.json is invalid.");
        if (loaded.LayoutVersion != DataLayoutState.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported data layout version {loaded.LayoutVersion}.");
        }

        return loaded;
    }

    public static void Save(DataLayoutState state)
    {
        AppDataPaths.EnsureDirectories();
        AtomicWriteJson(AppDataPaths.LayoutFile, state);
    }

    public static T ReadJson<T>(string path)
    {
        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        return value ?? throw new InvalidDataException($"Invalid JSON document: {Path.GetFileName(path)}");
    }

    public static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        AtomicWriteJson(path, value);
    }

    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void VerifyNoReparsePoint(string path)
    {
        FileSystemInfo? current = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
        while (current is not null)
        {
            if ((File.Exists(current.FullName) || Directory.Exists(current.FullName)) &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Data paths must not contain filesystem links.");
            }
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
    }

    public static void VerifyNoReparsePointsInTree(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root)) return;
        VerifyNoReparsePoint(root);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Data paths must not contain filesystem links: " + entry.FullName);
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
    }

    public static void ValidateRelativePath(string relative)
    {
        relative = relative.Replace('\\', '/');
        var parts = relative.Split('/');
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.EndsWith('/') ||
            relative.Contains(':') || parts.Any(IsUnsafeWindowsComponent))
        {
            throw new InvalidDataException("Unsafe backup path.");
        }
    }

    private static bool IsUnsafeWindowsComponent(string component)
    {
        if (component is "" or "." or ".." ||
            component.EndsWith('.') || component.EndsWith(' ') ||
            component.Any(character => char.IsControl(character) ||
                                        Path.GetInvalidFileNameChars().Contains(character)))
            return true;

        var stem = component.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }

    public static string ResolveUnder(string root, string relative)
    {
        ValidateRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Path escapes the data root.");
        }
        VerifyNoReparsePoint(full);
        return full;
    }

    public static string NewMigrationId() =>
        DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" +
        Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static void AtomicWriteJson<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var directory = Path.GetDirectoryName(path) ?? AppDataPaths.RootDirectory;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
