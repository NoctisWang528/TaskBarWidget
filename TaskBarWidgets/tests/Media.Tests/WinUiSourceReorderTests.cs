using TaskBarWidgets.UI.Settings;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class WinUiSourceReorderTests
{
    [Fact]
    public void DragGapsAndKeyboardMovesKeepCustomPlayersAndDisabledPositions()
    {
        var original = new[] { "QQMusic", "custom.player", "Netease", "Browser" };
        Assert.Equal(new[] { "custom.player", "Netease", "QQMusic", "Browser" }, ReorderSequence.MoveToGap(original, "QQMusic", 3));
        Assert.Equal(new[] { "Browser", "QQMusic", "custom.player", "Netease" }, ReorderSequence.MoveToGap(original, "Browser", 0));
        Assert.Equal(original, ReorderSequence.MoveToGap(original, "custom.player", 1));
        Assert.Equal(original, ReorderSequence.MoveToGap(original, "custom.player", 2));
        Assert.Equal(original, ReorderSequence.MoveToGap(original, "missing", 0));
        Assert.Equal(new[] { "QQMusic", "Netease", "Browser", "custom.player" }, ReorderSequence.MoveToGap(original, "custom.player", 99));
        Assert.Equal(new[] { "QQMusic", "custom.player", "Netease", "Browser" }, original);
    }
}
