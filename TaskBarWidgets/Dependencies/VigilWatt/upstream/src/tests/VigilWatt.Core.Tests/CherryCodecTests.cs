using VigilWatt.Core.Plugins.Cherry;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： CherryCodec —— CHERRY 无线键盘 64 字节厂商块帧（report 0x04）的纯编解码。
/// 2026/10/18 17:24:10 sheng
/// 用例帧取自本机真机：块 0x1D 设备信息、块 0x1A 电量 48%/51%（含充电位，与官方软件 UI 一致）。
/// </summary>
public class CherryCodecTests
{
    /// <summary>CHERRY 的 USB VID（dongle 046A:01E4，键盘本体 046A:01E2）。</summary>
    private const ushort CherryVid = 0x046A;

    /// <summary>帧头 8 字节：report 0x04 / 令牌 0 / 块 / 长度 / 偏移 0 / 保留 0。</summary>
    private static byte[] Frame(byte block, byte length, string payloadHex)
    {
        var frame = new byte[CherryCodec.FrameLength];
        frame[0] = CherryCodec.ReportId;
        frame[3] = block;
        frame[4] = length;
        var payload = Convert.FromHexString(payloadHex);
        Array.Copy(payload, 0, frame, CherryCodec.PayloadOffset, payload.Length);
        return frame;
    }

    /// <summary>真机块 0x1D 应答：签名 aa 55 + VID 0x046A + 键盘自身 PID 0x01E2（不是 dongle 的 0x01E4）。</summary>
    private static readonly byte[] DeviceInfoReply =
        Frame(CherryCodec.BlockDeviceInfo, 0x18, "AA5500006A04E201010001000100");

    /// <summary>真机块 0x1A 应答：payload[0]=0x30=48，payload[1]=0 未充电。</summary>
    private static readonly byte[] BatteryReply = Frame(CherryCodec.BlockBattery, 4, "30000000");

    /// <summary>插线充电时的块 0x1A：payload[0]=0x33=51、payload[1]=1 充电（官方软件同时画闪电）。</summary>
    private static readonly byte[] ChargingReply = Frame(CherryCodec.BlockBattery, 4, "33010000");

    [Fact]
    public void Request_has_report_id_block_length_and_zero_payload()
    {
        var frame = CherryCodec.BuildRequest(CherryCodec.BlockBattery, 4);
        Assert.Equal(CherryCodec.FrameLength, frame.Length);
        Assert.Equal(0x04, frame[0]);
        Assert.Equal(0x1A, frame[3]);
        Assert.Equal(4, frame[4]);
        Assert.Equal(0, frame[5]);
        Assert.Equal(0, frame[6]);
        Assert.All(frame.Skip(CherryCodec.PayloadOffset), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Request_carries_little_endian_offset()
    {
        var frame = CherryCodec.BuildRequest(CherryCodec.BlockBattery, 8, 0x0123);
        Assert.Equal(0x23, frame[5]);
        Assert.Equal(0x01, frame[6]);
    }

    [Fact]
    public void Device_info_parses_signature_vid_and_pid()
    {
        Assert.True(CherryCodec.TryParse(DeviceInfoReply, CherryCodec.BlockDeviceInfo, out var payload));
        Assert.True(CherryCodec.TryParseDeviceInfo(payload, CherryVid, out ushort pid));
        Assert.Equal(0x01E2, pid);
    }

    [Fact]
    public void Device_info_rejects_foreign_vid()
    {
        Assert.True(CherryCodec.TryParse(DeviceInfoReply, CherryCodec.BlockDeviceInfo, out var payload));
        Assert.False(CherryCodec.TryParseDeviceInfo(payload, 0x3554, out _));
    }

    [Fact]
    public void Device_info_rejects_missing_signature()
    {
        var broken = (byte[])DeviceInfoReply.Clone();
        broken[CherryCodec.PayloadOffset] = 0x00;
        Assert.True(CherryCodec.TryParse(broken, CherryCodec.BlockDeviceInfo, out var payload));
        Assert.False(CherryCodec.TryParseDeviceInfo(payload, CherryVid, out _));
    }

    [Fact]
    public void Battery_reply_yields_percent_and_not_charging()
    {
        Assert.True(CherryCodec.TryParse(BatteryReply, CherryCodec.BlockBattery, out var payload));
        Assert.True(CherryCodec.TryParseBattery(payload, out int percent, out bool charging));
        Assert.Equal(48, percent);
        Assert.False(charging);
    }

    [Fact]
    public void Battery_reply_flags_charging_when_second_byte_is_one()
    {
        Assert.True(CherryCodec.TryParse(ChargingReply, CherryCodec.BlockBattery, out var payload));
        Assert.True(CherryCodec.TryParseBattery(payload, out int percent, out bool charging));
        Assert.Equal(51, percent);
        Assert.True(charging);
    }

    [Fact]
    public void Battery_reply_with_one_byte_cannot_claim_charging()
    {
        Assert.True(CherryCodec.TryParse(BatteryReply, CherryCodec.BlockBattery, out var payload));
        Assert.True(CherryCodec.TryParseBattery(payload[..1], out int percent, out bool charging));
        Assert.Equal(48, percent);
        Assert.False(charging);
    }

    [Fact]
    public void Battery_rejects_out_of_range_value()
    {
        var broken = (byte[])BatteryReply.Clone();
        broken[CherryCodec.PayloadOffset] = 101;
        Assert.True(CherryCodec.TryParse(broken, CherryCodec.BlockBattery, out var payload));
        Assert.False(CherryCodec.TryParseBattery(payload, out _, out _));
    }

    [Fact]
    public void Parse_rejects_wrong_block()
    {
        Assert.False(CherryCodec.TryParse(BatteryReply, CherryCodec.BlockDeviceInfo, out _));
    }

    [Fact]
    public void Parse_rejects_zero_length()
    {
        var broken = (byte[])BatteryReply.Clone();
        broken[4] = 0;
        Assert.False(CherryCodec.TryParse(broken, CherryCodec.BlockBattery, out _));
    }
}
