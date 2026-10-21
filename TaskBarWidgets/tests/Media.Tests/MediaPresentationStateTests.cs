using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class MediaPresentationStateTests
{
    [Fact]
    public void UnchangedStateDoesNotPublishOrAdvanceRevision()
    {
        var state = new MediaPresentationState();
        var input = CreateInput();
        var first = state.Apply(input);
        Assert.True(first.MediaFrameChanged);
        Assert.True(first.CardFrameChanged);
        Assert.Equal(0, first.Frame.revision);
        Assert.Equal(0, first.Card.revision);

        var second = state.Apply(input);
        Assert.False(second.MediaFrameChanged);
        Assert.False(second.CardFrameChanged);
        Assert.Equal(first.Frame, second.Frame);
        Assert.Equal(first.Card, second.Card);
    }

    [Fact]
    public void PauseResumeAndSeekAdvanceRevisionOnlyWhenPresentationChanges()
    {
        var state = new MediaPresentationState();
        var playing = CreateInput();
        var initial = state.Apply(playing);

        var paused = state.Apply(playing with
        {
            Snapshot = playing.Snapshot with { IsPlaying = false },
            Lyrics = playing.Lyrics with { isPlaying = false },
            Browser = playing.Browser with { isPlaying = false }
        });
        Assert.True(paused.MediaFrameChanged);
        Assert.True(paused.CardFrameChanged);
        Assert.Equal(initial.Frame.revision + 1, paused.Frame.revision);
        Assert.Equal(initial.Card.revision + 1, paused.Card.revision);

        var resumed = state.Apply(playing);
        Assert.True(resumed.MediaFrameChanged);
        Assert.True(resumed.CardFrameChanged);
        Assert.Equal(paused.Frame.revision + 1, resumed.Frame.revision);

        var seeked = state.Apply(playing with
        {
            Snapshot = playing.Snapshot with { Position = TimeSpan.FromSeconds(45) },
            Lyrics = playing.Lyrics with { progress = 0.75, wordScanProgress = 0.7 }
        });
        Assert.True(seeked.MediaFrameChanged);
        Assert.True(seeked.CardFrameChanged);
        Assert.Equal(resumed.Frame.revision + 1, seeked.Frame.revision);
    }

    [Fact]
    public void SourceAndTrackChangesReplaceTheWholeSnapshot()
    {
        var state = new MediaPresentationState();
        var first = state.Apply(CreateInput());
        var changed = state.Apply(CreateInput() with
        {
            Snapshot = CreateInput().Snapshot with { SourceAppUserModelId = "cloudmusic.exe" },
            TrackId = "NETEASE|OTHER|ARTIST",
            Lyrics = CreateInput().Lyrics with { current = "other", trackId = "NETEASE|OTHER|ARTIST" }
        });

        Assert.Equal("cloudmusic.exe", changed.Frame.sourceId);
        Assert.Equal("NETEASE|OTHER|ARTIST", changed.Card.trackId);
        Assert.Equal("other", changed.Card.lyrics.current);
        Assert.Equal(first.Frame.revision + 1, changed.Frame.revision);
    }

    [Fact]
    public void NativeAppearanceChangesOnlyTheCompleteCardSnapshot()
    {
        var state = new MediaPresentationState();
        var first = state.Apply(CreateInput());
        using var appearance = JsonDocument.Parse("""{"fontFamily":"Segoe UI","taskbar":{"height":48}}""");
        var changed = state.Apply(CreateInput() with
        {
            NativeAppearance = appearance.RootElement
        });

        Assert.False(changed.MediaFrameChanged);
        Assert.True(changed.CardFrameChanged);
        Assert.Equal(first.Frame.revision, changed.Frame.revision);
        Assert.Equal(first.Card.revision + 1, changed.Card.revision);
        Assert.Equal("Segoe UI", changed.Card.nativeAppearance.GetProperty("fontFamily").GetString());
    }

    [Fact]
    public void SpectrumUpdatesOnlyAdvanceTheNativeFrame()
    {
        var state = new MediaPresentationState();
        var initial = state.Apply(CreateInput());
        var frame = state.UpdateSpectrum([0.25f, 0.5f]);
        Assert.NotNull(frame);
        Assert.Equal(initial.Frame.revision + 1, frame!.revision);
        Assert.Equal([0.25f, 0.5f], frame.spectrum);
        Assert.Null(state.UpdateSpectrum([0.25f, 0.5f]));
        Assert.Equal(initial.Card, state.CurrentCard);
    }

    private static MediaPresentationInput CreateInput()
    {
        var snapshot = new PlaybackSnapshot(
            true,
            TimeSpan.FromSeconds(20),
            new TrackInfo(
                "song",
                "Title",
                "Artist",
                "Album",
                "QQMusic",
                TimeSpan.FromMinutes(4)),
            SourceAppUserModelId: "qqmusic.exe",
            CanSeek: true);
        var lyrics = new MediaLyricsFrame(
            "current",
            "next",
            0.4,
            2,
            "QQMUSIC|TITLE|ARTIST",
            false,
            true,
            "",
            "",
            false,
            true,
            0.35,
            "lyrics");
        return new MediaPresentationInput(
            snapshot,
            false,
            "QQMUSIC|TITLE|ARTIST",
            lyrics,
            NativeMediaPayloadFactory.CreateStyle(new AppSettings(), 1),
            NativeMediaPayloadFactory.CreateCover(
                "data:image/png;base64,AAA",
                "Q",
                "rgba(67,160,71,1)",
                "",
                "QQMUSIC|TITLE|ARTIST"),
            new MediaBrowserFrame(
                false,
                "Title",
                "Artist",
                "qqmusic.exe",
                true,
                true,
                true,
                true),
            NativeMediaPayloadFactory.CreateSpectrumTuning(
                SpectrumTuningSettings.CreateDefault()),
            [],
            NativeAppearanceJson.Empty);
    }
}
