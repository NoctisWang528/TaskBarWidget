using System.Text;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 会话落盘与历史：一个会话一个目录，历史列表靠扫目录读 session.json，**不动 history.db**。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureStore
{
    private readonly string _root;

    public CaptureStore(string? rootDir = null) => _root = rootDir ?? CaptureJson.CapturesRoot;

    public string Root => _root;

    /// <summary>会话 ID 用本地时间，目录名排序即时间排序。</summary>
    public static string NewSessionId(DateTimeOffset startedUtc)
        => $"{startedUtc.ToLocalTime():yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xFFFF):X4}";

    public string CreateSessionDirectory(string sessionId)
    {
        var dir = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string PathOf(string sessionDir, string fileName) => Path.Combine(sessionDir, fileName);

    public static void WriteSession(string sessionDir, CaptureSessionInfo info)
        => File.WriteAllText(PathOf(sessionDir, "session.json"), CaptureJson.SessionJson(info), Utf8NoBom);

    public static void WriteDevice(string sessionDir, CaptureDeviceInfo device)
        => File.WriteAllText(PathOf(sessionDir, "device.json"), CaptureJson.DeviceJson(device), Utf8NoBom);

    public static void WriteAnalysis(string sessionDir, CaptureAnalysis analysis)
        => File.WriteAllText(PathOf(sessionDir, "analysis.json"), CaptureJson.AnalysisJson(analysis), Utf8NoBom);

    public static void WriteReport(string sessionDir, CaptureBundle bundle, string? packagePath)
        => File.WriteAllText(PathOf(sessionDir, "report.html"), HtmlReportGenerator.Generate(bundle, packagePath), Utf8NoBom);

    public static void WriteReadme(string sessionDir, CaptureBundle bundle)
        => File.WriteAllText(PathOf(sessionDir, "README.txt"), CapturePackageWriter.BuildReadme(bundle), Utf8NoBom);

    public static void WriteBackendLog(string sessionDir, string content)
        => File.WriteAllText(PathOf(sessionDir, "backend.log"), content, Utf8NoBom);

    /// <summary>扫目录列出历史会话（新→旧）。解析失败的单条跳过，不阻断列表。</summary>
    public IReadOnlyList<CaptureSessionEntry> ListSessions()
    {
        if (!Directory.Exists(_root)) return [];
        var entries = new List<CaptureSessionEntry>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var path = PathOf(dir, "session.json");
            if (!File.Exists(path)) continue;
            try
            {
                if (!CaptureJson.TryParseSession(File.ReadAllText(path), out var info) || info == null) continue;
                entries.Add(new CaptureSessionEntry
                {
                    Id = info.Id,
                    Directory = dir,
                    StartedUtc = info.StartedUtc,
                    State = info.State,
                    RecordCount = info.RecordCount,
                    ErrorCount = info.ErrorCount,
                    DeviceName = ReadDeviceName(dir),
                    HasReport = File.Exists(PathOf(dir, "report.html")),
                });
            }
            catch (IOException ex)
            {
                Utils.Log.Debug($"采集历史目录读取失败 {dir}: {ex.Message}");
            }
        }
        return entries.OrderByDescending(e => e.StartedUtc).ToList();
    }

    /// <summary>读回一个会话的全部材料；分析结果按记录重算，避免 analysis.json 与记录不一致。</summary>
    public static bool TryLoad(string sessionDir, out CaptureBundle? bundle)
    {
        bundle = null;
        try
        {
            var sessionPath = PathOf(sessionDir, "session.json");
            if (!File.Exists(sessionPath)) return false;
            if (!CaptureJson.TryParseSession(File.ReadAllText(sessionPath), out var session) || session == null) return false;

            CaptureDeviceInfo? device = null;
            var devicePath = PathOf(sessionDir, "device.json");
            if (File.Exists(devicePath)) CaptureJson.TryParseDevice(File.ReadAllText(devicePath), out device);

            var records = ReadRecords(PathOf(sessionDir, "records.jsonl"));
            var events = ReadEvents(PathOf(sessionDir, "events.jsonl"));
            var errors = ReadErrors(PathOf(sessionDir, "errors.jsonl"));

            bundle = new CaptureBundle
            {
                Session = session,
                Device = device,
                Records = records,
                Events = events,
                Errors = errors,
                Analysis = CaptureAnalyzer.Analyze(records, events, errors),
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Utils.Log.Warn($"采集会话读取失败 {sessionDir}: {ex.Message}");
            return false;
        }
    }

    /// <summary>删掉一个会话目录（放弃采集用）。只删 captures 下的目录；文件刚关闭可能还被占用，重试几次。</summary>
    public async Task<bool> TryDeleteSessionAsync(string sessionDir)
    {
        // 兜底：只允许删自己建出来的会话目录，别把误传的路径删了
        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(sessionDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Utils.Log.Debug($"删除采集会话失败（第 {attempt + 1} 次）{full}: {ex.Message}");
                await Task.Delay(80 * (attempt + 1)).ConfigureAwait(false);
            }
        }
        return !Directory.Exists(full);
    }

    private static List<CaptureRecord> ReadRecords(string path)
    {
        var list = new List<CaptureRecord>();
        foreach (var line in ReadLines(path))
        {
            if (CaptureJson.TryParseRecord(line, out var item) && item != null) list.Add(item);
        }
        return list;
    }

    private static List<CaptureEvent> ReadEvents(string path)
    {
        var list = new List<CaptureEvent>();
        foreach (var line in ReadLines(path))
        {
            if (CaptureJson.TryParseEvent(line, out var item) && item != null) list.Add(item);
        }
        return list;
    }

    private static List<CaptureError> ReadErrors(string path)
    {
        var list = new List<CaptureError>();
        foreach (var line in ReadLines(path))
        {
            if (CaptureJson.TryParseError(line, out var item) && item != null) list.Add(item);
        }
        return list;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path)) return [];
        return File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static string ReadDeviceName(string sessionDir)
    {
        var path = PathOf(sessionDir, "device.json");
        if (!File.Exists(path)) return "";
        return CaptureJson.TryParseDevice(File.ReadAllText(path), out var device) && device != null
            ? device.Product
            : "";
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}

/// <summary>历史列表的一行。</summary>
public sealed class CaptureSessionEntry
{
    public required string Id { get; init; }

    public required string Directory { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    public CaptureSessionState State { get; init; }

    public int RecordCount { get; init; }

    public int ErrorCount { get; init; }

    public string DeviceName { get; init; } = "";

    /// <summary>有没有 report.html。false = 会话没走完收尾（应用在采集途中被关掉），只剩原始记录。</summary>
    public bool HasReport { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(DeviceName) ? Id : $"{DeviceName} · {Id}";
}
