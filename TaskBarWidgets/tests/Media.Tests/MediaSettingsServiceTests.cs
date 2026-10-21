using System.IO;
using System.Text.Json;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;
using TaskbarLyrics.Core.Utilities;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

[CollectionDefinition("MediaSettingsServiceSerial", DisableParallelization = true)]
public sealed class MediaSettingsServiceSerialCollection;

internal static class MediaSettingsServiceTestFactory
{
    internal static MediaSettingsService Create(
        AppSettings settings,
        TrackLyricOffsetStore? offsets = null,
        Func<Task<CurrentTrackLyricsContext?>>? getCurrentTrackLyricsContext = null,
        Func<LyricDiagnosticRunner>? createLyricDiagnosticRunner = null,
        Func<TrackInfo, ResolvedLyrics, CancellationToken, Task<bool>>? tryApplyResolvedLyrics = null,
        Func<TrackInfo, ResolvedLyrics, CancellationToken, Task<ResolvedLyricCacheStoreResult>>? rememberResolvedLyrics = null,
        Action? clearCache = null,
        Func<AppSettings, bool>? saveSettings = null,
        Action<AppSettings>? previewSettings = null,
        Action? retrySpectrumCapture = null,
        Func<bool>? isApplicationUsingLightTheme = null,
        Func<bool>? isSystemUiUsingLightTheme = null,
        Func<IReadOnlyList<DisplayMonitor>>? getDisplays = null,
        Func<double>? getPrimaryDpiScale = null,
        Func<double>? getWorkAreaWidth = null,
        Func<CancellationToken, Task<IReadOnlyList<AvailablePlayerSession>>>? discoverPlayerSessions = null)
    {
        return new MediaSettingsService(
            settings,
            offsets,
            getCurrentTrackLyricsContext ?? (() => Task.FromResult<CurrentTrackLyricsContext?>(null)),
            createLyricDiagnosticRunner ?? (() => throw new NotSupportedException()),
            tryApplyResolvedLyrics ?? ((_, _, _) => throw new NotSupportedException()),
            rememberResolvedLyrics ?? ((_, _, _) => throw new NotSupportedException()),
            clearCache ?? (() => { }),
            saveSettings ?? (_ => true),
            previewSettings,
            retrySpectrumCapture,
            isApplicationUsingLightTheme,
            isSystemUiUsingLightTheme,
            getDisplays,
            getPrimaryDpiScale,
            getWorkAreaWidth,
            discoverPlayerSessions);
    }
}

[Collection("MediaSettingsServiceSerial")]
public sealed class MediaSettingsServiceTests
{
    [Fact]
    public async Task SourceReorderSpectrumAuthorizationAndTuningUseStructuredEvents()
    {
        AppSettings? saved = null;
        var retryCount = 0;
        using var service = MediaSettingsServiceTestFactory.Create(
            new AppSettings(),
            saveSettings: settings =>
            {
                saved = settings;
                return true;
            },
            retrySpectrumCapture: () => retryCount++,
            getDisplays: () => [],
            getPrimaryDpiScale: () => 1,
            getWorkAreaWidth: () => 1920);

        var reordered = await service.ExecuteServiceCommandAsync(
            new AppSettings(),
            "reorderSources",
            JsonSerializer.SerializeToElement(new[] { "Browser", "QQMusic", "UnknownPlayer" }));
        var order = reordered.GetProperty("settings")
            .GetProperty("sourceRecognitionOrder")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        Assert.Equal("Browser", order[0]);
        Assert.Equal("QQMusic", order[1]);
        Assert.DoesNotContain("UnknownPlayer", order);
        Assert.Contains(
            reordered.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "settingsSaveResult");
        Assert.Equal(
            1,
            reordered.GetProperty("events")
                .EnumerateArray()
                .First()
                .GetProperty("version")
                .GetInt32());

        var confirmed = await service.ExecuteServiceCommandAsync(
            new AppSettings(),
            "confirmSpectrumAudioAccess",
            JsonSerializer.SerializeToElement("Always"));
        Assert.True(confirmed.GetProperty("settings").GetProperty("spectrumAudioAccessGranted").GetBoolean());
        Assert.Equal(
            "Always",
            confirmed.GetProperty("settings").GetProperty("spectrumDisplayMode").GetString());
        Assert.Contains(
            confirmed.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "spectrumCaptureState");

        var tuned = service.ApplyServiceSettings(
            new AppSettings { SpectrumAudioAccessGranted = true, SpectrumDisplayMode = SpectrumDisplayMode.Always },
            JsonSerializer.SerializeToElement(new
            {
                spectrumTuning = new { sampleWindow = 1024, barCount = 12 }
            }));
        Assert.Equal(1024, tuned.GetProperty("spectrumTuning").GetProperty("sampleWindow").GetInt32());
        Assert.Equal(12, tuned.GetProperty("spectrumTuning").GetProperty("barCount").GetInt32());

        await service.ExecuteServiceCommandAsync(new AppSettings(), "retrySpectrumCapture", null);
        Assert.Equal(1, retryCount);

        var revoked = await service.ExecuteServiceCommandAsync(
            new AppSettings { SpectrumAudioAccessGranted = true, SpectrumDisplayMode = SpectrumDisplayMode.Always },
            "revokeSpectrumAudioAccess",
            null);
        Assert.False(revoked.GetProperty("settings").GetProperty("spectrumAudioAccessGranted").GetBoolean());
        Assert.Equal(
            "Disabled",
            revoked.GetProperty("settings").GetProperty("spectrumDisplayMode").GetString());
        Assert.NotNull(saved);
    }

