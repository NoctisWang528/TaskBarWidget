using System.Globalization;
using System.IO;
using System.Text.Json;
using TaskbarLyrics.App;

namespace TaskBarWidgets.Media.Integration;

internal sealed class AiUsageHub : IDisposable
{
    internal static readonly AiUsageProviderInfo[] Catalog = [
        new("codex", "ChatGPT", "subscription", "openai"), new("openai-api", "ChatGPT API", "api", "openai"),
        new("deepseek-web", "DeepSeek", "subscription", "deepseek"), new("deepseek-api", "DeepSeek API", "api", "deepseek"),
        new("gemini-web", "Gemini", "subscription", "gemini"), new("gemini-api", "Gemini API", "api", "gemini"),
        new("claude-web", "Claude", "subscription", "claude"), new("claude-api", "Claude API", "api", "claude")];
    internal static readonly string[] ServiceIds = ["openai", "deepseek", "gemini", "claude"];
    private readonly object _gate = new();
    private CodexUsageService _codex = new();
    private readonly AiUsageCredentialStore _credentials;
    private readonly string _manualDirectory;
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, string, string, CancellationToken, Task<AiUsageSnapshot>> _readApi;
    private string _selected;
    private bool _showApi;
    private readonly Dictionary<string, bool> _enabled = ServiceIds.ToDictionary(id => id, _ => false, StringComparer.Ordinal);
    private long _preferencesVersion;
    private long _revision;
    private AiUsageSnapshot _snapshot;
    private sealed class Entry(AiUsageSnapshot snapshot)
    {
        internal AiUsageSnapshot Snapshot = snapshot;
        internal DateTimeOffset Attempt;
        internal Task? Work;
        internal CancellationTokenSource? Cancellation;
        internal CancellationTokenSource? Configuration;
        internal long Generation;
        internal AiUsageSnapshot? WireSource, WireSnapshot;
        internal AiUsageSnapshot? BeforeRefresh;
        internal bool WireShowApi;
        internal long WirePreferencesVersion;
    }
    internal event Action? Changed;
    internal AiUsageHub(string secretsDirectory, string historyDirectory, string selected = "codex", bool showApi = false,
        Func<string, string, string, CancellationToken, Task<AiUsageSnapshot>>? readApi = null,
        IReadOnlyDictionary<string, bool>? enabledServices = null)
    {
        _readApi = readApi ?? ((id, credential, project, token) => id == "gemini-api"
            ? GeminiApiUsageReader.ReadAsync(credential, project, token) : AiUsageApiReader.ReadAsync(id, credential, token));
        _credentials = new(secretsDirectory); _manualDirectory = Path.Combine(historyDirectory, "ai-manual");
        foreach (var provider in Catalog) _entries[provider.Id] = new(Empty(provider) with
            { HasCredential = provider.Mode == "api" && _credentials.Exists(provider.Id) });
        foreach (var id in ServiceIds) _enabled[id] = enabledServices?.GetValueOrDefault(id) == true;
        _showApi = showApi; _selected = NormalizeSelection(selected, showApi, _enabled);
        _snapshot = _selected.Length == 0 ? Disabled() : Decorate(_entries[_selected].Snapshot);
        ConnectCodex();
    }
    private void ConnectCodex()
    {
        var source = _codex;
        source.Changed += () =>
        {
            lock (_gate)
            {
                if (_stop.IsCancellationRequested || !ReferenceEquals(source, _codex) || !_enabled["openai"]) return;
                _entries["codex"].Snapshot = source.Snapshot;
                if (_selected == "codex") _snapshot = Decorate(source.Snapshot);
            }
            Changed?.Invoke();
        };
    }
    internal AiUsageSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    internal static string NormalizeSelection(string? id, bool showApi, IReadOnlyDictionary<string, bool>? enabledServices = null)
    {
        var provider = Catalog.FirstOrDefault(provider => provider.Id == id && enabledServices?.GetValueOrDefault(provider.ServiceId) == true)
            ?? Catalog.FirstOrDefault(provider => provider.Mode == "subscription" && enabledServices?.GetValueOrDefault(provider.ServiceId) == true);
        if (provider is null) return "";
        return !showApi && provider.Mode == "api" ? Catalog.First(item => item.ServiceId == provider.ServiceId && item.Mode == "subscription").Id : provider.Id;
    }
    internal void SetPreferences(string id, bool showApi, IReadOnlyDictionary<string, bool>? enabledServices = null)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var service in ServiceIds)
            {
                var enabled = enabledServices?.GetValueOrDefault(service) ?? _enabled[service];
                if (_enabled[service] == enabled) continue;
                _enabled[service] = enabled; changed = true;
                if (!enabled)
                {
                    foreach (var provider in Catalog.Where(provider => provider.ServiceId == service))
                    {
                        var entry = _entries[provider.Id]; entry.Generation++; entry.Cancellation?.Cancel(); entry.Configuration?.Cancel(); entry.Work = null; entry.Attempt = default;
                        if (entry.Snapshot.Status == "loading" && entry.BeforeRefresh is not null) entry.Snapshot = entry.BeforeRefresh;
                    }
                    if (service == "openai") { _codex.Dispose(); _codex = new CodexUsageService(); ConnectCodex(); }
                }
            }
            var normalized = NormalizeSelection(id, showApi, _enabled);
            if (_selected == normalized && _showApi == showApi && !changed) return;
            if (_showApi && !showApi)
                foreach (var provider in Catalog.Where(provider => provider.Mode == "api"))
                {
                    var entry = _entries[provider.Id]; entry.Generation++; entry.Cancellation?.Cancel(); entry.Configuration?.Cancel(); entry.Work = null; entry.Attempt = default;
                    if (entry.Snapshot.Status == "loading" && entry.BeforeRefresh is not null) entry.Snapshot = entry.BeforeRefresh;
                }
            _preferencesVersion++;
            _showApi = showApi; _selected = normalized;
            if (_selected.Length == 0) _snapshot = Disabled();
            else { _entries[_selected].WireSnapshot = null; _snapshot = Decorate(_entries[_selected].Snapshot); }
        }
        Changed?.Invoke();
    }
    internal AiUsageSnapshot Read(string? providerId = null, bool force = false)
    {
        lock (_gate)
        {
            if (providerId is not null) EnsureEnabled(RequireProvider(providerId));
            var id = providerId is null ? _selected : NormalizeSelection(providerId, _showApi, _enabled);
            if (id.Length == 0) return _snapshot;
            var entry = _entries[id];
            if (id == "codex")
            {
                var current = _codex.Read(force);
                if (current.Status == "loading" && entry.Snapshot.Status != "loading") entry.BeforeRefresh = entry.Snapshot;
                entry.Snapshot = current;
            }
            else if (!_stop.IsCancellationRequested && (entry.Work is null || entry.Work.IsCompleted) &&
                DateTimeOffset.UtcNow - entry.Attempt >= TimeSpan.FromSeconds(force ? 5 : 60))
            {
                entry.Attempt = DateTimeOffset.UtcNow;
                entry.Cancellation?.Dispose();
                entry.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                entry.Cancellation.CancelAfter(TimeSpan.FromSeconds(45));
                var token = entry.Cancellation.Token;
                var generation = ++entry.Generation;
                entry.BeforeRefresh = entry.Snapshot;
                entry.Snapshot = Empty(RequireProvider(id)) with { Status = "loading", HasCredential = entry.Snapshot.HasCredential, ProjectId = entry.Snapshot.ProjectId };
                entry.Work = Task.Run(() => RefreshAsync(id, generation, token));
            }
            // Explicit reads do not persist or switch the selected provider.
            if (id == _selected) _snapshot = Decorate(entry.Snapshot);
            if (id == _selected) return _snapshot;
            var reply = Decorate(entry.Snapshot);
            if (_snapshot.Revision <= reply.Revision)
            {
                _entries[_selected].WireSnapshot = null;
                _snapshot = Decorate(_entries[_selected].Snapshot);
            }
            return reply;
        }
    }
    private AiUsageSnapshot Decorate(AiUsageSnapshot snapshot)
    {
        var entry = _entries[snapshot.ProviderId];
        if (entry.WireSnapshot is not null && entry.WireSource == snapshot && entry.WireShowApi == _showApi && entry.WirePreferencesVersion == _preferencesVersion) return entry.WireSnapshot;
        entry.WireSource = snapshot; entry.WireShowApi = _showApi;
        entry.WirePreferencesVersion = _preferencesVersion;
        return entry.WireSnapshot = snapshot with { Revision = ++_revision, Providers = Catalog, ShowApi = _showApi,
            EnabledServices = new Dictionary<string, bool>(_enabled, StringComparer.Ordinal) };
    }
    private AiUsageSnapshot Disabled() => new("disabled", null, null, [], null, null, null)
    { ProviderId = "", ProviderName = "", Revision = ++_revision, Providers = Catalog, ShowApi = _showApi,
        EnabledServices = new Dictionary<string, bool>(_enabled, StringComparer.Ordinal), SourceLabel = "", QuotaNote = "请先启用需要显示的服务。" };
    private void EnsureEnabled(AiUsageProviderInfo provider)
    {
        if (!_enabled[provider.ServiceId] || provider.Mode == "api" && !_showApi) throw new InvalidOperationException("请先打开该服务及需要的 API 显示开关。");
    }
    private sealed class Registration(Action release) : IDisposable { public void Dispose() => release(); }
    private static AiUsageProviderInfo RequireProvider(string id) => Catalog.FirstOrDefault(provider => provider.Id == id)
        ?? throw new ArgumentException("未知的 AI 服务。");
    private static AiUsageSnapshot Empty(AiUsageProviderInfo provider) => new("idle", null, null, [], null, null, null)
    { ProviderId = provider.Id, ProviderName = provider.Name, SourceLabel = provider.Mode == "api" ? "官方 API" : "尚未连接", CredentialKind = Kind(provider.Id),
        HistoryNote = provider.Mode == "subscription" && provider.Id != "codex" ? "尚无可用的官方自动历史接口；可手动记录真实活动。" : "",
        QuotaNote = "未获取到剩余额度。", ActivityUnit = provider.Mode == "subscription" && provider.Id != "codex" ? "requests" : "tokens" };
    private static string Kind(string id) => id switch { "deepseek-api" => "apiKey", "openai-api" or "claude-api" => "adminKey", "gemini-api" => "serviceAccountJson", _ => "none" };
    private async Task RefreshAsync(string id, long generation, CancellationToken token)
    {
        var provider = RequireProvider(id);
        AiUsageSnapshot snapshot = Empty(provider);
        try
        {
            token.ThrowIfCancellationRequested();
            if (provider.Mode == "subscription")
                snapshot = ReadManual(id) ?? snapshot with { Status = "unsupported", QuotaNote = "网页订阅暂无可用的官方自动读取接口；请在官方用量页面查看，或手动记录。" };
            else
            {
                var credential = _credentials.Read(id);
                if (string.IsNullOrEmpty(credential)) snapshot = snapshot with { Status = "unconfigured", QuotaNote = "请先在设置中配置凭据。" };
                else
                {
                    token.ThrowIfCancellationRequested();
                    snapshot = await _readApi(id, credential, ReadProject(id), token).ConfigureAwait(false);
                    snapshot = snapshot with { HasCredential = true };
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            snapshot = Empty(provider) with { Status = "error", Error = error is OperationCanceledException ? "读取超时或已取消，请稍后刷新。" : "无法读取。请检查凭据、权限和网络连接。" };
        }
        if (id == "deepseek-api")
        {
            try
            {
                if (ReadManual(id) is { } manual)
                    snapshot = snapshot with { Days = manual.Days, PeakDailyTokens = manual.PeakDailyTokens, ActivityUtc = false,
                        UpdatedAt = snapshot.UpdatedAt ?? manual.UpdatedAt, Status = "partial",
                        HistoryNote = "Token 历史为手动记录；记录时间 " + (manual.UpdatedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "未知") + "。余额仅在官方 API 成功返回后显示。",
                        SourceLabel = snapshot.Balances.Count > 0 ? "官方余额 / 手动历史" : "手动历史 / 官方余额暂不可用" };
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException) { }
        }
        lock (_gate)
        {
            var entry = _entries[id];
            if (_stop.IsCancellationRequested || entry.Generation != generation || !_enabled[provider.ServiceId]) return;
            entry.Snapshot = snapshot with { ProviderId = provider.Id, ProviderName = provider.Name, CredentialKind = Kind(id),
                HasCredential = provider.Mode == "api" && _credentials.Exists(id), ProjectId = ReadProject(id) };
            entry.BeforeRefresh = null;
            if (_selected == id) _snapshot = Decorate(entry.Snapshot);
        }
        Changed?.Invoke();
    }
    internal async Task<AiUsageSnapshot> ConfigureAsync(JsonElement payload, CancellationToken token)
    {
        var id = Text(payload, "providerId"); var provider = RequireProvider(id);
        lock (_gate) EnsureEnabled(provider);
        if (provider.Mode != "api") throw new ArgumentException("此服务不使用 API 凭据。");
        var clear = Flag(payload, "clearCredential"); var credential = Text(payload, "credential");
        if (clear && !string.IsNullOrWhiteSpace(credential)) throw new ArgumentException("不能同时设置和清除凭据。");
        if (credential.Length > 128 * 1024 || credential.Any(character => character == '\0')) throw new ArgumentException("凭据格式无效。");
        if (id != "gemini-api" && credential.Any(char.IsControl)) throw new ArgumentException("密钥不能包含换行或控制字符。");
        if (id == "gemini-api" && !string.IsNullOrWhiteSpace(credential))
        {
            using var document = JsonDocument.Parse(credential);
            if (Text(document.RootElement, "type") != "service_account" || Text(document.RootElement, "private_key").Length == 0 || Text(document.RootElement, "client_email").Length == 0)
                throw new ArgumentException("需要有效的服务账号 JSON。");
        }
        var project = Text(payload, "projectId");
        if (project.Length > 128 || project.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-'))) throw new ArgumentException("项目 ID 格式无效。");
        if (!clear && string.IsNullOrWhiteSpace(credential) && project.Length == 0) return DecorateThreadSafe(_entries[id].Snapshot);
        AiUsageSnapshot? verified = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        lock (_gate) { EnsureEnabled(provider); _entries[id].Configuration = deadline; }
        using var registration = new Registration(() => { lock (_gate) if (ReferenceEquals(_entries[id].Configuration, deadline)) _entries[id].Configuration = null; });
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        if (!clear && (!string.IsNullOrWhiteSpace(credential) || project.Length > 0))
        {
            try
            {
                var candidate = !string.IsNullOrWhiteSpace(credential) ? credential.Trim()
                    : await Task.Run(() => _credentials.Read(id), deadline.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(candidate)) throw new InvalidOperationException();
                verified = await _readApi(id, candidate, project.Length > 0 ? project : ReadProject(id), deadline.Token).ConfigureAwait(false);
                if (verified.Status is not ("ready" or "partial")) throw new InvalidOperationException();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new InvalidOperationException("连接验证失败，已保留原凭据和配置。请检查凭据、权限和网络。", null); }
        }
        deadline.Token.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            deadline.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                EnsureEnabled(provider); deadline.Token.ThrowIfCancellationRequested();
                if (clear) _credentials.Write(id, null);
                else if (!string.IsNullOrWhiteSpace(credential)) _credentials.Write(id, credential.Trim());
                if (id == "gemini-api" && (project.Length > 0 || clear)) WriteProject(id, clear ? "" : project);
            }
        }, deadline.Token).ConfigureAwait(false);
        Invalidate(id);
        if (verified is not null)
        {
            lock (_gate)
            {
                var entry = _entries[id]; entry.Attempt = DateTimeOffset.UtcNow;
                entry.Snapshot = verified with { ProviderId = id, ProviderName = provider.Name, HasCredential = true, CredentialKind = Kind(id), ProjectId = ReadProject(id) };
                if (id == _selected) _snapshot = Decorate(entry.Snapshot);
            }
            Changed?.Invoke();
        }
        return verified is null ? Read(id, true) : id == _selected ? Snapshot : DecorateThreadSafe(_entries[id].Snapshot);
    }
    private void Invalidate(string id)
    {
        lock (_gate)
        {
            var entry = _entries[id]; entry.Generation++; entry.Cancellation?.Cancel(); entry.Work = null; entry.Attempt = default;
            entry.Snapshot = Empty(RequireProvider(id)) with { HasCredential = RequireProvider(id).Mode == "api" && _credentials.Exists(id), ProjectId = ReadProject(id) };
            if (id == _selected) _snapshot = Decorate(entry.Snapshot);
        }
    }
    internal async Task<AiUsageSnapshot> SaveManualAsync(JsonElement payload, CancellationToken token)
    {
        var id = Text(payload, "providerId"); var provider = RequireProvider(id);
        lock (_gate) EnsureEnabled(provider);
        if (provider.Mode != "subscription" && id != "deepseek-api" || id == "codex") throw new ArgumentException("此服务使用官方历史，不接受手动覆盖。");
        if (provider.Mode == "api" && payload.TryGetProperty("remainingPercent", out var apiRemaining) && apiRemaining.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("API 服务不接受手动剩余百分比。");
        var snapshot = ParseManual(payload, Empty(provider));
        await Task.Run(() => { token.ThrowIfCancellationRequested(); Directory.CreateDirectory(_manualDirectory);
            var path = Path.Combine(_manualDirectory, id + ".json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(new { recordedAt = snapshot.UpdatedAt, days = snapshot.Days!.Select(day => new { date = day.Date, tokens = day.Tokens }),
                remainingPercent = snapshot.Quotas.Count == 0 ? (double?)null : 100 - snapshot.Quotas[0].UsedPercent })); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token).ConfigureAwait(false);
        Invalidate(id);
        lock (_gate)
        {
            _entries[id].Snapshot = snapshot;
            if (provider.Mode == "subscription") _entries[id].Attempt = DateTimeOffset.UtcNow;
            if (id == _selected) _snapshot = Decorate(snapshot);
        }
        Changed?.Invoke(); return DecorateThreadSafe(snapshot);
    }
    internal static AiUsageSnapshot ParseManual(JsonElement payload, AiUsageSnapshot empty)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("days", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 3660)
            throw new ArgumentException("请提供不超过 3660 条每日记录。");
        var days = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            var date = Text(value, "date");
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                parsed > DateOnly.FromDateTime(DateTime.Now) || !value.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Number || !tokens.TryGetInt64(out var count) || count < 0 || count > 9007199254740991L)
                throw new ArgumentException("日期或数量无效；数量须在 0–9007199254740991 之间，不能记录未来日期。");
            if (!days.TryAdd(date, count)) throw new ArgumentException("同一天只能有一条记录。");
        }
        var quotas = new List<AiUsageQuota>();
        if (payload.TryGetProperty("remainingPercent", out var remaining) && remaining.ValueKind != JsonValueKind.Null)
        {
            if (remaining.ValueKind != JsonValueKind.Number || !remaining.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0 || percent > 100) throw new ArgumentException("剩余百分比必须在 0 到 100 之间。");
            quotas.Add(new("manual", "手动记录的剩余额度", 100 - percent, 0, null));
        }
        return empty with { Status = "ready", UpdatedAt = DateTimeOffset.UtcNow, Quotas = quotas,
            Days = days.Select(day => new AiUsageTokenDay(day.Key, day.Value)).ToArray(), PeakDailyTokens = days.Count == 0 ? null : days.Values.Max(),
            SourceLabel = "手动记录", HistoryNote = "仅包含用户输入的日期；其他日期未知，不代表全账户自动统计。", QuotaNote = "手动额度没有官方重置时间。" };
    }
    private AiUsageSnapshot? ReadManual(string id)
    {
        var path = Path.Combine(_manualDirectory, id + ".json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 512 * 1024) throw new IOException();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var recordedAt = DateTimeOffset.TryParse(Text(document.RootElement, "recordedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? (DateTimeOffset?)time : null;
        return ParseManual(document.RootElement, Empty(RequireProvider(id))) with { UpdatedAt = recordedAt };
    }
    private AiUsageSnapshot DecorateThreadSafe(AiUsageSnapshot snapshot) { lock (_gate) return Decorate(snapshot); }
    internal Task WaitForIdleAsync(string id) { lock (_gate) return _entries[id].Work ?? Task.CompletedTask; }
    private string ProjectPath(string id) => Path.Combine(_manualDirectory, id + "-project.txt");
    private string ReadProject(string id) => id == "gemini-api" && File.Exists(ProjectPath(id)) && new FileInfo(ProjectPath(id)).Length < 256 ? File.ReadAllText(ProjectPath(id)) : "";
    private void WriteProject(string id, string project) { Directory.CreateDirectory(_manualDirectory); File.WriteAllText(ProjectPath(id), project); }
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    internal static bool Flag(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
    public void Dispose()
    {
        lock (_gate) { if (_stop.IsCancellationRequested) return; _stop.Cancel(); foreach (var entry in _entries.Values) entry.Cancellation?.Cancel(); }
        _codex.Dispose();
    }
}
