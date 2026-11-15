using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Razer;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证单次轮询只调度一个雷蛇查询及失败后的恢复。
/// 2026/10/24 19:30:14 Mzood
/// </summary>
public class RazerReadingStateTests
{
    [Fact]
    public void FirstBatteryReadingDoesNotInventChargingState()
    {
        var state = new RazerReadingState();
        Assert.Equal(RazerViperSeCodec.BatteryCommand, state.NextCommand);
        Assert.Null(state.Percent);
        Assert.True(state.TryApply(RazerViperSeCodecTests.CapturedBattery()));
        Assert.Equal(61, state.Percent);
        Assert.Equal(ChargingState.Unknown, state.Charging);
        Assert.Equal(RazerViperSeCodec.ChargingCommand, state.NextCommand);
    }

    [Fact]
    public void AlternatesSingleQueriesAndPreservesBatteryOnChargingReply()
    {
        var state = new RazerReadingState();
        Assert.True(state.TryApply(RazerViperSeCodecTests.CapturedBattery()));
        Assert.True(state.TryApply(RazerViperSeCodecTests.CapturedDischarging()));
        Assert.Equal(61, state.Percent);
        Assert.Equal(ChargingState.Discharging, state.Charging);
        Assert.Equal(RazerViperSeCodec.BatteryCommand, state.NextCommand);
        Assert.True(state.TryApply(RazerViperSeCodecTests.CapturedBattery()));
        Assert.Equal(ChargingState.Discharging, state.Charging);
        Assert.Equal(RazerViperSeCodec.ChargingCommand, state.NextCommand);
    }

    [Theory]
    [InlineData(156, 1, ChargingState.Charging)]
    [InlineData(255, 0, ChargingState.Full)]
    [InlineData(0, 0, ChargingState.Discharging)]
    public void MapsChargingAndFullState(int rawBattery, int flag, ChargingState expected)
    {
        var state = new RazerReadingState();
        var battery = RazerViperSeCodecTests.CapturedBattery();
        battery[10] = (byte)rawBattery;
        RazerViperSeCodecTests.UpdateChecksum(battery);
        var charging = RazerViperSeCodecTests.CapturedDischarging();
        charging[10] = (byte)flag;
        RazerViperSeCodecTests.UpdateChecksum(charging);
        Assert.True(state.TryApply(battery));
        Assert.True(state.TryApply(charging));
        Assert.Equal(expected, state.Charging);
    }

    [Fact]
    public void InvalidChargingReplyFallsBackToBatteryWithoutInventingReading()
    {
        var state = new RazerReadingState();
        Assert.True(state.TryApply(RazerViperSeCodecTests.CapturedBattery()));
        Assert.False(state.TryApply(RazerViperSeCodecTests.CapturedBattery()));
        Assert.Equal(61, state.Percent);
        Assert.Equal(ChargingState.Unknown, state.Charging);
        Assert.Equal(RazerViperSeCodec.BatteryCommand, state.NextCommand);
        Assert.True(state.TryApply(RazerViperSeCodecTests.CapturedBattery()));
    }

    [Fact]
    public void TransportFailureClearsStaleChargingAndRestartsBatteryQuery()
    {
        var state = new RazerReadingState();
        Assert.True(state.TryApply(RazerViperSeCodecTests.CapturedBattery()));
        Assert.True(state.TryApply(RazerViperSeCodecTests.CapturedDischarging()));
        state.RecordFailure();
        Assert.Equal(61, state.Percent);
        Assert.Equal(ChargingState.Unknown, state.Charging);
        Assert.Equal(RazerViperSeCodec.BatteryCommand, state.NextCommand);
    }
}
