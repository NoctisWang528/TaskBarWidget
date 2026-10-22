using System.IO;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

// Peer validation is exercised as pure logic: expected-path derivation, path
// comparison and the fallback decision. The Win32 handle query and a real WinUI
// client still require desktop acceptance.
public sealed class PipePeerTests
{
    private const int MediaProcessId = 4242;

    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "tbw-pipe-peer-tests");

    [Fact]
    public void UiBridgePipeExpectsTheSiblingUiExecutable()
    {
        var mediaDirectory = Path.Combine(TemporaryRoot(), "Media");
        var expected = Path.Combine(TemporaryRoot(), "UI", "TaskBarWidgets.UI.exe");

        Assert.Equal(expected, MediaPipeServer.ExpectedPeerPath($"TaskBarWidgets.UI.{MediaProcessId}", mediaDirectory, MediaProcessId));
        // AppContext.BaseDirectory keeps a trailing separator; both forms must derive
        // the same sibling path.
        Assert.Equal(expected, MediaPipeServer.ExpectedPeerPath($"TaskBarWidgets.UI.{MediaProcessId}",
            mediaDirectory + Path.DirectorySeparatorChar, MediaProcessId));
    }

    [Theory]
    [InlineData("TaskBarWidgets.4242")]
    [InlineData("TaskBarWidgets.UI.4243")]
    [InlineData("taskbarwidgets.ui.4242")]
    [InlineData("TaskBarWidgets.Tests.UI.0123456789abcdef")]
    [InlineData("TaskBarWidgets.UI.test.0123456789abcdef")]
    [InlineData("TaskBarWidgets.NativeTransport.0123456789abcdef")]
    [InlineData("")]
    public void PipesWithoutTheUiBridgeNameHaveNoExpectedPeer(string pipeName) =>
        Assert.Null(MediaPipeServer.ExpectedPeerPath(pipeName, Path.Combine(TemporaryRoot(), "Media"), MediaProcessId));

    [Fact]
    public void EmptyBaseDirectoryHasNoExpectedPeer() =>
        Assert.Null(MediaPipeServer.ExpectedPeerPath($"TaskBarWidgets.UI.{MediaProcessId}", " ", MediaProcessId));

    [Fact]
    public void ServerUsesItsOwnPipeNameForTheExpectation()
    {
        Assert.NotNull(MediaPipeServer.ExpectedPeerPath($"TaskBarWidgets.UI.{Environment.ProcessId}"));
        Assert.Null(MediaPipeServer.ExpectedPeerPath("TaskBarWidgets.4242"));
    }

    [Fact]
    public void PeerDecisionCoversFallbacksAndRejection()
    {
        var expected = Path.Combine(TemporaryRoot(), "UI", "TaskBarWidgets.UI.exe");
        var other = Path.Combine(TemporaryRoot(), "UI", "TaskBarWidgets.UI.copy.exe");
        var self = Path.Combine(TemporaryRoot(), "Media", "TaskBarWidgets.Media.exe");
        var selfOtherCase = Path.Combine(TemporaryRoot(), "Media", "TASKBARWIDGETS.MEDIA.EXE");

        Assert.Equal(MediaPipeServer.PeerVerdict.NoExpectation, MediaPipeServer.EvaluatePeer(null, true, expected, self));
        // A test or development layout without the sibling UI executable keeps the
        // current-user-only pipe instead of rejecting every connection.
        Assert.Equal(MediaPipeServer.PeerVerdict.ExpectedMissing, MediaPipeServer.EvaluatePeer(expected, false, other, self));
        Assert.Equal(MediaPipeServer.PeerVerdict.SelfConnection, MediaPipeServer.EvaluatePeer(expected, true, selfOtherCase, self));
        Assert.Equal(MediaPipeServer.PeerVerdict.Accepted, MediaPipeServer.EvaluatePeer(expected, true, expected, self));
        Assert.Equal(MediaPipeServer.PeerVerdict.Rejected, MediaPipeServer.EvaluatePeer(expected, true, other, self));
        Assert.Equal(MediaPipeServer.PeerVerdict.Rejected, MediaPipeServer.EvaluatePeer(expected, true, null, self));
        Assert.Equal(MediaPipeServer.PeerVerdict.Rejected, MediaPipeServer.EvaluatePeer(expected, true, string.Empty, self));
    }

    [Fact]
    public void PeerPathComparisonIgnoresCaseAndTrailingSeparators()
    {
        var expected = Path.Combine(TemporaryRoot(), "UI", "TaskBarWidgets.UI.exe");
        var directory = Path.GetDirectoryName(expected)!;
        var differentCase = Path.Combine(directory, "taskbarwidgets.ui.EXE");

        Assert.True(MediaPipeServer.PathsEqual(expected, differentCase));
        Assert.True(MediaPipeServer.PathsEqual(expected + Path.DirectorySeparatorChar, expected));
        Assert.False(MediaPipeServer.PathsEqual(expected, Path.Combine(directory, "TaskBarWidgets.UI.old.exe")));
        Assert.False(MediaPipeServer.PathsEqual(expected, string.Empty));
    }

    [Fact]
    public void ShortPathPeerIsAcceptedOnlyWhenTheLongPathResolverAgrees()
    {
        var longPath = Path.Combine(TemporaryRoot(), "Program Files", "TaskBarWidgets", "UI", "TaskBarWidgets.UI.exe");
        var shortPath = Path.Combine(TemporaryRoot(), "PROGRA~1", "TaskBarWidgets", "UI", "TASKBAR~1.EXE");

        // 8.3 names resolve to the long form; the resolver is injected so the test does
        // not depend on short-name generation being enabled on the test volume.
        Assert.True(MediaPipeServer.PathsEqual(shortPath, longPath, _ => longPath));
        Assert.False(MediaPipeServer.PathsEqual(shortPath, longPath, static _ => null));
    }
}