    [Fact]
    public async Task CustomSourceAddFailureAndUpdateRemoveRemainContained()
    {
        var settings = new AppSettings();
        Assert.True(settings.AddCustomPlayerSource("custom.app", "Old name"));
        using var service = MediaSettingsServiceTestFactory.Create(
            settings,
            saveSettings: _ => true,
            getDisplays: () => [],
            getPrimaryDpiScale: () => 1,
            getWorkAreaWidth: () => 1920);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteServiceCommandAsync(
                settings,
                "addPlayerSource",
                JsonSerializer.SerializeToElement(new
                {
                    sourceAppUserModelId = "custom.new",
                    displayName = "New player"
                })));

        var updated = await service.ExecuteServiceCommandAsync(
            settings,
            "updatePlayerSource",
            JsonSerializer.SerializeToElement(new
            {
                sourceAppUserModelId = "custom.app",
                displayName = "Updated name"
            }));
        var custom = updated.GetProperty("settings")
            .GetProperty("customPlayerSources")
            .EnumerateArray()
            .Single(item => item.GetProperty("sourceAppUserModelId").GetString() == "custom.app");
        Assert.Equal("Updated name", custom.GetProperty("displayName").GetString());

        var removed = await service.ExecuteServiceCommandAsync(
            settings,
            "removePlayerSource",
            JsonSerializer.SerializeToElement("custom.app"));
        Assert.Empty(
            removed.GetProperty("settings")
                .GetProperty("customPlayerSources")
                .EnumerateArray());
    }

    [Fact]
    public async Task DiscoveredCustomPlayerCanBeAddedAndReadBack()
    {
        var settings = new AppSettings();
        var discovered = new[]
        {
            new AvailablePlayerSession(
                "custom.new",
                "New player",
                "Playing track",
                true,
                false,
                string.Empty)
        };
        using var service = MediaSettingsServiceTestFactory.Create(
            settings,
            saveSettings: _ => true,
            discoverPlayerSessions: _ =>
                Task.FromResult<IReadOnlyList<AvailablePlayerSession>>(discovered),
            getDisplays: () => [],
            getPrimaryDpiScale: () => 1,
            getWorkAreaWidth: () => 1920);

        var discovery = await service.ExecuteServiceCommandAsync(
            settings,
            "discoverPlayerSessions",
            null);
        var sessions = discovery.GetProperty("events")
            .EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "playerSessions")
            .GetProperty("payload");
        Assert.Equal("ready", sessions.GetProperty("status").GetString());
        Assert.Single(sessions.GetProperty("sessions").EnumerateArray());

        var added = await service.ExecuteServiceCommandAsync(
            settings,
            "addPlayerSource",
            JsonSerializer.SerializeToElement(new
            {
                sourceAppUserModelId = "custom.new",
                displayName = "New player",
                enabled = true
            }));
        var custom = added.GetProperty("settings")
            .GetProperty("customPlayerSources")
            .EnumerateArray()
            .Single();
        Assert.Equal("custom.new", custom.GetProperty("sourceAppUserModelId").GetString());
        Assert.Contains(
            "custom.new",
            added.GetProperty("settings")
                .GetProperty("sourceRecognitionOrder")
                .EnumerateArray()
                .Select(item => item.GetString()));
    }

    [Fact]
    public async Task SaveFailureIsReportedWithoutClaimingSuccess()
    {
        using var service = MediaSettingsServiceTestFactory.Create(
            new AppSettings(),
            saveSettings: _ => false,
            getDisplays: () => [],
            getPrimaryDpiScale: () => 1,
            getWorkAreaWidth: () => 1920);

        Assert.Throws<IOException>(
            () => service.ApplyServiceSettings(
                new AppSettings(),
                JsonSerializer.SerializeToElement(new { fontSize = 20 })));
        await Assert.ThrowsAsync<IOException>(
            () => service.ExecuteServiceCommandAsync(
                new AppSettings(),
                "resetLyricsLayoutBase",
                null));
    }

    [Fact]
    public void PreviewAppliesToThePreviewCallbackWithoutSaving()
    {
        var saveCount = 0;
        AppSettings? preview = null;
        using var service = MediaSettingsServiceTestFactory.Create(
            new AppSettings(),
            saveSettings: _ =>
            {
                saveCount++;
                return true;
            },
            previewSettings: settings => preview = settings,
            getDisplays: () => [],
            getPrimaryDpiScale: () => 1,
            getWorkAreaWidth: () => 1920);

        var payload = service.PreviewServiceSettings(
            new AppSettings(),
            JsonSerializer.SerializeToElement(new { backgroundOpacity = 0.42 }));

        Assert.Equal(0, saveCount);
        Assert.NotNull(preview);
        Assert.Equal(0.42, preview!.BackgroundOpacity, 6);
        Assert.Equal(0.42, payload.GetProperty("backgroundOpacity").GetDouble());
    }

    [Fact]
    public async Task LyricDiagnosticsRejectsEmptyTrackAndCancelsOnDispose()
    {
        var runnerCreated = false;
        using (var emptyService = MediaSettingsServiceTestFactory.Create(
                   new AppSettings(),
                   createLyricDiagnosticRunner: () =>
                   {
                       runnerCreated = true;
                       throw new NotSupportedException();
                   },
                   getDisplays: () => [],
                   getPrimaryDpiScale: () => 1,
                   getWorkAreaWidth: () => 1920))
        {
            var reply = await emptyService.ExecuteServiceCommandAsync(
                new AppSettings(),
                "runLyricDiagnostics",
                null);
            var diagnostic = reply.GetProperty("events")
                .EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "lyricDiagnosticsState")
                .GetProperty("payload");
            Assert.Equal("empty", diagnostic.GetProperty("status").GetString());
            Assert.False(runnerCreated);
        }

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<CurrentTrackLyricsContext?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = MediaSettingsServiceTestFactory.Create(
            new AppSettings(),
            getCurrentTrackLyricsContext: () =>
            {
                entered.TrySetResult(true);
                return completion.Task;
            },
            getDisplays: () => [],
            getPrimaryDpiScale: () => 1,
            getWorkAreaWidth: () => 1920);

        var command = service.ExecuteServiceCommandAsync(
            new AppSettings(),
            "runLyricDiagnostics",
            null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        completion.TrySetResult(null);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => command);
        Assert.Throws<ObjectDisposedException>(() => service.ReadServiceSettings(new AppSettings()));
    }

    [Fact]
    public async Task TrackOffsetQueryAndMutationUseTheExistingStore()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "tbw-settings-offsets-" + Guid.NewGuid().ToString("N"));
        var originalRoot = AppDataPaths.RootDirectory;
        Directory.CreateDirectory(root);
        try
        {
            AppDataPaths.ConfigureForApplicationRoot(root, hostPortableMode: true);
            using var offsets = new TrackLyricOffsetStore();
            var context = new CurrentTrackLyricsContext(
                new TrackInfo(
                    "track-1",
                    "Test title",
                    "Test artist",
                    "Test album",
                    "QQMusic",
                    TimeSpan.FromSeconds(180)),
                "QQMusic");
            using var service = MediaSettingsServiceTestFactory.Create(
                new AppSettings(),
                offsets: offsets,
                getCurrentTrackLyricsContext: () => Task.FromResult<CurrentTrackLyricsContext?>(context),
                saveSettings: _ => true,
                getDisplays: () => [],
                getPrimaryDpiScale: () => 1,
                getWorkAreaWidth: () => 1920);

            var activated = await service.ExecuteServiceCommandAsync(
                new AppSettings(),
                "trackOffsetsPageActivated",
                null);
            var current = activated.GetProperty("events")
                .EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "currentTrackOffset")
                .GetProperty("payload");
            Assert.Equal("Test title", current.GetProperty("title").GetString());
            Assert.Equal("QQMusic", current.GetProperty("lyricSource").GetString());

            var mutated = await service.ExecuteServiceCommandAsync(
                new AppSettings(),
                "setCurrentTrackOffset",
                JsonSerializer.SerializeToElement(120));
            Assert.Contains(
                mutated.GetProperty("events").EnumerateArray(),
                item => item.GetProperty("type").GetString() == "trackOffsetSaveStatus");

            var queried = await service.ExecuteServiceCommandAsync(
                new AppSettings(),
                "queryTrackOffsets",
                JsonSerializer.SerializeToElement(new
                {
                    requestId = 7,
                    page = 1,
                    pageSize = 25,
                    search = "Test",
                    lyricSource = "All",
                    sort = "updated"
                }));
            var entries = queried.GetProperty("events")
                .EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "trackOffsetEntries")
                .GetProperty("payload")
                .GetProperty("entries");
            Assert.Single(entries.EnumerateArray());
            Assert.Equal(120, entries[0].GetProperty("offsetMilliseconds").GetInt32());
        }
        finally
        {
            AppDataPaths.ConfigureForApplicationRoot(originalRoot, hostPortableMode: false);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
