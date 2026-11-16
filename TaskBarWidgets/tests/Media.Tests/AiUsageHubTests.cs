using System.IO;
using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class AiUsageHubTests
{
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static AiUsageSnapshot Empty => new("idle", null, null, [], null, null, null);
    private static readonly Dictionary<string, bool> Enabled = AiUsageHub.ServiceIds.ToDictionary(id => id, _ => true);
    [Theory]
    [InlineData("openai-api", "codex")]
    [InlineData("deepseek-api", "deepseek-web")]
    [InlineData("gemini-api", "gemini-web")]
    [InlineData("claude-api", "claude-web")]
    public void HidingApisSelectsMatchingSubscription(string id, string expected) => Assert.Equal(expected, AiUsageHub.NormalizeSelection(id, false, Enabled));

    [Fact]
    public void SwitchingUsesGlobalRevisionAndIdenticalPreferencesDoNotPublish()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-ai-test-" + Guid.NewGuid().ToString("N"));
        using var hub = new AiUsageHub(Path.Combine(root, "secrets"), Path.Combine(root, "history"), enabledServices: Enabled);
        var first = hub.Snapshot;
        hub.SetPreferences("claude-web", false); var second = hub.Snapshot;
        hub.SetPreferences("codex", false); var third = hub.Snapshot;
        hub.SetPreferences("codex", false);
        Assert.True(first.Revision < second.Revision && second.Revision < third.Revision);
        Assert.Equal(third.Revision, hub.Snapshot.Revision);
        Assert.Equal(8, third.Providers.Count);
        Assert.False(third.ShowApi);
    }
    [Fact]
    public async Task AllServicesDefaultOffAndDisabledServicesCannotReadOrMutate()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-ai-test-" + Guid.NewGuid().ToString("N"));
        var calls = 0;
        using var hub = new AiUsageHub(Path.Combine(root, "secrets"), Path.Combine(root, "history"), readApi:
            (_, _, _, _) => { calls++; return Task.FromResult(Empty); });
        Assert.Equal("disabled", hub.Read().Status);
        Assert.Equal("", hub.Snapshot.ProviderId);
        Assert.All(hub.Snapshot.EnabledServices.Values, Assert.False);
        Assert.Empty(hub.Snapshot.Quotas); Assert.Empty(hub.Snapshot.Balances); Assert.Null(hub.Snapshot.Days);
        Assert.Throws<InvalidOperationException>(() => hub.Read("codex"));
        Assert.Throws<InvalidOperationException>(() => hub.Read("deepseek-api"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.ConfigureAsync(Json("""{"providerId":"deepseek-api","credential":"not-saved"}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.SaveManualAsync(Json("""{"providerId":"claude-web","days":[]}"""), CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(root));
        Assert.Equal(new[] { "ChatGPT", "DeepSeek", "Gemini", "Claude" }, hub.Snapshot.Providers.Where(provider => provider.Mode == "subscription").Select(provider => provider.Name));
    }
    [Fact]
    public async Task TurningServiceOffCancelsPendingReadButRetainsSavedCredentialAndManualHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-ai-test-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<AiUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var secrets = Path.Combine(root, "secrets"); var history = Path.Combine(root, "history");
            var store = new AiUsageCredentialStore(secrets); store.Write("deepseek-api", "kept-key");
            using var hub = new AiUsageHub(secrets, history, "deepseek-api", true,
                (_, _, _, token) => { started.TrySetResult(token); return response.Task; }, Enabled);
            await hub.SaveManualAsync(Json("""{"providerId":"deepseek-api","days":[{"date":"2026-01-01","tokens":42}]}"""), CancellationToken.None);
            hub.Read(); var pending = hub.WaitForIdleAsync("deepseek-api"); var token = await started.Task;
            var off = Enabled.ToDictionary(pair => pair.Key, _ => false);
            hub.SetPreferences("deepseek-api", true, off);
            var disabledRevision = hub.Snapshot.Revision;
            Assert.True(token.IsCancellationRequested);
            response.SetResult(Empty with { Status = "ready", Balances = [new("CNY", "1", "balance")] });
            await pending;
            Assert.Equal("disabled", hub.Snapshot.Status); Assert.Equal(disabledRevision, hub.Snapshot.Revision);
            Assert.Equal("kept-key", store.Read("deepseek-api"));
            Assert.True(File.Exists(Path.Combine(history, "ai-manual", "deepseek-api.json")));
            var onlyClaude = off.ToDictionary(pair => pair.Key, pair => pair.Key == "claude");
            hub.SetPreferences("deepseek-api", true, onlyClaude);
            Assert.Equal("claude-web", hub.Snapshot.ProviderId);
            Assert.True(hub.Snapshot.Revision > disabledRevision);
            hub.SetPreferences("deepseek-api", true, Enabled);
            Assert.Equal("deepseek-api", hub.Snapshot.ProviderId);
            Assert.Equal(42, Assert.Single(hub.Snapshot.Days!).Tokens);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void ManualRecordsRejectDuplicatesFutureDatesAndNegativeCounts()
    {
        Assert.Throws<ArgumentException>(() => AiUsageHub.ParseManual(Json("""{"days":[{"date":"2026-01-01","tokens":2},{"date":"2026-01-01","tokens":3}]}"""), Empty));
        Assert.Throws<ArgumentException>(() => AiUsageHub.ParseManual(Json("""{"days":[{"date":"9999-01-01","tokens":0}]}"""), Empty));
        Assert.Throws<ArgumentException>(() => AiUsageHub.ParseManual(Json("""{"days":[{"date":"2026-01-01","tokens":-1}]}"""), Empty));
        var snapshot = AiUsageHub.ParseManual(Json("""{"days":[{"date":"2026-01-01","tokens":3000000000}],"remainingPercent":0}"""), Empty);
        Assert.Equal(3000000000L, Assert.Single(snapshot.Days!).Tokens);
        Assert.Equal(100, Assert.Single(snapshot.Quotas).UsedPercent);
        Assert.Null(snapshot.LifetimeTokens);
    }
    [Fact]
    public async Task FailedCandidateCredentialDoesNotReplaceSavedCredentialOrOldSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-ai-test-" + Guid.NewGuid().ToString("N"));
        var secrets = Path.Combine(root, "secrets");
        try
        {
            var store = new AiUsageCredentialStore(secrets); store.Write("deepseek-api", "previous-key");
            using var hub = new AiUsageHub(secrets, Path.Combine(root, "history"), "deepseek-api", true,
                (_, _, _, _) => throw new InvalidOperationException("private response that must not escape"), Enabled);
            var before = hub.Snapshot;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hub.ConfigureAsync(Json("""{"providerId":"deepseek-api","credential":"wrong-key"}"""), CancellationToken.None));
            Assert.DoesNotContain("private", error.Message);
            Assert.Equal("previous-key", store.Read("deepseek-api"));
            Assert.Equal(before, hub.Snapshot);
            Assert.DoesNotContain("previous-key", File.ReadAllText(Path.Combine(secrets, "ai-deepseek-api.bin")));
            Assert.Null(store.Read("openai-api"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ManualStorageWhitelistsFieldsAndKeepsRecordedTimestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hub = new AiUsageHub(Path.Combine(root, "secrets"), Path.Combine(root, "history"), "claude-web", enabledServices: Enabled);
            var snapshot = await hub.SaveManualAsync(Json("""{"providerId":"claude-web","days":[{"date":"2026-01-01","tokens":0}],"credential":"must-not-save","unexpected":"private"}"""), CancellationToken.None);
            var path = Path.Combine(root, "history", "ai-manual", "claude-web.json");
            var stored = File.ReadAllText(path);
            Assert.DoesNotContain("must-not-save", stored); Assert.DoesNotContain("private", stored);
            Assert.Equal(snapshot.UpdatedAt, hub.Read().UpdatedAt);
            var revision = hub.Read().Revision; Assert.Equal(revision, hub.Read().Revision);
            Assert.Equal("requests", snapshot.ActivityUnit);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task DisposedHubCannotCommitWhenCandidateReaderIgnoresCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-ai-test-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<AiUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var secrets = Path.Combine(root, "secrets");
            var store = new AiUsageCredentialStore(secrets);
            store.Write("deepseek-api", "previous-key");
            using var hub = new AiUsageHub(secrets, Path.Combine(root, "history"), "deepseek-api", true,
                (_, _, _, _) => { started.SetResult(); return response.Task; }, Enabled);
            var before = hub.Snapshot;
            var configure = hub.ConfigureAsync(Json("""{"providerId":"deepseek-api","credential":"candidate-key"}"""), CancellationToken.None);
            await started.Task;
            hub.Dispose();
            response.SetResult(Empty with { Status = "partial" });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configure);
            Assert.Equal("previous-key", store.Read("deepseek-api"));
            Assert.Equal(before, hub.Snapshot);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void OfficialParsersKeepMoneySeparateAndDoNotDoubleCountCachedInput()
    {
        var balance = AiUsageApiReader.ParseDeepSeek(Json("""{"is_available":false,"balance_infos":[{"currency":"CNY","total_balance":"0.0000"}]}"""));
        Assert.Equal("0.0000", Assert.Single(balance.Balances).Available);
        Assert.Empty(balance.Quotas); Assert.Null(balance.Days); Assert.Contains("不足", balance.QuotaNote);
        var days = AiUsageApiReader.ParseDays("openai-api", Json("""{"data":[{"start_time":1767225600,"results":[{"input_tokens":3000000000,"output_tokens":10,"input_cached_tokens":100}]}]}"""));
        Assert.Equal(3000000010L, Assert.Single(days).Tokens);
        var claude = AiUsageApiReader.ParseDays("claude-api", Json("""{"data":[{"starting_at":"2026-01-01T00:00:00Z","usage":{"total_tokens":42}}]}"""));
        Assert.Equal(42, Assert.Single(claude).Tokens);
        var precise = AiUsageApiReader.ParseDeepSeek(Json("""{"balance_infos":[{"currency":"USD","total_balance":"0.00000000000000000000000000001"}]}"""));
        Assert.Equal("0.00000000000000000000000000001", Assert.Single(precise.Balances).Available);
    }

    [Fact]
    public void WireSnapshotCarriesExplicitHistoryCalendarAndManualDefaultsToLocal()
    {
        var automatic = JsonSerializer.SerializeToElement(CardSnapshotBuilder.DescribeAiUsage(Empty with { ActivityUtc = true }));
        Assert.True(automatic.GetProperty("activityUtc").GetBoolean());
        var manual = AiUsageHub.ParseManual(Json("""{"days":[{"date":"2026-01-01","tokens":1}]}"""), Empty);
        var wire = JsonSerializer.SerializeToElement(CardSnapshotBuilder.DescribeAiUsage(manual));
        Assert.False(wire.GetProperty("activityUtc").GetBoolean());
    }

    [Fact]
    public async Task ManualHistorySurvivesBalanceFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var secrets = Path.Combine(root, "secrets"); new AiUsageCredentialStore(secrets).Write("deepseek-api", "test-key");
            using var hub = new AiUsageHub(secrets, Path.Combine(root, "history"), "deepseek-api", true,
                (_, _, _, _) => throw new System.Net.Http.HttpRequestException(), Enabled);
            await hub.SaveManualAsync(Json("""{"providerId":"deepseek-api","days":[{"date":"2026-01-01","tokens":42}]}"""), CancellationToken.None);
            hub.Read(); await hub.WaitForIdleAsync("deepseek-api");
            Assert.Equal(42, Assert.Single(hub.Snapshot.Days!).Tokens);
            Assert.Empty(hub.Snapshot.Balances);
            Assert.Equal("partial", hub.Snapshot.Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
