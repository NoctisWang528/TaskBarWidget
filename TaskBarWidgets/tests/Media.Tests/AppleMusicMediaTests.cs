using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class AppleMusicMediaTests
{
    [Theory]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")]
    [InlineData("appleinc.applemusicwin_nzyj5cx40ttqa!app")]
    [InlineData("AppleMusic.exe")]
    [InlineData("AppleMusic")]
    [InlineData("Apple Music")]
    public void ExactAppleIdentityUsesTheBuiltInEnabledSmtcRoute(string id)
    {
        Assert.True(AppleMusicSource.IsAppleMusic(id));
        Assert.Equal("AppleMusic", SmtcMusicSessionProvider.NormalizeSource(id));
        using var provider = new SmtcMusicSessionProvider();
        Assert.True(provider.CanUseSource(id));
        provider.SetRecognitionOrder(new[] { "AppleMusic", "QQMusic", "Browser" }, new[] { "QQMusic" });
        Assert.False(provider.CanUseSource(id));
        provider.SetRecognitionOrder(new[] { "AppleMusic", "QQMusic", "Browser" }, new[] { "AppleMusic", "QQMusic" });
        Assert.True(provider.CanUseSource(id));
    }

    [Theory]
    [InlineData("FakeAppleMusic.exe")]
    [InlineData("AppleMusic.exe.other")]
    [InlineData("OtherVendor.AppleMusicWin_nzyj5cx40ttqa!App")]
    [InlineData("AppleInc.AppleMusicWin_otherpublisher!App")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!LibraryServer")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!AppExtra")]
    [InlineData("https://music.apple.com")]
    public void SimilarIdsAndHelpersAreNotClassifiedAsTheApplePlayer(string id)
    {
        Assert.False(AppleMusicSource.IsAppleMusic(id));
        Assert.Equal(id, SmtcMusicSessionProvider.NormalizeSource(id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AppleKeepsMetadataAndArtworkAndUsesTheLyricsPipeline(bool playing)
    {
        byte[] cover = { 1, 2, 3 };
        var snapshot = new PlaybackSnapshot(playing, TimeSpan.FromSeconds(42),
            new TrackInfo("apple-track", "Song", "Artist", "Album", "AppleMusic", TimeSpan.FromMinutes(4)),
            CoverImageBytes: cover, SourceAppUserModelId: AppleMusicSource.PackagedAppId, CanSeek: true);
        using var provider = new SmtcMusicSessionProvider();
        Assert.Equal(snapshot, provider.CommitPlaybackSnapshot(snapshot));
        Assert.False(NonLyricsMediaPolicy.IsDouyin(snapshot));
        Assert.False(NonLyricsMediaPolicy.IsNonLyrics(snapshot));
        var lyricsInput = MediaCoordinator.LyricsInput(snapshot);
        Assert.Equal(snapshot, lyricsInput);
        Assert.Same(snapshot.Track, lyricsInput.Track);
        Assert.Same(cover, snapshot.CoverImageBytes);
        Assert.Equal("Song", snapshot.Track!.Title);
        Assert.Equal("Artist", snapshot.Track.Artist);
        Assert.Equal(TimeSpan.FromSeconds(42), snapshot.Position);
        var display = new LyricDisplayFrame("Current lyric", "Next lyric", "Song", 0, 0);
        Assert.Equal(LyricsPresentationScene.Lyrics, LyricsPresentationCalculator.ResolveCardScene(
            new AppSettings(), display, NonLyricsMediaPolicy.IsNonLyrics(snapshot), snapshot.Track is not null,
            LyricAcquisitionKind.Unknown));
    }

    [Theory]
    [InlineData("AppleMusic")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")]
    public void SourceAndMissingArtistHaveAReadableAppleName(string name)
    {
        var source = new MediaSourceChoice(AppleMusicSource.PackagedAppId, name);
        Assert.Equal("Apple Music", PlayerMediaSessionDiscovery.DisplayNameFor(AppleMusicSource.PackagedAppId));
        Assert.Equal("Apple Music", PlayerMediaSessionDiscovery.DisplayNameFor("AppleMusic.exe"));
        Assert.Equal("App", PlayerMediaSessionDiscovery.DisplayNameFor("OtherVendor.Player_123!App"));
        Assert.Equal("Apple Music", MediaSourcePresentation.Label(source));
        Assert.Equal("Apple Music", MediaSourcePresentation.Artist("Unknown Artist", source));
        Assert.Equal("Actual Artist", MediaSourcePresentation.Artist("Actual Artist", source));
    }

    [Fact]
    public void AppleRemainsMusicForAutomaticSelectionAndSupportsManualSelection()
    {
        MediaSessionCandidate[] candidates = [
            new(AppleMusicSource.PackagedAppId, "AppleMusic", true, int.MaxValue),
            new("msedge.exe", "Browser", true, 0),
            new("electron.app.douyin", "Douyin", true, 0)];
        Assert.Equal(AppleMusicSource.PackagedAppId, MediaSourcePolicy.Select(candidates, null, "msedge.exe"));
        Assert.Equal("msedge.exe", MediaSourcePolicy.Select(candidates, "msedge.exe", AppleMusicSource.PackagedAppId));
        var music = new MediaSessionCandidate("QQMusic.exe", "QQMusic", true, 0);
        Assert.Equal(AppleMusicSource.PackagedAppId, MediaSourcePolicy.Select([music, candidates[0]], null, AppleMusicSource.PackagedAppId));
        Assert.Equal("QQMusic.exe", MediaSourcePolicy.Select([music, candidates[0]], null, "QQMusic.exe"));
    }
}
