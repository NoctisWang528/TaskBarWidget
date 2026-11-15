using System.IO;
using System.Text.Json;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

[Collection("MediaSettingsServiceSerial")]
public sealed class AppleMusicSettingsTests
{
    [Fact]
    public void OlderSettingsGainEnabledAppleWithoutChangingExistingPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-apple-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "media-settings.json");
            File.WriteAllText(path, """{"EnableQQMusic":false,"EnableSpotify":false,"SourceRecognitionOrder":["Browser","Spotify","QQMusic","Netease","Kugou"],"PlayerSources":{"Spotify":{"LyricOffsetMilliseconds":321}}}""");
            var store = new SettingsStore(path);
            var loaded = store.Load();
            Assert.True(loaded.EnableAppleMusic);
            Assert.False(loaded.EnableQQMusic);
            Assert.False(loaded.EnableSpotify);
            Assert.Equal(new[] { "Browser", "Spotify", "QQMusic", "Netease", "Kugou", "AppleMusic" }, loaded.SourceRecognitionOrder);
            Assert.Equal(321, loaded.PlayerSources["Spotify"].LyricOffsetMilliseconds);
            Assert.Contains("AppleMusic", loaded.PlayerSources.Keys);
            loaded.EnableAppleMusic = false;
            Assert.True(store.Save(loaded));
            Assert.False(store.Load().EnableAppleMusic);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AppleEnablementAndOrderTraverseTheRealSettingsPayloadAndCompositionMapping()
    {
        var current = new AppSettings();
        AppSettings? saved = null;
        using var service = MediaSettingsServiceTestFactory.Create(current, saveSettings: value => { saved = value.Clone(); return true; },
            getDisplays: () => [], getPrimaryDpiScale: () => 1, getWorkAreaWidth: () => 1920);
        var initial = service.ReadServiceSettings(current);
        Assert.True(initial.GetProperty("enableAppleMusic").GetBoolean());
        Assert.Contains("AppleMusic", initial.GetProperty("sourceRecognitionOrder").EnumerateArray().Select(item => item.GetString()));
        var disabled = service.ApplyServiceSettings(current, JsonSerializer.SerializeToElement(new { enableAppleMusic = false }));
        Assert.False(disabled.GetProperty("enableAppleMusic").GetBoolean());
        Assert.NotNull(saved);
        Assert.False(saved!.EnableAppleMusic);
        Assert.True(AppSettingsChangeSet.Create(current, saved).PlayerRecognitionChanged);
        using var root = new AppCompositionRoot(new NoCache(), () => { });
        Assert.DoesNotContain("AppleMusic", root.GetEnabledPlayerSources(saved));
        using var provider = new SmtcMusicSessionProvider();
        provider.SetRecognitionOrder(saved.SourceRecognitionOrder, root.GetEnabledPlayerSources(saved));
        Assert.False(provider.CanUseSource(AppleMusicSource.PackagedAppId));
        var enabled = service.ApplyServiceSettings(saved, JsonSerializer.SerializeToElement(new { enableAppleMusic = true }));
        Assert.True(enabled.GetProperty("enableAppleMusic").GetBoolean());
        Assert.True(saved.EnableAppleMusic);
        provider.SetRecognitionOrder(saved.SourceRecognitionOrder, root.GetEnabledPlayerSources(saved));
        Assert.True(provider.CanUseSource(AppleMusicSource.PackagedAppId));
        var reordered = await service.ExecuteServiceCommandAsync(saved, "reorderSources", JsonSerializer.SerializeToElement(new[] { "AppleMusic", "Browser", "QQMusic" }));
        var order = reordered.GetProperty("settings").GetProperty("sourceRecognitionOrder").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "AppleMusic", "Browser", "QQMusic" }, order.Take(3));
        Assert.Equal(1, order.Count(id => id == "AppleMusic"));
    }

    [Fact]
    public void ExactLegacyAppleCustomSourcesFoldOnceAndRetainTheirEarlierPreference()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"SourceRecognitionOrder":["vendor.custom","AppleInc.AppleMusicWin_nzyj5cx40ttqa!App","Browser","AppleMusic.exe"],
             "CustomPlayerSources":[
               {"SourceAppUserModelId":"AppleInc.AppleMusicWin_nzyj5cx40ttqa!App","DisplayName":"Legacy Apple","Enabled":false},
               {"SourceAppUserModelId":"AppleMusic.exe","DisplayName":"Duplicate Apple","Enabled":true},
               {"SourceAppUserModelId":"vendor.custom","DisplayName":"Keep custom","Enabled":false},
               {"SourceAppUserModelId":"FakeAppleMusic.exe","DisplayName":"Another player","Enabled":true}]}
            """)!;
        settings.NormalizePlayerSources();
        Assert.False(settings.EnableAppleMusic);
        Assert.Equal(new[] { "vendor.custom", "AppleMusic", "Browser" }, settings.SourceRecognitionOrder);
        Assert.Equal(new[] { "vendor.custom", "FakeAppleMusic.exe" }, settings.CustomPlayerSources.Select(source => source.SourceAppUserModelId));
        Assert.False(settings.CustomPlayerSources[0].Enabled);
        Assert.Equal("Keep custom", settings.CustomPlayerSources[0].DisplayName);
        Assert.True(settings.CustomPlayerSources[1].Enabled);
        settings.NormalizePlayerSources();
        Assert.Equal(1, settings.SourceRecognitionOrder.Count(id => id == "AppleMusic"));
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        roundTrip.NormalizePlayerSources();
        Assert.False(roundTrip.EnableAppleMusic);
        Assert.Equal(settings.SourceRecognitionOrder, roundTrip.SourceRecognitionOrder);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitBuiltInApplePreferenceAndPositionWinOverLegacyCustom(bool enabled)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new {
            EnableAppleMusic = enabled,
            SourceRecognitionOrder = new[] { "AppleMusic.exe", "vendor.custom", "AppleMusic", "Browser" },
            CustomPlayerSources = new[] {
                new CustomPlayerSource("AppleMusic.exe", "Legacy Apple", !enabled),
                new CustomPlayerSource("vendor.custom", "Keep custom", true) }
        }))!;
        var clone = settings.Clone();
        Assert.Equal(enabled, clone.EnableAppleMusic);
        Assert.Equal(new[] { "vendor.custom", "AppleMusic", "Browser" }, clone.SourceRecognitionOrder);
        Assert.Equal("vendor.custom", Assert.Single(clone.CustomPlayerSources).SourceAppUserModelId);
        using var root = new AppCompositionRoot(new NoCache(), () => { });
        using var provider = new SmtcMusicSessionProvider();
        provider.SetRecognitionOrder(clone.SourceRecognitionOrder, root.GetEnabledPlayerSources(clone),
            clone.CustomPlayerSources.Select(source => source.SourceAppUserModelId).ToArray());
        Assert.Equal(enabled, provider.CanUseSource(AppleMusicSource.PackagedAppId));
        Assert.Equal(enabled, provider.CanUseSource("AppleMusic.exe"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppleMigrationPreservesLyricPreferencesAndExplicitBuiltInSettingsWin(bool hasBuiltInSettings)
    {
        var legacy = new PlayerSourceSettings
        {
            LyricOffsetMilliseconds = 175,
            LyricProviders = PlayerSourceSettings.CreateDefaultLyricProviders().AsEnumerable().Reverse()
                .Select((provider, index) => provider with { Enabled = index != 0 }).ToList()
        };
        var builtIn = new PlayerSourceSettings { LyricOffsetMilliseconds = -225 };
        var settings = new AppSettings
        {
            CustomPlayerSources = [new CustomPlayerSource(AppleMusicSource.PackagedAppId, "Legacy Apple", true)],
            SourceRecognitionOrder = [AppleMusicSource.PackagedAppId, "Browser"],
            PlayerSources = new() { [AppleMusicSource.PackagedAppId] = legacy }
        };
        if (hasBuiltInSettings) settings.PlayerSources["AppleMusic"] = builtIn;
        var expected = hasBuiltInSettings ? builtIn : legacy;

        settings.NormalizePlayerSources();
        Assert.Equal(expected.LyricOffsetMilliseconds,
            settings.GetPlayerLyricOffsetMilliseconds(AppleMusicSource.PackagedAppId));
        Assert.Equal(expected.LyricProviders, settings.GetPlayerLyricProviders(AppleMusicSource.PackagedAppId));
        Assert.DoesNotContain(AppleMusicSource.PackagedAppId, settings.PlayerSources.Keys);
        Assert.Empty(settings.CustomPlayerSources);

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.NormalizePlayerSources();
        Assert.Equal(expected.LyricOffsetMilliseconds, restored.GetPlayerLyricOffsetMilliseconds("AppleMusic"));
        Assert.Equal(expected.LyricProviders, restored.GetPlayerLyricProviders("AppleMusic"));
    }

    private sealed class NoCache : IResolvedLyricCache
    {
        public bool TryGet(TrackInfo track, out ResolvedLyrics? lyrics) { lyrics = null; return false; }
        public bool Store(TrackInfo track, ResolvedLyrics lyrics) => false;
        public void Clear() { }
    }
}
