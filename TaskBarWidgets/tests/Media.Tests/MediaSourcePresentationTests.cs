using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class MediaSourcePresentationTests
{
    [Theory]
    [InlineData("msedge", "msedge")]
    [InlineData("MSEdge.exe", "MSEdge")]
    [InlineData("msedge.exe._Default", "msedge")]
    [InlineData("C:\\Apps\\player.exe", "player")]
    [InlineData("C:\\folder.exe.cache\\player.exe", "player")]
    [InlineData("Microsoft.MicrosoftEdge.Stable", "msedge")]
    [InlineData("com.bytedance.douyin", "douyin")]
    [InlineData("抖音", "douyin")]
    [InlineData("another-player", "another-player")]
    [InlineData("Vendor.Player_123!App", null)]
    [InlineData("", null)]
    public void DesktopSourceIdsResolveWithoutRequiringExeSuffix(string id, string? expected) =>
        Assert.Equal(expected, ProcessExecutablePathResolver.GetProcessNameCandidate(id));

    [Theory]
    [InlineData("msedge.exe._Default", "Browser", "Microsoft Edge")]
    [InlineData("Microsoft.MicrosoftEdge.Stable", "Browser", "Microsoft Edge")]
    [InlineData("douyin.exe", "Douyin", "抖音")]
    [InlineData("com.bytedance.douyin", "抖音", "抖音")]
    [InlineData("QQMusic.exe", "QQMusic", "QQ 音乐")]
    [InlineData("unknown.exe", "自定义播放器", "自定义播放器")]
    public void MissingArtistUsesTheSelectedApplication(string id, string name, string expected)
    {
        var source = new MediaSourceChoice(id, name);
        Assert.Equal(expected, MediaSourcePresentation.Artist(null, source));
        Assert.Equal(expected, MediaSourcePresentation.Artist(" \t", source));
        Assert.Equal("真实作者", MediaSourcePresentation.Artist("真实作者", source));
    }

    [Theory]
    [InlineData("Unknown Artist")]
    [InlineData(" UNKNOWN ARTIST ")]
    [InlineData("Unknown Artists")]
    [InlineData("未知艺术家")]
    [InlineData("未知歌手")]
    [InlineData("未知作者")]
    [InlineData("<UNKNOWN>")]
    public void ExplicitPlaceholderArtistsUseTheApplicationWithoutChangingMetadata(string placeholder)
    {
        var track = new TrackInfo("fixture", "A video", placeholder, "", "Browser", TimeSpan.FromMinutes(3));
        var edge = new MediaSourceChoice("msedge.exe._Default", "Browser");
        var chrome = new MediaSourceChoice("chrome.exe", "Browser");
        Assert.Equal("Microsoft Edge", MediaSourcePresentation.Artist(track.Artist, edge));
        Assert.Equal("Google Chrome", MediaSourcePresentation.Artist(track.Artist, chrome));
        Assert.Equal(placeholder, track.Artist);
        Assert.Equal("Browser", track.SourceApp);
        Assert.Null(MediaCoordinator.LyricsInput(new(true, TimeSpan.Zero, track, SourceAppUserModelId: edge.Id)).Track);
        Assert.Null(MediaCoordinator.LyricsInput(new(true, TimeSpan.Zero, track, SourceAppUserModelId: chrome.Id)).Track);
    }

    [Theory]
    [InlineData("Unknown Artist Collective")]
    [InlineData("Various Artists")]
    [InlineData("Unknown")]
    [InlineData("群星")]
    [InlineData("  Real Artist  ")]
    public void ActualArtistsRemainByteForByteUnchanged(string artist) =>
        Assert.Equal(artist, MediaSourcePresentation.Artist(artist, new("msedge.exe", "Browser")));

    [Theory]
    [InlineData("chrome.exe", "Browser", "Google Chrome")]
    [InlineData("msedge.exe._Default", "浏览器", "Microsoft Edge")]
    [InlineData("msedge.exe", "Microsoft Edge Beta", "Microsoft Edge Beta")]
    [InlineData("chrome.exe", "Google Chrome Canary", "Google Chrome Canary")]
    [InlineData("C:\\Apps\\OtherBrowser.exe", "Browser", "OtherBrowser")]
    [InlineData("Vendor.Browser_123!App", "Browser", "Vendor.Browser_123!App")]
    [InlineData("other.exe", "", "other")]
    public void SourceLabelsPreserveDisplayNamesOrSpecificProcessIdentity(string id, string name, string expected) =>
        Assert.Equal(expected, MediaSourcePresentation.Label(new(id, name)));

    [Fact]
    public void TwoBrowserSessionsKeepDistinctLabelsAndSelectionIds()
    {
        MediaSourceChoice[] sources = [new("msedge.exe._Default", "Browser"), new("chrome.exe", "Browser")];
        Assert.Equal(new[] { "Microsoft Edge", "Google Chrome" }, sources.Select(MediaSourcePresentation.Label));
        Assert.Equal(new[] { "msedge.exe._Default", "chrome.exe" }, sources.Select(source => source.Id));
        Assert.All(sources, source => Assert.Equal("Browser", SmtcMusicSessionProvider.NormalizeSource(source.Id)));
    }

    [Theory]
    [InlineData("Unknown Artist", "Song - QQ 音乐")]
    [InlineData("", "Song - QQ 音乐")]
    [InlineData("Real Artist", "Song - Real Artist")]
    public void TaskbarSearchingTextUsesDisplayFallbackWithoutMutatingTrack(string artist, string expected)
    {
        var track = new TrackInfo("fixture", "Song", artist, "", "QQMusic", TimeSpan.FromMinutes(3));
        Assert.Equal(expected, LyricsSearchingPresentationPolicy.Create(track).Current);
        Assert.Equal(artist, track.Artist);
        Assert.Equal("QQMusic", track.SourceApp);
    }

    [Fact]
    public void DefaultApplicationIconIsNativeDecodablePng()
    {
        const string prefix = "data:image/png;base64,";
        var icon = AppIconExtractor.DefaultApplicationDataUrl;
        Assert.StartsWith(prefix, icon);
        var bytes = Convert.FromBase64String(icon[prefix.Length..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
    }
}
