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
    public void FrameFilterReusesStyleAndCoverOnlyOnAnEstablishedConnection()
    {
        var frame = CreateFrame(revision: 1);
        var filter = new MediaFrameFilter();
        var first = Assert.IsType<MediaFrame>(filter.Filter(frame));
        Assert.NotNull(first.style);
        Assert.NotNull(first.cover);

        var second = Assert.IsType<MediaFrame>(filter.Filter(frame with
        {
            revision = 2,
            lyrics = frame.lyrics with { current = "next line" }
        }));
        Assert.Null(second.style);
        Assert.Null(second.cover);

        var third = Assert.IsType<MediaFrame>(filter.Filter(frame with
        {
            revision = 3,
            cover = frame.cover! with { dataUri = "data:image/png;base64,BBB" }
        }));
        Assert.Null(third.style);
        Assert.NotNull(third.cover);

        var cleared = Assert.IsType<MediaFrame>(filter.Filter(frame with
        {
            revision = 4,
            cover = NativeMediaPayloadFactory.EmptyCover()
        }));
        Assert.NotNull(cleared.cover);
        Assert.Equal("", cleared.cover!.dataUri);
        Assert.Equal("", cleared.cover.trackId);

        var reconnected = Assert.IsType<MediaFrame>(new MediaFrameFilter().Filter(frame));
        Assert.NotNull(reconnected.style);
        Assert.NotNull(reconnected.cover);
    }

    [Fact]
    public void LatestQueueReplacesUnsentFramesButRetainsCompleteState()
    {
        var queue = new LatestDisplayQueue();
        queue.Publish(CreateFrame(revision: 1, sourceId: "one"));
        queue.Publish(CreateFrame(revision: 2, sourceId: "two"));
        var latest = CreateFrame(revision: 3, sourceId: "three");
        queue.Publish(latest);

        Assert.True(queue.TryTake(out var value));
        var retained = Assert.IsType<MediaFrame>(value);
        Assert.Equal(3, retained.revision);
        Assert.Equal("three", retained.sourceId);
        Assert.NotNull(retained.style);
        Assert.NotNull(retained.cover);
        Assert.False(queue.TryTake(out _));
        Assert.Equal(3, queue.PublishedCount);
    }

    [Fact]
    public void NativeFrameSerializationContainsNoScriptsOrHtml()
    {
        var json = JsonSerializer.SerializeToElement(CreateFrame());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal(2, json.GetProperty("renderVersion").GetInt32());
        Assert.Equal("frame", json.GetProperty("type").GetString());
        Assert.Equal("line", json.GetProperty("lyrics").GetProperty("current").GetString());
        Assert.Equal("rgba(67,160,71,1)", json.GetProperty("cover").GetProperty("fallbackColor").GetString());
        Assert.False(json.TryGetProperty("scripts", out _));
        Assert.False(json.TryGetProperty("html", out _));
    }

    [Fact]
    public void CardSnapshotIsAlwaysComplete()
    {
        var snapshot = new PlaybackSnapshot(
            true,
            TimeSpan.FromSeconds(2),
            new TrackInfo("id", "Title", "Artist", "Album", "QQMusic", TimeSpan.FromMinutes(3)));
        var lyrics = new MediaLyricsFrame(
            "line", "next", 0.5, 0, "QQMUSIC|TITLE|ARTIST", false, true,
            "", "", false, true, 0.25, "lyrics");
        var style = NativeMediaPayloadFactory.CreateStyle(new AppSettings(), 1);
        var state = new MediaPresentationState();
        var update = state.Apply(new MediaPresentationInput(
            snapshot,
            false,
            "QQMUSIC|TITLE|ARTIST",
            lyrics,
            style,
            CreateCover(),
            new MediaBrowserFrame(false, "", "", "qq", true, false, false, true),
            NativeMediaPayloadFactory.CreateSpectrumTuning(SpectrumTuningSettings.CreateDefault()),
            [],
            NativeAppearanceJson.Empty));

        Assert.Equal(MediaProtocol.RenderVersion, update.Card.renderVersion);
        Assert.Equal(0, update.Card.revision);
        Assert.Equal(style, update.Card.style);
        Assert.Equal("line", update.Card.lyrics.current);
        Assert.Equal(JsonValueKind.Object, update.Card.nativeAppearance.ValueKind);
        Assert.Same(update.Card, state.CurrentCard);

        var json = JsonSerializer.SerializeToElement(update.Card);
        Assert.Equal(2, json.GetProperty("renderVersion").GetInt32());
        Assert.Equal("QQMUSIC|TITLE|ARTIST", json.GetProperty("trackId").GetString());
        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.True(json.GetProperty("isPlaying").GetBoolean());
        Assert.Equal("line", json.GetProperty("lyrics").GetProperty("current").GetString());
        Assert.Equal(JsonValueKind.Object, json.GetProperty("style").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("nativeAppearance").ValueKind);
        Assert.False(json.TryGetProperty("scripts", out _));
    }

    [Fact]
    public async Task PipeReconnectAlwaysSendsInitializeAndLatestSnapshot()
    {
        var name = "TaskBarWidgets.Tests." + Guid.NewGuid().ToString("N");
        var frame = CreateFrame(revision: 7);
        await using var server = new MediaPipeServer(
            name,
            MediaProtocol.Initialize,
            () => frame,
            _ => Task.CompletedTask);
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
            Assert.Equal(2, initialize.RootElement.GetProperty("renderVersion").GetInt32());
            Assert.False(initialize.RootElement.TryGetProperty("html", out _));
            Assert.Equal("qq", snapshot.RootElement.GetProperty("sourceId").GetString());
            Assert.Equal(7, snapshot.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal("line", snapshot.RootElement.GetProperty("lyrics").GetProperty("current").GetString());
            Assert.Equal(JsonValueKind.Object, snapshot.RootElement.GetProperty("style").ValueKind);
            Assert.Equal(JsonValueKind.Object, snapshot.RootElement.GetProperty("cover").ValueKind);
        }
    }

    [Fact]
    public async Task DisplayQueueReplacementDoesNotDropQueuedCommands()
    {
        var name = "TaskBarWidgets.Tests.Commands." + Guid.NewGuid().ToString("N");
        var frame = CreateFrame(revision: 1);
        await using var server = new MediaPipeServer(
            name,
            MediaProtocol.Initialize,
            () => frame,
            _ => Task.CompletedTask);
        await server.SendCommandAsync(new
        {
            version = 1,
            type = "command",
            action = "keep-me",
            requestId = "command-1"
        });
        server.Publish(CreateFrame(revision: 2));
        server.Publish(CreateFrame(revision: 3));
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new NamedPipeClientStream(
            ".",
            name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client);
        using var initialize = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        using var snapshot = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        using var command = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);

        Assert.Equal("initialize", initialize.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, snapshot.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal("keep-me", command.RootElement.GetProperty("action").GetString());
        Assert.Equal("command-1", command.RootElement.GetProperty("requestId").GetString());
    }

    private static MediaFrame CreateFrame(
        long revision = 1,
        string sourceId = "qq")
    {
        var style = NativeMediaPayloadFactory.CreateStyle(new AppSettings(), 1);
        return new MediaFrame(
            MediaProtocol.Version,
            "frame",
            MediaProtocol.RenderVersion,
            revision,
            sourceId,
            true,
            true,
            false,
            "QQMUSIC|TITLE|ARTIST",
            new MediaLyricsFrame(
                "line", "next", 0.25, 0, "QQMUSIC|TITLE|ARTIST", false, true,
                "", "", false, true, 0.5, "lyrics"),
            style,
            CreateCover(),
            new MediaBrowserFrame(false, "Title", "Artist", sourceId, true, true, true, true),
            NativeMediaPayloadFactory.CreateSpectrumTuning(SpectrumTuningSettings.CreateDefault()),
            []);
    }

    private static MediaCoverFrame CreateCover() =>
        NativeMediaPayloadFactory.CreateCover(
            "data:image/png;base64,AAA",
            "Q",
            "rgba(67,160,71,1)",
            "",
            "QQMUSIC|TITLE|ARTIST");
}
