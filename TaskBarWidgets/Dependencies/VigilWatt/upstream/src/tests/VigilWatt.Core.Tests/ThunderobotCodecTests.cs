using VigilWatt.Core.Plugins.Thunderobot;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证雷神实机电量帧与非电量数据的隔离。
/// 2026/10/18 14:55:00 KNA7022
/// </summary>
public class ThunderobotCodecTests
{
    private static byte[] Sample() => Convert.FromHexString(
        "a1020401110211e3019b360213313233343536022523022601" + new string('0', 78));

    [Fact]
    public void CapturedMl903Reports35Percent()
    {
        Assert.True(ThunderobotCodec.TryParse(Sample(), out int percent));
        Assert.Equal(35, percent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void BatteryEndpointsAreValid(int value)
    {
        var report = Sample(); report[21] = (byte)value;
        Assert.True(ThunderobotCodec.TryParse(report, out int percent));
        Assert.Equal(value, percent);
    }

    [Fact]
    public void RejectsWrongReportMissingBatteryAndUnknownTags()
    {
        Assert.False(ThunderobotCodec.TryParse([], out _));
        var report = Sample(); report[0] = 0xa0;
        Assert.False(ThunderobotCodec.TryParse(report, out _));
        report = Sample(); report[21] = 101;
        Assert.False(ThunderobotCodec.TryParse(report, out _));
        report = Sample(); report[20] = 0x27;
        Assert.False(ThunderobotCodec.TryParse(report, out _));
        report = Sample(); Array.Clear(report, 19, 45);
        Assert.False(ThunderobotCodec.TryParse(report, out _));
    }

    [Fact]
    public void TagInsideSerialNumberIsNotBattery()
    {
        var report = Sample(); report[13] = 2; report[14] = 0x25; report[15] = 99;
        Assert.True(ThunderobotCodec.TryParse(report, out int percent));
        Assert.Equal(35, percent);
    }

    [Fact]
    public void QueryContainsOnlyOfficialGetDeviceInfo()
    {
        var query = ThunderobotCodec.CreateQuery();
        Assert.Equal(64, query.Length);
        Assert.Equal(0xa0, query[0]); Assert.Equal(2, query[1]);
        Assert.All(query.Skip(2), b => Assert.Equal(0, b));
    }
}
