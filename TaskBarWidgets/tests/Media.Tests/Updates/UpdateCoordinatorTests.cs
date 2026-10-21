using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskBarWidgets.Media.Updates;
using Xunit;

namespace TaskBarWidgets.Media.Tests.Updates;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public void PublishedSettingsResourcesAreAllowedWithoutAllowingPersonalSettings()
    {
        foreach (var resource in new[] { "UI/Settings/SettingsThemeResources.xbf", "UI/Resources/SettingsNavigationResources.xbf", "Media/Web/Settings/bridge.js", "vccorlib140.dll", "release-manifest.json" })
            Assert.Equal(resource, UpdatePackage.ValidateRelativePath(resource));
        foreach (var personal in new[] { "UI/Settings/user.json", "UI/Settings/SettingsNavigationResources.xbf", "Media/Web/Settings/settings.json", "UI/Data/record.db", "licenses/private.db" })
            Assert.Throws<InvalidDataException>(() => UpdatePackage.ValidateRelativePath(personal));
    }

    [Fact]
    public async Task ValidPackageIsStagedRecoveredAndRevalidatedBeforeInstallation()
    {
        await using var test = new Scenario();
        var coordinator = test.Create();
        coordinator.Check();
        await coordinator.WaitForIdleAsync();
        Assert.Equal("available", coordinator.Snapshot.Phase);
        Assert.Equal(3, test.Requests.Count);
        coordinator.Download();
        await coordinator.WaitForIdleAsync();
        Assert.Equal("ready", coordinator.Snapshot.Phase);
        Assert.True(coordinator.Snapshot.CanInstall);
        Assert.Equal(test.Zip.LongLength, coordinator.Snapshot.BytesDownloaded);
        Assert.All(test.Requests, request => Assert.True(request.HasUserAgent));
        Assert.True(test.Changes.Select(snapshot => snapshot.Revision).Zip(test.Changes.Skip(1).Select(snapshot => snapshot.Revision))
            .All(pair => pair.First < pair.Second));
        await coordinator.DisposeAsync();

        var recovered = test.Create();
        recovered.Start();
        var restoration = recovered.Initialization;
        await restoration;
        Assert.Equal("ready", recovered.Snapshot.Phase);
        Assert.Equal(4, test.Requests.Count); // Recovery never downloads a persisted package again.
        var prepared = await recovered.PrepareInstall();
        Assert.Equal(Scenario.Latest, prepared.Version);
        Assert.Equal(test.CacheRoot, prepared.CacheRoot);
        Assert.Equal("new executable", await File.ReadAllTextAsync(Path.Combine(prepared.StageRoot, "TaskBarWidgets.exe")));
        Assert.Equal("preparing", recovered.Check().Phase);
        Assert.Equal("preparing", recovered.Download().Phase);
        Assert.False(recovered.Snapshot.CanInstall);
        Assert.Same(restoration, recovered.Initialization);
    }

    [Fact]
    public async Task TamperedReadyFilesCannotBePreparedOrRecovered()
    {
        await using var test = new Scenario();
        var coordinator = test.Create();
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        coordinator.Download(); await coordinator.WaitForIdleAsync();
        var stageFile = Directory.GetFiles(test.CacheRoot, "TaskBarWidgets.exe", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(stageFile, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.PrepareInstall());
        await coordinator.WaitForIdleAsync();
        Assert.Equal("error", coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.CanInstall);
        await coordinator.DisposeAsync();
        var recovered = test.Create();
        recovered.Start(); await recovered.WaitForIdleAsync();
        Assert.False(recovered.Snapshot.CanInstall);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("protocol")]
    [InlineData("checksum")]
    [InlineData("size")]
    [InlineData("foreign-asset")]
    public async Task UnsupportedOrInconsistentMetadataOffersOnlyTrustedManualRelease(string problem)
    {
        await using var test = new Scenario { MetadataProblem = problem };
        var coordinator = test.Create();
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        Assert.Equal("manualRequired", coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.CanDownload);
        Assert.False(coordinator.Snapshot.CanInstall);
        Assert.Equal(Scenario.ReleaseUrl, coordinator.Snapshot.ReleaseUrl);
        Assert.DoesNotContain(test.Requests, request => request.Url.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Scenario.Latest)]
    [InlineData("271001-1200")]
    public async Task SameOrOlderReleaseNeverDownloads(string current)
    {
        await using var test = new Scenario();
        var coordinator = test.Create(current);
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        Assert.Equal("idle", coordinator.Snapshot.Phase);
        Assert.Single(test.Requests);
        Assert.False(coordinator.Snapshot.CanDownload);
    }

    [Fact]
    public async Task RateLimitIsPersistedAndManualCheckHonorsRetryAfter()
    {
        await using var test = new Scenario { RateLimited = true };
        var coordinator = test.Create();
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        Assert.Equal("error", coordinator.Snapshot.Phase);
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        Assert.Single(test.Requests);
        await coordinator.DisposeAsync();
        var recovered = test.Create();
        recovered.Start(); await recovered.WaitForIdleAsync();
        recovered.Check(); await recovered.WaitForIdleAsync();
        Assert.Single(test.Requests);
    }

    [Fact]
    public async Task CancelAndSingleFlightLeaveNoReadyOrPartialPackage()
    {
        await using var test = new Scenario { BlockZip = true };
        var coordinator = test.Create();
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        coordinator.Download();
        await test.ZipRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Download(); coordinator.Check();
        Assert.Single(test.Requests, request => request.Url.EndsWith(".zip", StringComparison.Ordinal));
        coordinator.CancelDownload(); await coordinator.WaitForIdleAsync();
        Assert.Equal("available", coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.CanInstall);
        Assert.Empty(Directory.GetFiles(test.CacheRoot, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DisablingAutomaticChecksDoesNotCancelAnExplicitDownload()
    {
        await using var test = new Scenario { BlockZip = true };
        var coordinator = test.Create();
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        coordinator.Download();
        await test.ZipRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.SetAutomatic(false);
        test.UnblockZip.TrySetResult();
        await coordinator.WaitForIdleAsync();
        Assert.Equal("ready", coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.AutomaticEnabled);
    }

    [Fact]
    public async Task DisablingAutomaticChecksCancelsAnAutomaticDownload()
    {
        await using var test = new Scenario { BlockZip = true };
        var coordinator = test.Create();
        coordinator.Check(false);
        await test.ZipRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.SetAutomatic(false);
        await coordinator.WaitForIdleAsync();
        Assert.Equal("available", coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.CanInstall);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("manifest-hash")]
    [InlineData("file-hash")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("case-collision")]
    [InlineData("directory-case")]
    [InlineData("file-directory")]
    [InlineData("personal")]
    [InlineData("license-personal")]
    [InlineData("unlisted")]
    [InlineData("symlink")]
    [InlineData("device")]
    [InlineData("ads")]
    public async Task UntrustedPackagesCannotBecomeReady(string problem)
    {
        await using var test = new Scenario(problem);
        var coordinator = test.Create();
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        Assert.Equal("available", coordinator.Snapshot.Phase);
        coordinator.Download(); await coordinator.WaitForIdleAsync();
        Assert.Equal("error", coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.CanInstall);
        Assert.Empty(Directory.GetFiles(test.CacheRoot, "TaskBarWidgets.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AutomaticPackageIntegrityFailureRemainsAnErrorWithADownloadRetry()
    {
        await using var test = new Scenario("hash");
        var coordinator = test.Create();
        coordinator.Check(false); await coordinator.WaitForIdleAsync();
        Assert.Equal("error", coordinator.Snapshot.Phase);
        Assert.True(coordinator.Snapshot.CanDownload);
        Assert.False(coordinator.Snapshot.CanInstall);
    }

    [Fact]
    public async Task ForeignRedirectAndOversizedMetadataAreRejectedBeforeUse()
    {
        await using var redirected = new Scenario { ForeignRedirect = true };
        var coordinator = redirected.Create();
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        Assert.Equal("error", coordinator.Snapshot.Phase);
        Assert.Single(redirected.Requests);
        await using var oversized = new Scenario { OversizedMetadata = true };
        coordinator = oversized.Create();
        coordinator.Check(); await coordinator.WaitForIdleAsync();
        Assert.Equal("manualRequired", coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.CanDownload);
    }

    [Fact]
    public async Task StartupWaitsSixtySecondsAndPersistsDailyScheduleWhileManualChecksBypassIt()
    {
        var clock = new TestClock();
        await using var test = new Scenario();
        var coordinator = test.Create(Scenario.Latest, clock);
        coordinator.Start(); await coordinator.WaitForIdleAsync();
        await Eventually(() => clock.PendingTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(test.Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => test.Requests.Count == 1);
        await coordinator.WaitForIdleAsync();
        await Eventually(() => clock.PendingTimers > 0);
        Assert.Equal(clock.GetUtcNow(), coordinator.Snapshot.LastCheckedAt);
        await coordinator.DisposeAsync();

        clock.Advance(TimeSpan.FromHours(1));
        var recovered = test.Create(Scenario.Latest, clock);
        recovered.Start(); await recovered.WaitForIdleAsync();
        await Eventually(() => clock.PendingTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(60));
        await Eventually(() => clock.PendingTimers > 0);
        Assert.Single(test.Requests);
        recovered.Check(); await recovered.WaitForIdleAsync();
        Assert.Equal(2, test.Requests.Count);
        await Eventually(() => clock.PendingTimers > 0);
        clock.Advance(TimeSpan.FromHours(24));
        await Eventually(() => test.Requests.Count == 3);
        await recovered.WaitForIdleAsync();
    }

    [Fact]
    public async Task ReadyPackageIsReusedDailyAndReplacedWhenANewerReleaseAppears()
    {
        var clock = new TestClock();
        await using var test = new Scenario();
        var coordinator = test.Create(clock: clock);
        coordinator.Start(); await coordinator.WaitForIdleAsync();
        await Eventually(() => clock.PendingTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(60));
        await Eventually(() => coordinator.Snapshot.Phase == "ready");
        await coordinator.WaitForIdleAsync();
        var firstStage = Directory.GetFiles(test.CacheRoot, "app-files.json", SearchOption.AllDirectories).Single();
        Assert.Single(test.Requests, request => request.Url.EndsWith(".zip", StringComparison.Ordinal));

        await Eventually(() => clock.PendingTimers > 0);
        clock.Advance(TimeSpan.FromHours(24));
        await Eventually(() => test.Requests.Count(request => request.Url.EndsWith("/latest", StringComparison.Ordinal)) == 2);
        await coordinator.WaitForIdleAsync();
        Assert.Equal("ready", coordinator.Snapshot.Phase);
        Assert.Equal(firstStage, Directory.GetFiles(test.CacheRoot, "app-files.json", SearchOption.AllDirectories).Single());
        Assert.Single(test.Requests, request => request.Url.EndsWith(".zip", StringComparison.Ordinal));

        const string newer = "261020-1200";
        test.Publish(newer);
        await Eventually(() => clock.PendingTimers > 0);
        clock.Advance(TimeSpan.FromHours(24));
        await Eventually(() => coordinator.Snapshot.LatestVersion == newer && coordinator.Snapshot.Phase == "ready");
        await coordinator.WaitForIdleAsync();
        Assert.Equal(2, test.Requests.Count(request => request.Url.EndsWith(".zip", StringComparison.Ordinal)));
        Assert.DoesNotContain(test.Changes, state => state.Phase == "installing");
        var prepared = await coordinator.PrepareInstall();
        Assert.Equal(newer, prepared.Version);
        Assert.Contains(newer, await File.ReadAllTextAsync(Path.Combine(prepared.StageRoot, "TaskBarWidgets.exe")));
    }

    [Fact]
    public async Task CancellingRefreshPreservesTheActualReadyVersion()
    {
        await using var test = new Scenario();
        var coordinator = test.Create();
        coordinator.Check(false); await coordinator.WaitForIdleAsync();
        Assert.Equal("ready", coordinator.Snapshot.Phase);
        test.Publish("261020-1200");
        test.BlockMetadata = true;
        test.MetadataRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Check(false);
        await test.MetadataRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("261020-1200", coordinator.Snapshot.LatestVersion);
        coordinator.SetAutomatic(false);
        await coordinator.WaitForIdleAsync();
        Assert.Equal("ready", coordinator.Snapshot.Phase);
        Assert.Equal(Scenario.Latest, coordinator.Snapshot.LatestVersion);
        Assert.Equal(Scenario.Latest, (await coordinator.PrepareInstall()).Version);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData("261018-1408", true)]
    [InlineData("000229-0000", true)]
    [InlineData("260229-1234", false)]
    [InlineData("261018-2400", false)]
    [InlineData("v261018-1408", false)]
    [InlineData("261018-1408+commit", false)]
    public void ReleaseVersionsAreActualBuildTimestamps(string value, bool valid) => Assert.Equal(valid, UpdateVersion.TryParse(value, out _));

    private sealed record ObservedRequest(string Url, bool HasUserAgent);

    private sealed class Scenario : IAsyncDisposable
    {
        internal const string Latest = "261019-1200";
        internal const string ReleaseUrl = "https://github.com/NoctisWang528/TaskBarWidget/releases/tag/" + Latest;
        private string _latest = Latest;
        private string AssetName => "TaskBarWidgets-" + _latest + "-win-x64.zip";
        private string AssetPrefix => "https://github.com/NoctisWang528/TaskBarWidget/releases/download/" + _latest + "/";
        private string CurrentReleaseUrl => "https://github.com/NoctisWang528/TaskBarWidget/releases/tag/" + _latest;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "TaskBarWidgets-UpdateTests-" + Guid.NewGuid().ToString("N"));
        private readonly List<UpdateCoordinator> _coordinators = new();
        private readonly string _problem;
        private byte[] _manifest = [];
        internal byte[] Zip { get; private set; } = [];
        internal string CacheRoot => Path.Combine(_root, "cache");
        internal readonly ConcurrentQueue<ObservedRequest> Requests = new();
        internal readonly ConcurrentQueue<UpdateSnapshot> Changes = new();
        internal readonly TaskCompletionSource ZipRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource UnblockZip = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MetadataRequested { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool BlockMetadata { get; set; }
        internal string? MetadataProblem { get; init; }
        internal bool RateLimited { get; init; }
        internal bool BlockZip { get; init; }
        internal bool ForeignRedirect { get; init; }
        internal bool OversizedMetadata { get; init; }

        internal Scenario(string problem = "")
        {
            _problem = problem;
            Directory.CreateDirectory(Path.Combine(_root, "installation"));
            BuildPackage();
        }

        internal void Publish(string version) { _latest = version; BuildPackage(); }

        private void BuildPackage()
        {
            var problem = _problem;
            var content = Encoding.UTF8.GetBytes(_latest == Latest ? "new executable" : "new executable " + _latest);
            var program = problem switch { "personal" => "Media/settings.json", "license-personal" => "licenses/private.db", "device" => "Media/CON.txt", "ads" => "Media/file:stream", _ => "TaskBarWidgets.exe" };
            var entries = new List<(string Path, byte[] Content)> { (program, content) };
            if (problem == "case-collision") entries.Add(("taskbarwidgets.EXE", content));
            if (problem == "directory-case") { entries.Add(("Media/one.dll", content)); entries.Add(("media/two.dll", content)); }
            if (problem == "file-directory") { entries.Add(("UI/file.dll", content)); entries.Add(("UI/file.dll/nested.dll", content)); }
            _manifest = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, version = _latest, architecture = "win-x64",
                files = entries.Select(item => new { path = item.Path, bytes = item.Content.Length, sha256 = problem == "file-hash" ? new string('0', 64) : Hash(item.Content) }) });
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                void Add(string path, byte[] bytes, bool link = false)
                {
                    var entry = archive.CreateEntry(Path.GetFileNameWithoutExtension(AssetName) + "/" + path);
                    if (link) entry.ExternalAttributes = 0xA1FF << 16;
                    using var stream = entry.Open(); stream.Write(bytes);
                }
                Add("app-files.json", _manifest);
                foreach (var entry in entries) Add(entry.Path, entry.Content, problem == "symlink");
                if (problem == "duplicate") Add(program, content);
                if (problem == "traversal") Add("../escape.exe", content);
                if (problem == "unlisted") Add("UI/unlisted.dll", content);
            }
            Zip = buffer.ToArray();
        }

        internal UpdateCoordinator Create(string current = "261018-1200", TimeProvider? clock = null)
        {
            var coordinator = new UpdateCoordinator(Path.Combine(_root, "installation"), current, true, Changes.Enqueue,
                CacheRoot, new SyntheticHandler(Respond), clock);
            _coordinators.Add(coordinator);
            return coordinator;
        }

        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Enqueue(new(url, request.Headers.UserAgent.Count != 0));
            if (ForeignRedirect) return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/update") } };
            if (RateLimited) return new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1)) } };
            if (url.EndsWith("/latest", StringComparison.Ordinal))
            {
                object Asset(string name, long size) => new { name, size, browser_download_url = MetadataProblem == "foreign-asset" ? "https://evil.example/" + name : AssetPrefix + name };
                var assets = new List<object> { Asset(AssetName, MetadataProblem == "size" ? Zip.Length + 1 : Zip.Length), Asset("SHA256SUMS.txt", Encoding.UTF8.GetByteCount(Sums())) };
                if (MetadataProblem != "missing") assets.Add(Asset("update.json", Metadata().Length));
                return Json(new { tag_name = _latest, html_url = CurrentReleaseUrl, draft = false, prerelease = false, body = "Release notes", assets });
            }
            if (url.EndsWith("update.json", StringComparison.Ordinal))
            {
                MetadataRequested.TrySetResult();
                if (BlockMetadata) await Task.Delay(Timeout.Infinite, token);
                var response = Bytes(Metadata());
                if (OversizedMetadata) response.Content.Headers.ContentLength = 65537;
                return response;
            }
            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)) return Bytes(Encoding.UTF8.GetBytes(Sums()));
            if (url.EndsWith(".zip", StringComparison.Ordinal))
            {
                ZipRequested.TrySetResult();
                if (BlockZip) await UnblockZip.Task.WaitAsync(token);
                return Bytes(Zip);
            }
            throw new InvalidOperationException("Unexpected synthetic request: " + url);
        }

        private byte[] Metadata() => JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = MetadataProblem == "protocol" ? 2 : 1,
            version = _latest, architecture = "win-x64", assetName = AssetName, size = Zip.Length,
            sha256 = _problem == "hash" ? new string('0', 64) : Hash(Zip),
            fileManifestSha256 = _problem == "manifest-hash" ? new string('0', 64) : Hash(_manifest),
            requirements = new { dotnetDesktop = "8.0.0", windowsAppRuntime = "2.5.1.0", windowsBuild = 22000 } });
        private string Sums() => (MetadataProblem == "checksum" || _problem == "hash" ? new string('0', 64) : Hash(Zip)) + "  " + AssetName + "\n";
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        private static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));
        private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        public async ValueTask DisposeAsync()
        {
            foreach (var coordinator in _coordinators) await coordinator.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }

    private sealed class SyntheticHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        private readonly List<TestTimer> _timers = new();
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        internal int PendingTimers { get { lock (_gate) return _timers.Count(timer => timer.Due.HasValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new TestTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        internal void Advance(TimeSpan duration)
        {
            List<TestTimer> due;
            lock (_gate)
            {
                _now += duration;
                due = _timers.Where(timer => timer.Due <= _now).ToList();
                foreach (var timer in due) timer.Due = null;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class TestTimer(TestClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal DateTimeOffset? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (clock._gate) Due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
