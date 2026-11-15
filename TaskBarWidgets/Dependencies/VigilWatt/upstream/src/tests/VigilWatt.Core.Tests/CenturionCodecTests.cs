using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Logitech;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能：Centurion 电量帧与接口判据的回归测试。
/// 2026/10/23 20:58:09 nyamu
/// </summary>
public sealed class CenturionCodecTests
{
    [Theory]
    [InlineData(0x046D, 0x0AF7, 0xFFA0, 0x0001, 64, 64, true)]
    [InlineData(0x046D, 0x0AF7, 0xFF43, 0x0001, 64, 64, false)]
    [InlineData(0x046D, 0x0AF7, 0xFFA0, 0x0001, 63, 64, false)]
    [InlineData(0x046D, 0x0AF6, 0xFFA0, 0x0001, 64, 64, false)]
    public void IsSupportedInterface_RequiresExactKnownEndpoint(ushort vid, ushort pid, ushort usagePage, ushort usage, ushort inputLength, ushort outputLength, bool expected)
        => Assert.Equal(expected, CenturionCodec.IsSupportedInterface(vid, pid, usagePage, usage, inputLength, outputLength));
    [Fact]
    public void BuildBatteryRequest_MatchesKnownReadOnlyRequest()
    {
        var packet = CenturionCodec.BuildBatteryRequest();

        Assert.Equal(64, packet.Length);
        Assert.Equal(new byte[] { 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A }, packet[..10]);
        Assert.All(packet[10..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0x00, ChargingState.Discharging)]
    [InlineData(0x02, ChargingState.Charging)]
    [InlineData(0x03, ChargingState.Unknown)]
    public void TryParseBatteryResponse_ParsesKnownFields(byte chargeState, ChargingState expected)
    {
        var packet = new byte[64];
        packet[0] = 0x51;
        packet[1] = 0x0B;
        packet[8] = 0x04;
        packet[10] = 75;
        packet[12] = chargeState;

        Assert.True(CenturionCodec.TryParseBatteryResponse(packet, out var percent, out var charging));
        Assert.Equal(75, percent);
        Assert.Equal(expected, charging);
    }

    [Theory]
    [InlineData(0x50, 0x0B, 0x04, 75)]
    [InlineData(0x51, 0x05, 0x04, 75)]
    [InlineData(0x51, 0x0B, 0x03, 75)]
    [InlineData(0x51, 0x0B, 0x04, 101)]
    public void TryParseBatteryResponse_RejectsUnrelatedOrInvalidFrames(byte report, byte kind, byte marker, byte percent)
    {
        var packet = new byte[64];
        packet[0] = report;
        packet[1] = kind;
        packet[8] = marker;
        packet[10] = percent;

        Assert.False(CenturionCodec.TryParseBatteryResponse(packet, out _, out _));
    }

    [Fact]
    public void TryParseBatteryResponse_RejectsShortFrame()
        => Assert.False(CenturionCodec.TryParseBatteryResponse(new byte[12], out _, out _));
}
