using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TaskBarWidgets.Media.Integration;

internal sealed record AiUsageQuota(string Id, string Label, double UsedPercent, int WindowMinutes, long? ResetsAt);
internal sealed record AiUsageTokenDay(string Date, long Tokens);
internal sealed record AiUsageBalance(string Currency, string Available, string Label);
internal sealed record AiUsageProviderInfo(string Id, string Name, string Mode, string ServiceId);
internal sealed record AiUsageSnapshot(string Status, string? Error, DateTimeOffset? UpdatedAt,
    IReadOnlyList<AiUsageQuota> Quotas, IReadOnlyList<AiUsageTokenDay>? Days, long? LifetimeTokens, long? PeakDailyTokens)
{
    public long Revision { get; init; }
    public string ProviderId { get; init; } = "codex";
    public string ProviderName { get; init; } = "ChatGPT";
    public IReadOnlyDictionary<string, bool> EnabledServices { get; init; } = new Dictionary<string, bool>
        { ["openai"] = false, ["deepseek"] = false, ["gemini"] = false, ["claude"] = false };
    public IReadOnlyList<AiUsageProviderInfo> Providers { get; init; } = [];
    public bool ShowApi { get; init; }
    public IReadOnlyList<AiUsageBalance> Balances { get; init; } = [];
    public string SourceLabel { get; init; } = "官方 Codex";
    public string HistoryNote { get; init; } = "";
    public string QuotaNote { get; init; } = "";
    public string ActivityUnit { get; init; } = "tokens";
    public bool ActivityUtc { get; init; }
    public bool HasCredential { get; init; }
    public string CredentialKind { get; init; } = "none";
    public string ProjectId { get; init; } = "";
}

internal interface IAiUsageProvider : IDisposable
{
    AiUsageSnapshot Snapshot { get; }
    AiUsageSnapshot Read(bool force = false);
    event Action? Changed;
}

// Only the installed official CLI handles authentication. No credential files are read or exported.
internal sealed class CodexUsageService : IAiUsageProvider
{
    public event Action? Changed;
    private long _revision;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _refresh;
    private DateTimeOffset _attempt;
    private AiUsageSnapshot _snapshot = new("idle", null, null, [], null, null, null);
    public AiUsageSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public AiUsageSnapshot Read(bool force = false)
    {
        lock (_gate)
        {
            if (!_stop.IsCancellationRequested && (_refresh is null || _refresh.IsCompleted) &&
                (DateTimeOffset.UtcNow - _attempt > TimeSpan.FromSeconds(force ? 5 : 60)))
            {
                _attempt = DateTimeOffset.UtcNow;
                // The user can switch Codex accounts outside this application. Never mix old history with a new account's quota.
                _snapshot = new("loading", null, null, [], null, null, null) { Revision = ++_revision };
                _refresh = Task.Run(RefreshAsync);
            }
            return _snapshot;
        }
    }

