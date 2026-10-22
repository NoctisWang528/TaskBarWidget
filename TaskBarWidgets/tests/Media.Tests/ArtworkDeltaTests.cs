using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class ArtworkDeltaTests
{
    private const string Cover = "data:image/png;base64,AAAA";
    private const string Icon = "data:image/png;base64,BBBB";

    [Fact]
    public void FirstReplyOfAConnectionIsAlwaysComplete()
    {
        var state = new ArtworkDeltaState();
        var delta = state.Begin(generation: 7, requested: true);
        Assert.False(delta);
        Assert.True(state.CoverChanged(Cover, delta));
        Assert.True(state.IconChanged("qq", Icon, delta));
    }

    [Fact]
    public void UnchangedArtworkIsOmittedWithinOneConnection()
    {
        var state = new ArtworkDeltaState();
        var first = state.Begin(7, requested: true);
        Assert.True(state.CoverChanged(Cover, first));
        state.CommitCover(Cover);
        Assert.True(state.IconChanged("qq", Icon, first));

        var second = state.Begin(7, requested: true);
        Assert.True(second);
        Assert.False(state.CoverChanged(Cover, second));
        Assert.False(state.IconChanged("qq", Icon, second));
    }

    [Fact]
    public void ChangedArtworkIsResent()
    {
        var state = new ArtworkDeltaState();
        var first = state.Begin(7, requested: true);
        state.CommitCover(Cover);
        state.IconChanged("qq", Icon, first);

        var next = state.Begin(7, requested: true);
        Assert.True(state.CoverChanged(Cover + "x", next));
        Assert.True(state.IconChanged("qq", Icon + "x", next));
        Assert.True(state.IconChanged("netease", Icon, next));
    }

    [Fact]
    public void NewConnectionForcesACompleteReplyAgain()
    {
        var state = new ArtworkDeltaState();
        var first = state.Begin(7, requested: true);
        state.CommitCover(Cover);
        state.IconChanged("qq", Icon, first);

        var reconnect = state.Begin(8, requested: true);
        Assert.False(reconnect);
        Assert.True(state.CoverChanged(Cover, reconnect));
        state.CommitCover(Cover);
        Assert.True(state.IconChanged("qq", Icon, reconnect));

        // The re-created client has the artwork now; the next reply may omit it again.
        var steady = state.Begin(8, requested: true);
        Assert.True(steady);
        Assert.False(state.CoverChanged(Cover, steady));
        Assert.False(state.IconChanged("qq", Icon, steady));
    }

    [Fact]
    public void ClientWithoutDeltaOptInAlwaysGetsCompleteArtwork()
    {
        var state = new ArtworkDeltaState();
        state.Begin(7, requested: true);
        state.CommitCover(Cover);

        var full = state.Begin(7, requested: false);
        Assert.False(full);
        Assert.True(state.CoverChanged(Cover, full));
        Assert.True(state.IconChanged("qq", Icon, full));
    }
}
