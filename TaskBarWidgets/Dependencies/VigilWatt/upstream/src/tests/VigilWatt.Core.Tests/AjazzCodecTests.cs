using VigilWatt.Core.Plugins.Ajazz;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证 AK870 接收器状态解析及无效帧拒绝。
/// 2026/10/18 14:45:00 KNA7022
/// </summary>
public class AjazzCodecTests
{
    private static byte[] Sample() => Convert.FromHexString("000029000001010160" + new string('0', 110));

    [Fact]
    public void CapturedAk870Reports41Percent()
    {
        Assert.True(AjazzCodec.TryParse(Sample(), out int percent, out bool online));
        Assert.Equal(41, percent);
        Assert.True(online);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void AcceptsBatteryEndpoints(int value)
    {
        var frame = Sample(); frame[2] = (byte)value;
        Assert.True(AjazzCodec.TryParse(frame, out int percent, out _));
        Assert.Equal(value, percent);
    }

    [Fact]
    public void OfflineFlagDoesNotInventOnlineBattery()
    {
        var frame = Sample(); frame[4] = 1;
        Assert.True(AjazzCodec.TryParse(frame, out _, out bool online));
        Assert.False(online);
    }

    [Fact]
    public void RejectsShortInvalidAndUnrelatedReplies()
    {
        Assert.False(AjazzCodec.TryParse([], out _, out _));
        Assert.False(AjazzCodec.TryParse(new byte[65], out _, out _));
        var frame = Sample(); frame[2] = 101;
        Assert.False(AjazzCodec.TryParse(frame, out _, out _));
        frame = Sample(); frame[1] = 0x8f;
        Assert.False(AjazzCodec.TryParse(frame, out _, out _));
        frame = Sample(); frame[4] = 2;
        Assert.False(AjazzCodec.TryParse(frame, out _, out _));
    }

    [Fact]
    public void QueryMatchesDocumentedBit7Checksum()
    {
        var query = AjazzCodec.CreateStatusQuery();
        Assert.Equal(64, query.Length);
        Assert.Equal(0xf7, query[0]);
        Assert.Equal(0x08, query[7]);
        Assert.Equal(255, query.Take(8).Sum(b => b));
        Assert.All(query.Skip(8), b => Assert.Equal(0, b));
    }
}
