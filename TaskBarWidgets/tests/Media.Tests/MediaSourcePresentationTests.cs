using TaskbarLyrics.App;
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
