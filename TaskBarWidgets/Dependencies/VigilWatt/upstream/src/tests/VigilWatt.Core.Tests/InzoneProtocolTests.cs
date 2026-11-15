using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Sony;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 索尼 INZONE H9 II 帧编解码用例。
/// 2026/10/24 23:45:00 imx1n
/// 向量全部来自 2026-10-24 真机（USBPcap 抓包 + SET_REPORT 实测）。
/// </summary>
public class InzoneProtocolTests
{
    [Fact]
    public void BuildBatteryRequest_MatchesCapturedFrame()
    {
        // pcap 里客户端真实发出的 SET_REPORT 载荷（64 字节，后 50 字节全 0）
        var expected = new byte[64];
        byte[] head = { 0x02, 0x0C, 0x01, 0x00, 0xFC, 0x08, 0x96, 0xC3, 0x41, 0x04, 0x01, 0x01, 0x00, 0xA0 };
        head.CopyTo(expected, 0);

        Assert.Equal(expected, InzoneCodec.BuildBatteryRequest());
    }

    [Theory]
    [InlineData(0x41, 0x04, 0xA0)]   // 电量
    [InlineData(0x41, 0x06, 0xA2)]   // 状态（含音量）
    [InlineData(0x41, 0x03, 0x9F)]   // 设备信息
    [InlineData(0x21, 0x01, 0x7D)]   // 状态总览
    [InlineData(0x21, 0x09, 0x85)]
    public void Checksum_MatchesCapturedFrames(byte cls, byte id, byte expected)
        => Assert.Equal(expected, InzoneCodec.Checksum(cls, id));

    [Fact]
    public void TryParseBattery_RealDeviceFrame_Returns78()
    {
        // 真机应答：02 0E 04 FF 0B 00 96 C3 14 04 10 01 00 00 4E D0 ...
        var frame = new byte[64];
        byte[] head =
        {
            0x02, 0x0E, 0x04, 0xFF, 0x0B, 0x00, 0x96, 0xC3,
            0x14, 0x04, 0x10, 0x01, 0x00, 0x00, 0x4E, 0xD0,
        };
        head.CopyTo(frame, 0);

        Assert.True(InzoneCodec.TryParseBattery(frame, out int percent, out var charging));
        Assert.Equal(0x4E, percent);
        Assert.Equal(ChargingState.Discharging, charging);   // [13]=0x00
    }

    [Fact]
    public void TryParseBattery_AcceptsNotifyFlag()
    {
        var frame = new byte[64];
        frame[0] = 0x02;
        frame[8] = 0x14;
        frame[9] = 0x04;
        frame[10] = 0xA0;
        frame[14] = 55;

        Assert.True(InzoneCodec.TryParseBattery(frame, out int percent, out _));
        Assert.Equal(55, percent);
    }

    [Fact]
    public void TryParseBattery_RejectsOtherCommandsAndOutOfRange()
    {
        var frame = new byte[64];
        frame[0] = 0x02;
        frame[8] = 0x14;
        frame[9] = 0x06;   // 状态帧，不是电量帧
        frame[10] = 0x10;
        frame[14] = 78;
        Assert.False(InzoneCodec.TryParseBattery(frame, out _, out _));

        frame[9] = 0x04;
        frame[14] = 0xFF;  // 越界值不能当百分比
        Assert.False(InzoneCodec.TryParseBattery(frame, out _, out _));
    }

    [Fact]
    public void TryParseBattery_ChargingFlagFromRealFrame()
    {
        // 插线充电时的真机帧：02 0E 04 FF 0B 00 96 C3 14 04 10 01 00 01 4F D2 …
        var frame = new byte[64];
        frame[0] = 0x02;
        frame[8] = 0x14;
        frame[9] = 0x04;
        frame[10] = 0x10;
        frame[13] = 0x01;
        frame[14] = 0x4F;

        Assert.True(InzoneCodec.TryParseBattery(frame, out int percent, out var charging));
        Assert.Equal(79, percent);
        Assert.Equal(ChargingState.Charging, charging);
    }

    [Fact]
    public void IsSupportedInterface_RequiresVendorPageAndFrameLength()
    {
        Assert.True(InzoneCodec.IsSupportedInterface(0x054C, 0x0FA8, 0xFF04, 64));
        Assert.False(InzoneCodec.IsSupportedInterface(0x054C, 0x0FA8, 0xFF13, 64)); // 另一条厂商通道
        Assert.False(InzoneCodec.IsSupportedInterface(0x054C, 0x0FA8, 0xFF04, 62)); // 长度不对
        Assert.False(InzoneCodec.IsSupportedInterface(0x054C, 0x0CE6, 0xFF04, 64)); // DualSense
    }
}
