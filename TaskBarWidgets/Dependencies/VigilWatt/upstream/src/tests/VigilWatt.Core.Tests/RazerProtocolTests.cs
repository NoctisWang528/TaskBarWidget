using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Razer;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 雷蛇协议编解码用例（校验字节、缩放、响应判定）。
/// 2026/10/24 23:45:00 imx1n
/// 已知向量来自 OpenRazer 逆向成果与 razer-battery-report 的实现交叉验证。
/// </summary>
public class RazerProtocolTests
{
    [Fact]
    public void Checksum_IsXorOverBytes2To87()
    {
        // tx=0x1F 查电量：0x02 ^ 0x07 ^ 0x80 = 0x85（参考实现里记录的常量）
        var request = RazerCodec.BuildRequest(RazerCodec.CommandGetBattery, 0x1F);
        Assert.Equal(0x85, request[88]);
        Assert.Equal(90, request.Length);
        Assert.Equal(0x00, request[0]);
        Assert.Equal(0x07, request[6]);
        Assert.Equal(0x80, request[7]);
    }

    [Fact]
    public void Checksum_ChargingQuery_DiffersByCommandId()
    {
        var battery = RazerCodec.BuildRequest(RazerCodec.CommandGetBattery, 0x1F);
        var charging = RazerCodec.BuildRequest(RazerCodec.CommandGetCharging, 0x1F);
        Assert.NotEqual(battery[88], charging[88]);
        Assert.Equal((byte)(battery[88] ^ 0x80 ^ 0x84), charging[88]);
    }

    [Theory]
    [InlineData(161, 63)]   // 0..255 线性映射：161/255 ≈ 63%
    [InlineData(255, 100)]  // 毒蝰 V4 Pro 真机满电回报 0xFF
    [InlineData(0, 0)]
    public void ToPercent_Scaled255(int raw, int expected)
        => Assert.Equal(expected, RazerCodec.ToPercent(0x00E6, raw));

    [Fact]
    public void ToPercent_OutOfRange_IsClamped()
        => Assert.InRange(RazerCodec.ToPercent(0x00E5, 300), 0, 100);

    [Fact]
    public void TryParseBattery_RequiresEchoAndSuccessStatus()
    {
        var response = new byte[RazerCodec.BufferLength];
        response[0] = 0x00;   // report id
        response[1] = 0x02;   // status: success
        response[7] = RazerCodec.CommandClassBattery;
        response[8] = RazerCodec.CommandGetBattery;
        response[10] = 0xA1;  // raw

        Assert.True(RazerCodec.TryParseBattery(response, out int raw));
        Assert.Equal(0xA1, raw);

        // 忙：即使 echo 对也不能采用（可能是上一条命令的应答头）
        response[1] = 0x01;
        Assert.False(RazerCodec.TryParseBattery(response, out _));

        // 命令号不匹配
        response[1] = 0x02;
        response[8] = RazerCodec.CommandGetCharging;
        Assert.False(RazerCodec.TryParseBattery(response, out _));
    }

    [Fact]
    public void TryParseCharging_MapsFlags()
    {
        var response = new byte[RazerCodec.BufferLength];
        response[0] = 0x00;
        response[1] = 0x02;
        response[7] = RazerCodec.CommandClassBattery;
        response[8] = RazerCodec.CommandGetCharging;

        response[10] = 0x01;
        Assert.True(RazerCodec.TryParseCharging(response, out var charging));
        Assert.Equal(ChargingState.Charging, charging);

        response[10] = 0x00;
        Assert.True(RazerCodec.TryParseCharging(response, out charging));
        Assert.Equal(ChargingState.Discharging, charging);
    }

    [Fact]
    public void IsSupported_PicksThe91ByteInterfaceOnly()
    {
        // 同一台毒蝰 V4 Pro 有两条厂商接口：消费控制口 91 字节（经典协议）、厂商口 62 字节（另一套）。
        Assert.True(RazerCodec.IsSupported(0x1532, 0x00E6, 91));   // 接收器，真机验证
        Assert.True(RazerCodec.IsSupported(0x1532, 0x00E5, 91));   // 本体 PID
        Assert.False(RazerCodec.IsSupported(0x1532, 0x00E6, 61));  // 62 字节那条接口，不能认
        Assert.False(RazerCodec.IsSupported(0x1532, 0x0016, 91));  // 未登记 PID
        Assert.False(RazerCodec.IsSupported(0x046D, 0x00E6, 91));  // 别的品牌
    }

    [Fact]
    public void TryParseBattery_RealDeviceFrame_ReturnsFull()
    {
        // 2026-10-24 真机应答（1532:00E6 接口 3）：status=0x02，args[1]=0xFF，校验 0x85
        var frame = new byte[RazerCodec.BufferLength];
        frame[0] = 0x00;  // report id
        frame[1] = 0x02;  // status: success
        frame[2] = 0x1F;  // transaction echo
        frame[6] = 0x02;  // data size
        frame[7] = RazerCodec.CommandClassBattery;
        frame[8] = RazerCodec.CommandGetBattery;
        frame[10] = 0xFF; // args[1] = level
        frame[89] = 0x85;

        Assert.True(RazerCodec.TryParseBattery(frame, out int raw));
        Assert.Equal(0xFF, raw);
        Assert.Equal(100, RazerCodec.ToPercent(0x00E6, raw));
    }
}
