using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class AudioRolePolicyTests
{
    private static readonly Guid Sonic = new("b53d940c-b846-4831-9f76-d102b9b725a0");

    [Fact]
    public void OutputSwitchRestoresOffWhenWindowsChangesOnlyTheActiveFormat()
    {
        var off = new OutputSpatialSound(Guid.Empty, Guid.Empty);
        Assert.Equal(Guid.Empty, off.FormatToRestore(new(Guid.Empty, Sonic)));
        Assert.Equal(Guid.Empty, off.FormatToRestore(new(Sonic, Sonic)));
        Assert.Null(off.FormatToRestore(off));
    }

    [Fact]
    public void OutputSwitchKeepsTheTargetDevicesSelectedProvider()
    {
        var enabled = new OutputSpatialSound(Sonic, Sonic);
        Assert.Equal(Sonic, enabled.FormatToRestore(new(Guid.Empty, Guid.Empty)));
        Assert.Null(enabled.FormatToRestore(enabled));
        // The active provider may already differ from the user's selected provider.
        Assert.Null(new OutputSpatialSound(Sonic, Guid.Empty).FormatToRestore(enabled));
    }

    [Fact]
    public void FormatRestoreFailureKeepsRoleReadbackAndReportsWarning()
    {
        var result = new AudioSwitchResult([new(AudioRole.Console, true, null)]) { SpatialSoundUnconfirmed = true };
        Assert.False(result.Succeeded);
        Assert.True(result.PartiallySucceeded);
        Assert.True(result.Roles.Single().IsSelected);
        Assert.Contains("空间音效", result.Message);
    }

    [Fact]
    public void DefaultSwitchPreservesCommunicationsRole() =>
        Assert.Equal([AudioRole.Console, AudioRole.Multimedia], AudioRolePolicy.GetRoles(false));

    [Fact]
    public void ExplicitOptInAddsCommunicationsRole() =>
        Assert.Equal([AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications], AudioRolePolicy.GetRoles(true));

    [Fact]
    public void ReadbackOfAllRequestedRolesConfirmsSuccess()
    {
        var result = new AudioSwitchResult([
            new(AudioRole.Console, true, null), new(AudioRole.Multimedia, true, null)]);
        Assert.True(result.Succeeded);
        Assert.False(result.PartiallySucceeded);
    }

    [Fact]
    public void MixedReadbackIsPartialSuccessAndPreservesError()
    {
        var result = new AudioSwitchResult([
            new(AudioRole.Console, true, null), new(AudioRole.Multimedia, false, "readback mismatch")]);
        Assert.False(result.Succeeded);
        Assert.True(result.PartiallySucceeded);
        Assert.Contains("部分", result.Message);
        Assert.Equal("readback mismatch", result.Roles[1].Error);
    }

    [Fact]
    public void FailedOrEmptyReadbackCannotReportSuccess()
    {
        var failed = new AudioSwitchResult([
            new(AudioRole.Console, false, "unavailable"), new(AudioRole.Multimedia, false, "unavailable")]);
        Assert.False(failed.Succeeded);
        Assert.False(failed.PartiallySucceeded);
        Assert.False(new AudioSwitchResult([]).Succeeded);
    }

    [Fact]
    public void SuccessfulComCallsStillRequireMatchingReadback()
    {
        var result = AudioRolePolicy.ReadBack("requested", false, role =>
            role == AudioRole.Console ? "REQUESTED" : "old-device",
            new Dictionary<AudioRole, string?> { [AudioRole.Console] = null, [AudioRole.Multimedia] = null });
        Assert.True(result.PartiallySucceeded);
        Assert.NotNull(result.Roles[1].Error);
    }

    [Fact]
    public void ReadbackOnlyInspectsRequestedRolesAndRetainsComError()
    {
        var rolesRead = new List<AudioRole>();
        var result = AudioRolePolicy.ReadBack("requested", false, role =>
        {
            rolesRead.Add(role);
            return null;
        }, new Dictionary<AudioRole, string?> { [AudioRole.Multimedia] = "access denied" });
        Assert.Equal([AudioRole.Console, AudioRole.Multimedia], rolesRead);
        Assert.False(result.Succeeded);
        Assert.Equal("access denied", result.Roles[1].Error);
    }
}
