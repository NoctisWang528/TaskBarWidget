using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskBarWidgets.Media.Updates;

internal static class UpdatePackage
{
    internal const long MaxZipBytes = 512L * 1024 * 1024;
    internal const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaxFiles = 8192;
    internal const int MaxManifestBytes = 4 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16 };
    private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "TaskBarWidgets.exe", "TaskBarWidgets.Updater.exe", "OpenHardwareMonitorApi.dll", "LibreHardwareMonitorLib.dll",
        "build-info.json", "release-manifest.json", "LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.md", "ACCEPTANCE.md", "README.txt",
        "concrt140.dll", "vccorlib140.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll",
        "msvcp140_codecvt_ids.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcruntime140_threads.dll", "mfc140u.dll",
    };
    private static readonly HashSet<string> ProgramDirectories = new(StringComparer.OrdinalIgnoreCase) { "Media", "UI", "language", "licenses" };
    private static readonly HashSet<string> PersonalDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "data", "cache", "caches", "logs", "log", "history", "backup", "backups", "updates", "settings", "config", "configuration", "userdata", "user-data", "plugins" };

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static T ParseJson<T>(byte[] bytes) where T : class
    {
        // Windows tools may emit a UTF-8 BOM; hashes continue to cover the original bytes.
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 16 });
        void CheckNames(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Ambiguous duplicate JSON property.");
                    CheckNames(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) CheckNames(item);
        }
        CheckNames(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException("Missing update JSON document.");
    }

    internal static string ValidateRelativePath(string path, bool programFile = true)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/'))
            throw new InvalidDataException("Invalid update path.");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                Regex.IsMatch(part.Split('.')[0].TrimEnd(' '), @"^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Unsafe update path.");
        }
        if (!programFile) return path;
        if (parts.Length == 1)
        {
            if (!RootFiles.Contains(path) && !Regex.IsMatch(path, @"^(concrt140|msvcp140(?:_[a-z0-9]+)?|vcruntime140(?:_[a-z0-9]+)?|mfc140u)\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Unmanaged root file in update.");
            return path;
        }
        var settingsResource = path.Equals("UI/Settings/SettingsThemeResources.xbf", StringComparison.OrdinalIgnoreCase) ||
            (parts.Length == 4 && parts[0].Equals("Media", StringComparison.OrdinalIgnoreCase) &&
             parts[1].Equals("Web", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("Settings", StringComparison.OrdinalIgnoreCase) &&
             new[] { "bridge.js", "hotkeys.js", "integration.js", "settings.css", "settings.html", "settings.js", "state.js", "track-offsets-page.js" }.Contains(parts[3], StringComparer.OrdinalIgnoreCase));
        if (!ProgramDirectories.Contains(parts[0]) || (!settingsResource && parts.Skip(1).Any(PersonalDirectories.Contains)))
            throw new InvalidDataException("Personal data cannot be included in updates.");
        var name = parts[^1];
        var extension = Path.GetExtension(name);
        if (parts[0].Equals("language", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length != 2 || (name != "English.ini" && name != "Simplified_Chinese.ini"))
                throw new InvalidDataException("Unmanaged language file.");
        }
        else if (new[] { ".ini", ".db", ".db3", ".sqlite", ".sqlite3", ".log", ".dat", ".bak", ".tmp", ".config" }.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
             name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) || name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("media-settings.json", StringComparison.OrdinalIgnoreCase) || name.Contains(".db-", StringComparison.OrdinalIgnoreCase) || name.Contains(".sqlite-", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("config.json", StringComparison.OrdinalIgnoreCase) || name.Contains("history", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("credentials", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Personal configuration cannot be included in updates.");
        return path;
    }

    internal static void EnsureSafePath(string path)
    {
        var full = Path.GetFullPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Update paths cannot contain filesystem links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string ChildPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes its root.");
        EnsureSafePath(result);
        return result;
    }

    internal static async Task VerifyHash(string path, string expected, CancellationToken token)
    {
        EnsureSafePath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update SHA256 verification failed.");
    }

    internal static async Task ExtractAndVerify(string package, string stage, UpdateMetadata metadata, CancellationToken token)
    {
        EnsureSafePath(stage);
        if (Directory.Exists(stage)) throw new IOException("The update staging directory already exists.");
        await VerifyHash(package, metadata.Sha256, token);
        CheckZipDirectory(package, token);
        using var archive = ZipFile.OpenRead(package);
        if (archive.Entries.Count > MaxFiles * 2 + 1) throw new InvalidDataException("Too many ZIP entries.");
        var prefix = Path.GetFileNameWithoutExtension(metadata.AssetName) + "/";
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathCasing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((type != 0 && type != 0x8000 && type != 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP links and special files are forbidden.");
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected ZIP root.");
            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0 && entry.FullName.EndsWith('/')) continue;
            var directory = entry.FullName.EndsWith('/');
            if (type == 0x4000 && !directory) throw new InvalidDataException("Invalid ZIP directory.");
            relative = ValidateRelativePath(directory ? relative.TrimEnd('/') : relative, false);
            var pathParts = relative.Split('/');
            for (var part = 1; part <= pathParts.Length; part++)
            {
                var componentPath = string.Join('/', pathParts.Take(part));
                if (pathCasing.TryGetValue(componentPath, out var previous) && previous != componentPath)
                    throw new InvalidDataException("Case-conflicting ZIP directory.");
                pathCasing[componentPath] = componentPath;
            }
            if (!paths.Add(relative)) throw new InvalidDataException("Duplicate or case-conflicting ZIP path.");
            if (directory) { if (entry.Length != 0) throw new InvalidDataException("Invalid ZIP directory length."); continue; }
            if (entry.Length < 0 || entry.Length > MaxUnpackedBytes || (total += entry.Length) > MaxUnpackedBytes || files.Count >= MaxFiles)
                throw new InvalidDataException("Update exceeds unpacked limits.");
            files.Add(relative, entry);
        }
        // A file cannot also be an implicit parent directory (including case-folded parents).
        foreach (var file in files.Keys)
            for (var slash = file.IndexOf('/'); slash >= 0; slash = file.IndexOf('/', slash + 1))
                if (files.ContainsKey(file[..slash])) throw new InvalidDataException("ZIP file/directory collision.");
        if (!files.TryGetValue("app-files.json", out var manifestEntry) || manifestEntry.Length > MaxManifestBytes)
            throw new InvalidDataException("Missing or oversized update file manifest.");
        using var manifestBuffer = new MemoryStream();
        await using (var source = manifestEntry.Open()) await CopyBounded(source, manifestBuffer, manifestEntry.Length, token);
        var manifestBytes = manifestBuffer.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(manifestBytes)).Equals(metadata.FileManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update file manifest SHA256 mismatch.");
        var manifest = ParseManifest(manifestBytes, metadata);
        if (files.Count != manifest.Files.Length + 1) throw new InvalidDataException("ZIP contains unlisted program files.");
        foreach (var file in manifest.Files)
            if (!files.TryGetValue(file.Path, out var entry) || entry.FullName[prefix.Length..] != file.Path || entry.Length != file.Bytes)
                throw new InvalidDataException("ZIP and file manifest do not agree.");
        Directory.CreateDirectory(stage);
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var destination = ChildPath(stage, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureSafePath(destination);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            await using (var source = files[file.Path].Open()) await CopyBounded(source, output, file.Bytes, token);
            await VerifyHash(destination, file.Sha256, token);
        }
        await File.WriteAllBytesAsync(ChildPath(stage, "app-files.json"), manifestBytes, token);
        await VerifyStage(stage, metadata, token);
    }

    private static void CheckZipDirectory(string package, CancellationToken token)
    {
        // Check the actual central-directory count before ZipArchive allocates its entry objects.
        // These package limits never require ZIP64 or multi-disk ZIPs.
        using var stream = File.OpenRead(package);
        if (stream.Length < 22 || stream.Length > MaxZipBytes) throw new InvalidDataException("Invalid ZIP size.");
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        var end = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054b50 &&
                BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) == tail.Length - index - 22)
            { end = index; break; }
        if (end < 0) throw new InvalidDataException("Missing ZIP directory.");
        var record = tail.AsSpan(end);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        var endPosition = stream.Length - tail.Length + end;
        if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(record[6..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != count || count > MaxFiles * 2 + 1 ||
            size > 16 * 1024 * 1024 || (long)offset + size != endPosition)
            throw new InvalidDataException("Oversized or unsupported ZIP directory.");
        stream.Position = offset;
        Span<byte> header = stackalloc byte[46];
        var actual = 0;
        while (stream.Position < endPosition)
        {
            token.ThrowIfCancellationRequested();
            if (++actual > MaxFiles * 2 + 1 || stream.Position + header.Length > endPosition)
                throw new InvalidDataException("Too many ZIP entries.");
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                throw new InvalidDataException("Invalid ZIP directory entry.");
            var variableLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]) +
                BinaryPrimitives.ReadUInt16LittleEndian(header[30..]) + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            stream.Position += variableLength;
            if (stream.Position > endPosition) throw new InvalidDataException("Invalid ZIP directory length.");
        }
        if (actual != count) throw new InvalidDataException("ZIP directory count mismatch.");
    }

    internal static AppFileManifest ParseManifest(byte[] bytes, UpdateMetadata metadata)
    {
        var manifest = ParseJson<AppFileManifest>(bytes);
        if (manifest.ProtocolVersion != 1 || manifest.Version != metadata.Version || manifest.Architecture != "win-x64" ||
            manifest.Files is null || manifest.Files.Length == 0 || manifest.Files.Length >= MaxFiles)
            throw new InvalidDataException("Incompatible update file manifest.");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
            if (file is null || !paths.Add(ValidateRelativePath(file.Path)) || !IsHash(file.Sha256) ||
                file.Bytes < 0 || file.Bytes > MaxUnpackedBytes || (total += file.Bytes) > MaxUnpackedBytes)
                throw new InvalidDataException("Invalid update file manifest entry.");
        return manifest;
    }

    internal static async Task VerifyStage(string stage, UpdateMetadata metadata, CancellationToken token)
    {
        EnsureSafePath(stage);
        var manifestPath = ChildPath(stage, "app-files.json");
        if (new FileInfo(manifestPath).Length > MaxManifestBytes) throw new InvalidDataException("Oversized staged manifest.");
        await VerifyHash(manifestPath, metadata.FileManifestSha256, token);
        var manifest = ParseManifest(await File.ReadAllBytesAsync(manifestPath, token), metadata);
        var expected = new HashSet<string>(manifest.Files.Select(file => file.Path), StringComparer.Ordinal) { "app-files.json" };
        // Walk without following links, then check the exact persisted stage on every recovery/preparation.
        var directories = new Stack<string>();
        directories.Push(stage);
        while (directories.TryPop(out var directory))
        {
            EnsureSafePath(directory);
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                EnsureSafePath(item);
                if (Directory.Exists(item)) directories.Push(item);
                else if (!expected.Remove(Path.GetRelativePath(stage, item).Replace('\\', '/')))
                    throw new InvalidDataException("Unexpected staged update file.");
            }
        }
        if (expected.Count != 0) throw new InvalidDataException("Missing staged update file.");
        foreach (var file in manifest.Files)
        {
            var path = ChildPath(stage, file.Path);
            if (new FileInfo(path).Length != file.Bytes) throw new InvalidDataException("Staged file size mismatch.");
            await VerifyHash(path, file.Sha256, token);
        }
    }

    internal static async Task CopyBounded(Stream source, Stream destination, long expected, CancellationToken token, Action<long>? progress = null)
    {
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            if ((total += read) > expected) throw new InvalidDataException("Response exceeds the declared size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            progress?.Invoke(total);
        }
        if (total != expected) throw new InvalidDataException("Response is shorter than its declared size.");
    }
}
