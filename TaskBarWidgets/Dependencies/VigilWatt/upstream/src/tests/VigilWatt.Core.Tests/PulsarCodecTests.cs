using VigilWatt.Core.Plugins.Pulsar;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： PulsarCodec —— 派世 17 字节厂商帧（report 0x08）的校验、构造与 POWER/STATUS 解析。
/// 2026/10/18 17:24:10 sheng
/// 用例帧取自本机真机与 Linux hid-pulsar 驱动文档。
/// </summary>
public class PulsarCodecTests
{
    /// <summary>真机 STATUS 应答（鼠标休眠时抓的，[6]=0 表示离线），末字节校验 0x6D。</summary>
    private static readonly byte[] StatusReply =
        Convert.FromHexString("080300000001006506710000000000006D");

    /// <summary>真机 POWER 应答：20%、未充电、电压 0x0DF2=3570 mV，末字节校验 0x34。</summary>
    private static readonly byte[] PowerReply =
        Convert.FromHexString("08040000000214000DF200000000000034");

    [Fact]
    public void Status_request_matches_captured_frame()
    {
        Assert.Equal("080300000000000000000000000000004A", Convert.ToHexString(PulsarCodec.BuildFrame(PulsarCodec.CmdStatus)));
    }

    [Fact]
    public void Power_request_matches_captured_frame()
    {
        Assert.Equal("0804000000000000000000000000000049", Convert.ToHexString(PulsarCodec.BuildFrame(PulsarCodec.CmdPower)));
    }

    [Fact]
    public void Checksum_matches_real_replies()
    {
        Assert.Equal(StatusReply[^1], PulsarCodec.Checksum(StatusReply));
        Assert.Equal(PowerReply[^1], PulsarCodec.Checksum(PowerReply));
    }

    [Fact]
    public void Parse_accepts_matching_command()
    {
        Assert.True(PulsarCodec.TryParse(PowerReply, PulsarCodec.CmdPower, out var payload));
        Assert.Equal(PulsarCodec.FrameLength, payload.Length);
    }

    [Fact]
    public void Parse_rejects_other_command()
    {
        Assert.False(PulsarCodec.TryParse(PowerReply, PulsarCodec.CmdStatus, out _));
    }

    [Fact]
    public void Parse_rejects_bad_checksum()
    {
        var broken = (byte[])PowerReply.Clone();
        broken[^1] ^= 0xFF;
        Assert.False(PulsarCodec.TryParse(broken, PulsarCodec.CmdPower, out _));
    }

    [Fact]
    public void Status_reply_says_offline_when_byte_is_zero()
    {
        // 真机这一帧是鼠标休眠时抓的：[6]=0，dongle 在位但设备没连上。
        Assert.True(PulsarCodec.TryParseOnline(StatusReply, out bool online));
        Assert.False(online);
    }

    [Fact]
    public void Status_reply_says_online_when_byte_is_one()
    {
        var awake = (byte[])StatusReply.Clone();
        awake[6] = 1;
        awake[^1] = PulsarCodec.Checksum(awake);
        Assert.True(PulsarCodec.TryParseOnline(awake, out bool online));
        Assert.True(online);
    }

    [Fact]
    public void Power_reply_yields_percent_charging_and_millivolts()
    {
        Assert.True(PulsarCodec.TryParsePower(PowerReply, out int percent, out bool charging, out int mv));
        Assert.Equal(20, percent);
        Assert.False(charging);
        Assert.Equal(3570, mv);
    }

    [Fact]
    public void Power_rejects_out_of_range_percent()
    {
        var broken = (byte[])PowerReply.Clone();
        broken[6] = 200;
        Assert.False(PulsarCodec.TryParsePower(broken, out _, out _, out _));
    }
}
