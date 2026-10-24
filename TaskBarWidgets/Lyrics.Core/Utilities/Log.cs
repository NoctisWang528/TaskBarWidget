using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TaskbarLyrics.Core.Utilities;

public static class Log
{
    private const long MaxLogFileSizeBytes = 5 * 1024 * 1024;
    private const int LogRetentionDays = 7;
    private const string DebugLogFileName = "app-debug.log";
    private const string ErrorLogFileName = "error.log";
    private static bool _isVerboseEnabled;

    public enum Level
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public static void Write(Level level, string message)
    {
        if (!_isVerboseEnabled && level < Level.Warn)
        {
            return;
        }

        try
        {
            var logPath = level == Level.Error
                ? GetErrorLogPath()
                : GetDebugLogPath();
            LogFileWriter.AppendLine(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant()}] {message}", MaxLogFileSizeBytes);
        }
        catch
        {
            // 忽略写日志时的异常，防止影响主流程
        }
    }

    public static void Debug(string message) => Write(Level.Debug, message);
    public static void Info(string message) => Write(Level.Info, message);
    public static void Warn(string message) => Write(Level.Warn, message);
    public static void Error(string message) => Write(Level.Error, message);

    /// <summary>
    /// Writes a low-frequency diagnostic event even when verbose logging is disabled.
    /// Use this for state transitions and individual operations, never for polling loops.
    /// </summary>
    public static void Diagnostic(string category, string message)
    {
        try
        {
            var safeCategory = string.IsNullOrWhiteSpace(category) ? "DIAGNOSTIC" : category.Trim();
            LogFileWriter.AppendLine(
                GetDebugLogPath(),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{safeCategory}] {message}",
                MaxLogFileSizeBytes,
                LogRetentionDays);
        }
        catch
        {
            // Diagnostics must never affect the application flow.
        }
    }

    public static void SetVerboseEnabled(bool enabled)
    {
        _isVerboseEnabled = enabled;
    }

    public static void EnsureLogsDirectory()
    {
        Directory.CreateDirectory(GetLogsDirectory());
    }

    public static string GetLogsDirectory() => AppDataPaths.LogsDirectory;

    public static string GetDebugLogPath()
    {
        return LogFileWriter.GetActiveLogPath(
            Path.Combine(GetLogsDirectory(), DebugLogFileName),
            MaxLogFileSizeBytes,
            LogRetentionDays);
    }

    public static string GetErrorLogPath()
    {
        return LogFileWriter.GetActiveLogPath(
            Path.Combine(GetLogsDirectory(), ErrorLogFileName),
            MaxLogFileSizeBytes,
            LogRetentionDays);
    }

}

public static class LogFileWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly object FileLock = new();

    public static string GetActiveLogPath(string logPath, long maxFileSizeBytes = 5 * 1024 * 1024, int retentionDays = 7)
    {
        lock (FileLock)
        {
            var logsDirectory = Path.GetDirectoryName(logPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(logsDirectory);
            CleanupExpiredLogs(logsDirectory, retentionDays);
            return ResolveActiveLogPath(logPath, maxFileSizeBytes);
        }
    }

    public static void AppendLine(string logPath, string message, long maxFileSizeBytes = 5 * 1024 * 1024, int retentionDays = 7)
    {
        lock (FileLock)
        {
            var logsDirectory = Path.GetDirectoryName(logPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(logsDirectory);
            CleanupExpiredLogs(logsDirectory, retentionDays);

            var activeLogPath = ResolveActiveLogPath(logPath, maxFileSizeBytes);
            RotateLegacyEncodingIfNeeded(activeLogPath);
            using var writer = new StreamWriter(activeLogPath, append: true, Utf8WithBom);
            writer.WriteLine(message);
        }
    }

    private static string ResolveActiveLogPath(string logPath, long maxFileSizeBytes)
    {
        var directory = Path.GetDirectoryName(logPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var fileName = Path.GetFileName(logPath);
        var prefix = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var today = DateTime.Now.Date;
        for (var day = 0; day < 7; day++)
        {
            var date = today.AddDays(-day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            for (var index = 0; index < int.MaxValue; index++)
            {
                var suffix = index == 0 ? string.Empty : $".{index}";
                var candidate = Path.Combine(directory, $"{prefix}-{date}{suffix}{extension}");
                if (!File.Exists(candidate) || new FileInfo(candidate).Length < maxFileSizeBytes)
                {
                    return candidate;
                }
            }
        }

        throw new IOException("No available log file slot.");
    }

    private static void CleanupExpiredLogs(string logsDirectory, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log"))
        {
            var fileName = Path.GetFileName(file);
            if (!IsManagedLogFile(fileName, out var logDate) || logDate >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsManagedLogFile(string fileName, out DateTime logDate)
    {
        logDate = default;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dotIndex = stem.LastIndexOf('.');
        if (dotIndex >= 0 && int.TryParse(stem[(dotIndex + 1)..], out _))
        {
            stem = stem[..dotIndex];
        }

        const int dateLength = 10;
        if (stem.Length <= dateLength)
        {
            return false;
        }

        var prefix = stem[..^dateLength].TrimEnd('-');
        if (!string.Equals(prefix, "app-debug", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(prefix, "error", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DateTime.TryParseExact(
            stem[^dateLength..],
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out logDate);
    }

    private static void RotateLegacyEncodingIfNeeded(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        var info = new FileInfo(logPath);
        if (info.Length == 0)
        {
            return;
        }

        byte[] prefix;
        using (var stream = File.OpenRead(logPath))
        {
            prefix = new byte[Math.Min(Utf8Bom.Length, stream.Length)];
            _ = stream.Read(prefix, 0, prefix.Length);
        }

        if (prefix.Length >= Utf8Bom.Length &&
            prefix[0] == Utf8Bom[0] &&
            prefix[1] == Utf8Bom[1] &&
            prefix[2] == Utf8Bom[2])
        {
            return;
        }

        var legacyPath = Path.Combine(
            Path.GetDirectoryName(logPath) ?? AppDomain.CurrentDomain.BaseDirectory,
            $"{Path.GetFileNameWithoutExtension(logPath)}.legacy-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(logPath)}");
        try
        {
            File.Move(logPath, legacyPath);
        }
        catch (IOException)
        {
            // Another running instance may still hold the old log. Keep logging best-effort.
        }
    }

}
