using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class MediaIntegrationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"version\":\"1\",\"type\":\"command\",\"action\":\"next\"}")]
    [InlineData("{\"version\":2,\"type\":\"command\",\"action\":\"next\"}")]
    [InlineData("{\"version\":1,\"type\":1,\"action\":\"next\"}")]
    public void MalformedEnvelopeIsRejectedWithoutThrowing(string value)
    {
        using var document = JsonDocument.Parse(value);
        Assert.False(MediaPipeServer.IsCommand(document.RootElement));
    }

    [Fact]
    public void BrowserIsRecognizedForSeparateNonLyricRoute()
    {
        var browser = new TrackInfo("browser", "A video", "channel", "", "Browser", TimeSpan.FromMinutes(3));
        Assert.True(MediaCoordinator.IsBrowser(browser));
        Assert.False(MediaCoordinator.IsBrowser(browser with { SourceApp = "QQMusic" }));
        Assert.Null(MediaCoordinator.LyricsInput(new(true, TimeSpan.Zero, browser)).Track);
        Assert.Null(MediaCoordinator.LyricsInput(new(true, TimeSpan.Zero, browser with { SourceApp = "Custom" }, SourceAppUserModelId: "msedge.exe")).Track);
        Assert.NotNull(MediaCoordinator.LyricsInput(new(true, TimeSpan.Zero, browser with { SourceApp = "QQMusic" }, SourceAppUserModelId: "qqmusic.exe")).Track);
    }

    [Fact]
    public void SourceSelectionKeepsPlayingMusicAndPromotesMusicOverBrowser()
    {
        MediaSessionCandidate[] candidates = [new("qq", "QQMusic", true, 0), new("netease", "Netease", true, 1), new("edge", "Browser", true, -1)];
        Assert.Equal("netease", MediaSourcePolicy.Select(candidates, null, "netease"));
        Assert.Equal("qq", MediaSourcePolicy.Select(candidates, null, "edge"));
        Assert.Equal("edge", MediaSourcePolicy.Select(candidates, "edge", "qq"));
        Assert.Equal("edge", MediaSourcePolicy.Select([candidates[0] with { IsPlaying = false }, candidates[2]], null, "qq"));
    }

    [Fact]
    public void HelperOnlyStartsForExactHostPipePair()
    {
        Assert.True(Program.TryGetHostArguments(["--host-pid", "42", "--pipe-name", "TaskBarWidgets.42"], out _, out _));
        Assert.False(Program.TryGetHostArguments([], out _, out _));
        Assert.True(Program.TryGetHostArguments(["--host-pid", "42", "--pipe-name", "TaskBarWidgets.42", "--portable-data", "1"], out _, out _, out var portable));
        Assert.True(portable);
        Assert.False(Program.TryGetHostArguments(["--host-pid", "42", "--pipe-name", "TaskBarWidgets.42", "--portable-data", "arbitrary"], out _, out _));
        Assert.False(Program.TryGetHostArguments(["--host-pid", "42", "--pipe-name", "another"], out _, out _));
    }

    [Fact]
    public void RejectedMediaControlCannotReportSuccess()
    {
        SmtcMusicSessionProvider.EnsureCommandAccepted(true);
        Assert.Throws<InvalidOperationException>(() => SmtcMusicSessionProvider.EnsureCommandAccepted(false));
    }

    [Fact]
    public void FullSnapshotSurvivesQueueCoalescingAndNewConnectionRestoresHeavyValues()
    {
        var style = WebViewMessageScriptFactory.Dispatch("taskbarLyrics", "style", new { fontSize = 13 });
        var cover = WebViewMessageScriptFactory.Dispatch("taskbarLyrics", "cover", new { dataUri = "data:image/png;base64,AAA" });
        var frame = new MediaFrame(1, "frame", "qq", true, true, false, "track", [style, cover, "position1"]);
        var filter = new MediaFrameFilter();
        Assert.Equal(3, ((MediaFrame)filter.Filter(frame)).scripts.Length);
        Assert.Single(((MediaFrame)filter.Filter(frame)).scripts);
        Assert.Equal(3, ((MediaFrame)new MediaFrameFilter().Filter(frame)).scripts.Length);
    }

    [Fact]
    public void LyricTextMatchingHeavySlotNamesCannotSuppressStyleOrCover()
    {
        var lyric = WebViewMessageScriptFactory.Dispatch("taskbarLyrics", "lyrics", new { current = "style", next = "cover" });
        var style = WebViewMessageScriptFactory.Dispatch("taskbarLyrics", "style", new { fontSize = 13 });
        var frame = new MediaFrame(1, "frame", "qq", true, true, false, "track", [lyric, style]);
        var filter = new MediaFrameFilter();
        Assert.Equal(2, ((MediaFrame)filter.Filter(frame)).scripts.Length);
        Assert.Empty(((MediaFrame)filter.Filter(frame)).scripts);
    }

    [Fact]
    public async Task PipeReconnectAlwaysSendsInitializeAndLatestSnapshot()
    {
        var name = "TaskBarWidgets.Tests." + Guid.NewGuid().ToString("N");
        var frame = new MediaFrame(1, "frame", "qq", true, true, false, "track", []);
        await using var server = new MediaPipeServer(name, () => new { version = 1, type = "initialize", html = "<html/>" }, () => frame, _ => Task.CompletedTask);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(client);
            using var initialize = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            using var snapshot = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert.Equal("initialize", initialize.RootElement.GetProperty("type").GetString());
            Assert.Equal("track", snapshot.RootElement.GetProperty("trackId").GetString());
        }
    }
}
