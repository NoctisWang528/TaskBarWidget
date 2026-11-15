using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Razer;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证耳机 PA 实机响应、查询白名单和鼠标协议隔离。
/// 2026/10/25 15:34:21 Mzood
/// </summary>
public class RazerHeadsetCodecTests
{
    internal static byte[] Battery() => Convert.FromHexString(
        "020E504908CE92DE050004002101014F4901C00C6B00000300119313000000000000000000000000000000000000000000000000000000000000000000000000");
    internal static byte[] Charging() => Convert.FromHexString(
        "020E504908CFB6E0050004002A0101004901C00C6B00000300119313000000000000000000000000000000000000000000000000000000000000000000000000");

    [Fact]
    public void CapturedRepliesReport79PercentAndDischarging()
    {
        Assert.True(RazerHeadsetCodec.TryParseBattery(Battery(), out int percent));
        Assert.Equal(79, percent);
        Assert.True(RazerHeadsetCodec.TryParseCharging(Charging(), out bool charging));
        Assert.False(charging);
    }

    [Fact]
    public void QueryAndRemoteFramesMatchKnownProtocol()
    {
        var query = new byte[64];
        query[0] = 2; query[1] = 0x80; query[2] = 8; query[5] = 0x50; query[6] = 0x41;
        query[7] = 8; query[9] = 3; query[10] = 0x21;
        Assert.Equal(query, RazerHeadsetCodec.CreateQuery(0x21));
        var on = query.ToArray();
        on[2] = 7; on[7] = 0x0E; on[9] = 2; on[10] = 0xE1; on[11] = 1;
        Assert.Equal(on, RazerHeadsetCodec.CreateRemoteMode(true));
        on[11] = 0;
        Assert.Equal(on, RazerHeadsetCodec.CreateRemoteMode(false));
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerHeadsetCodec.CreateQuery(0x93));
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerHeadsetCodec.CreateQuery(0xE1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(12, 0x2A)]
    [InlineData(13, 0)]
    [InlineData(13, 2)]
    [InlineData(14, 0)]
    [InlineData(14, 50)]
    [InlineData(15, 101)]
    public void RejectsOtherCommandsNotificationsAndInvalidValues(int at, int value)
    {
        var reply = Battery(); reply[at] = (byte)value;
        Assert.False(RazerHeadsetCodec.TryParseBattery(reply, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void AcceptsTrueBatteryEndpoints(int value)
    {
        var reply = Battery(); reply[15] = (byte)value;
        Assert.True(RazerHeadsetCodec.TryParseBattery(reply, out int percent));
        Assert.Equal(value, percent);
    }

    [Fact]
    public void RejectsTruncatedReportsAndMouseResponses()
    {
        Assert.False(RazerHeadsetCodec.TryParseBattery(Battery().AsSpan(0, 63), out _));
        Assert.False(RazerHeadsetCodec.TryParseBattery(new byte[65], out _));
        Assert.False(RazerHeadsetCodec.TryParseBattery(RazerViperSeCodecTests.CapturedBattery(), out _));
        Assert.False(RazerViperSeCodec.TryParseBattery(Battery(), out _));
    }

    [Fact]
    public void HeadsetScheduleIsIndependentFromMouseSchedule()
    {
        var headset = new RazerReadingState(headset: true);
        var mouse = new RazerReadingState();
        Assert.Equal((byte)0x21, headset.NextCommand);
        Assert.True(headset.TryApply(Battery()));
        Assert.Equal((byte)0x2A, headset.NextCommand);
        Assert.Equal((byte)0x80, mouse.NextCommand);
        Assert.True(headset.TryApply(Charging()));
        Assert.Equal(79, headset.Percent);
        Assert.Equal(ChargingState.Discharging, headset.Charging);
        headset.RecordFailure();
        Assert.Equal((byte)0x21, headset.NextCommand);
        Assert.Equal(ChargingState.Unknown, headset.Charging);
    }

    [Theory]
    [InlineData(0x1532, 0x0555, 0xFF00, 1, 64, 64, 2, "&mi_03&col02#", true)]
    [InlineData(0x1532, 0x0556, 0xFF00, 1, 64, 64, 2, "&mi_03&col02#", false)]
    [InlineData(0x1532, 0x0555, 0x000C, 1, 64, 64, 2, "&mi_03&col02#", false)]
    [InlineData(0x1532, 0x0555, 0xFF00, 1, 64, 64, 1, "&mi_03&col02#", false)]
    [InlineData(0x1532, 0x0555, 0xFF00, 1, 64, 0, 2, "&mi_03&col02#", false)]
    [InlineData(0x1532, 0x0555, 0xFF00, 1, 64, 64, 2, "&mi_03&col01#", false)]
    public void HeadsetMatcherOnlyAcceptsVerifiedInterface(int vid, int pid, int page, int usage,
        int input, int output, int id, string path, bool expected) =>
        Assert.Equal(expected, RazerDeviceProfile.MatchesHeadset((ushort)vid, (ushort)pid,
            (ushort)page, (ushort)usage, (ushort)input, (ushort)output, (byte)id, path));
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ParsesHeadsetChargingFlagWithoutApplyingMouseRestriction(int value, bool expected)
    {
        var reply = Charging();
        reply[15] = (byte)value;
        Assert.True(RazerHeadsetCodec.TryParseCharging(reply, out bool charging));
        Assert.Equal(expected, charging);
        Assert.False(RazerHeadsetCodec.TryParseBattery(reply, out _));
        reply[13] = 0;
        Assert.False(RazerHeadsetCodec.TryParseCharging(reply, out _));
    }
}