    internal static string? FindExecutable()
    {
        // Resolve the registered package, never a version-specific WindowsApps path.
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty)
                .Where(package => package.Id.Name.Equals("OpenAI.Codex", StringComparison.OrdinalIgnoreCase)))
            {
                var candidate = Path.Combine(package.InstalledLocation.Path, "app", "resources", "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(installed))
        {
            var candidate = Directory.EnumerateDirectories(installed).Select(dir => Path.Combine(dir, "codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (candidate is not null) return candidate;
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(dir => !string.IsNullOrWhiteSpace(dir) && Path.IsPathFullyQualified(dir.Trim('"')))
            .Select(dir => Path.Combine(dir.Trim('"'), "codex.exe")).FirstOrDefault(File.Exists);
    }

    private async Task RefreshAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = new Process();
        try
        {
            var executable = FindExecutable() ?? throw new FileNotFoundException();
            process.StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-s", "read-only", "-a", "never", "app-server" }) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            // Drain without retaining CLI diagnostics (which can contain account information).
            var drain = DrainAsync(process.StandardError, timeout.Token);
            try
            {
                await SendAsync(1, "initialize", new { clientInfo = new { name = "taskbarwidgets", version = "1.0" }, capabilities = new { experimentalApi = true } });
                await ReceiveAsync(1);
                await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), timeout.Token);
                await SendAsync(2, "account/rateLimits/read", null);
                var limits = await ReceiveAsync(2);
                var quotas = ParseQuotas(limits);
                // Preserve the independently successful quota read if an older CLI lacks usage/read.
                lock (_gate) _snapshot = _snapshot with { Quotas = quotas, UpdatedAt = DateTimeOffset.UtcNow };
                await SendAsync(3, "account/usage/read", null);
                var usage = await ReceiveAsync(3);
                await SendAsync(4, "account/rateLimits/read", null);
                var confirmedLimits = await ReceiveAsync(4);
                if (!SameAccount(limits, confirmedLimits))
                {
                    lock (_gate) _snapshot = new("error", "Codex 账户已变更，请刷新。", null, [], null, null, null) { Revision = ++_revision };
                    return;
                }
                var snapshot = Parse(confirmedLimits, usage, DateTimeOffset.UtcNow);
                lock (_gate) _snapshot = snapshot with { Revision = ++_revision };
            }
            finally
            {
                timeout.Cancel();
                try { await drain; } catch (OperationCanceledException) { }
            }

            async Task SendAsync(int id, string method, object? parameters) =>
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), timeout.Token);
            async Task<JsonElement> ReceiveAsync(int id)
            {
                while (true)
                {
                    var line = await ReadBoundedLineAsync(process.StandardOutput, timeout.Token) ?? throw new IOException();
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var found) || !found.TryGetInt32(out var value) || value != id) continue;
                    if (root.TryGetProperty("error", out _)) throw new InvalidOperationException();
                    return root.GetProperty("result").Clone();
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = error is FileNotFoundException ? "未找到 Codex。请先安装并登录官方 Codex 客户端。" :
                error is OperationCanceledException ? "读取超时，请稍后刷新。" :
                "暂时无法读取。请确认官方 Codex 已登录；历史数据需要支持 account/usage/read 的新版客户端。";
            lock (_gate) _snapshot = _snapshot with { Status = _snapshot.Quotas.Count > 0 ? "partial" : "error", Error = message, Revision = ++_revision };
        }
        finally
        {
            try { if (process.Id > 0 && !process.HasExited) { process.StandardInput.Close(); if (!process.WaitForExit(500)) process.Kill(); } }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            if (!_stop.IsCancellationRequested) Changed?.Invoke();
        }
    }

    internal static bool SameAccount(JsonElement before, JsonElement after)
    {
        static string? Account(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("accountId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        var account = Account(before);
        return !string.IsNullOrEmpty(account) && string.Equals(account, Account(after), StringComparison.Ordinal);
    }

    // ReadLineAsync allocates without a bound before a caller can check length.
    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = new System.Text.StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= 4 * 1024 * 1024) throw new InvalidDataException();
            line.Append(character[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { }
    }

    internal static IReadOnlyList<AiUsageQuota> ParseQuotas(JsonElement limits)
    {
        var quotas = new List<AiUsageQuota>();
        if (limits.ValueKind != JsonValueKind.Object) return quotas;
        if (limits.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
            foreach (var bucket in buckets.EnumerateObject()) Add(bucket.Name, bucket.Value);
        else if (limits.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object) Add("codex", legacy);
        return quotas;
        void Add(string id, JsonElement bucket)
        {
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (bucket.ValueKind != JsonValueKind.Object || !bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
                    !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
                var minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var length) ? length : 0;
                var label = bucket.TryGetProperty("limitName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : id;
                quotas.Add(new(id + ":" + key, label, Math.Clamp(percent, 0, 100), minutes, Number(window, "resetsAt")));
            }
        }
    }

    internal static AiUsageSnapshot Parse(JsonElement limits, JsonElement usage, DateTimeOffset now)
    {
        List<AiUsageTokenDay>? days = null;
        if (usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("dailyUsageBuckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
        {
            var unique = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var day in buckets.EnumerateArray())
            {
                if (day.ValueKind != JsonValueKind.Object || !day.TryGetProperty("startDate", out var date) || date.ValueKind != JsonValueKind.String ||
                    !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || Number(day, "tokens") is not { } tokens || tokens < 0) continue;
                // A bucket is a daily aggregate, not an increment. Repeated dates must not be added twice.
                unique[parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = tokens;
            }
            days = unique.Select(pair => new AiUsageTokenDay(pair.Key, pair.Value)).ToList();
        }
        var summary = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("summary", out var value) ? value : default;
        var quotas = ParseQuotas(limits);
        var complete = quotas.Count > 0 && days is not null;
        return new(complete ? "ready" : "partial", complete ? null : "部分使用量数据暂不可用。", now,
            quotas, days, NonnegativeNumber(summary, "lifetimeTokens"), NonnegativeNumber(summary, "peakDailyTokens"));
    }
    private static long? Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var result) ? result : null;
    private static long? NonnegativeNumber(JsonElement value, string key) => Number(value, key) is { } number && number >= 0 ? number : null;
    public void Dispose() => _stop.Cancel();

    internal async Task<AiUsageSnapshot> WaitForRefreshAsync()
    {
        Read();
        Task? refresh;
        lock (_gate) refresh = _refresh;
        if (refresh is not null) await refresh.ConfigureAwait(false);
        lock (_gate) return _snapshot;
    }
}
