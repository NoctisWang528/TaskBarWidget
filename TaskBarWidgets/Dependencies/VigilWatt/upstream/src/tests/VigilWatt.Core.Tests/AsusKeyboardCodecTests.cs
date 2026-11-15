using VigilWatt.Core.Plugins.Asus;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证 Omni 配对身份、电量解析和同接收器分组。
/// 2026/10/25 15:34:21 Mzood
/// </summary>
public class AsusKeyboardCodecTests
{
    private static byte[] Pair() => Convert.FromHexString("01A0000100B01A0204" + new string('0', 110));
    private static byte[] Power() => Convert.FromHexString("0212010000005F020100145FBC10" + new string('0', 100));

    [Fact]
    public void RealReceiverIdentifiesScopeAndReports95Percent()
    {
        Assert.True(AsusKeyboardCodec.HasScopeKeyboard(Pair()));
        Assert.True(AsusKeyboardCodec.TryParseBattery(Power(), out int percent, out bool charging));
        Assert.Equal(95, percent);
        Assert.False(charging);
        Assert.Equal(Convert.FromHexString("01A0" + new string('0', 124)), AsusKeyboardCodec.CreatePairedQuery());
        Assert.Equal(Convert.FromHexString("021201" + new string('0', 122)), AsusKeyboardCodec.CreateBatteryQuery());
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0x12)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 255)]
    [InlineData(4, 1)]
    [InlineData(5, 0x25)]
    [InlineData(7, 3)]
    [InlineData(8, 5)]
    public void RejectsWrongPairedModelAndMalformedList(int at, int value)
    {
        var reply = Pair(); reply[at] = (byte)value;
        Assert.False(AsusKeyboardCodec.HasScopeKeyboard(reply));
    }

    [Theory]
    [InlineData(0x1AAE)]
    [InlineData(0x1AAF)]
    [InlineData(0x1AB0)]
    public void AcceptsScopeModelIds(int pid)
    {
        var reply = Pair(); reply[5] = (byte)pid; reply[6] = (byte)(pid >> 8);
        Assert.True(AsusKeyboardCodec.HasScopeKeyboard(reply));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(95, 1, true)]
    [InlineData(100, 0, false)]
    public void ParsesBatteryRangeAndCharging(int value, int flag, bool expectedCharging)
    {
        var reply = Power(); reply[6] = (byte)value; reply[9] = (byte)flag;
        Assert.True(AsusKeyboardCodec.TryParseBattery(reply, out int percent, out bool charging));
        Assert.Equal(value, percent); Assert.Equal(expectedCharging, charging);
    }

    [Fact]
    public void RejectsTruncatedOrUnrelatedPowerReplies()
    {
        Assert.False(AsusKeyboardCodec.HasScopeKeyboard(Pair().AsSpan(0, 8)));
        Assert.False(AsusKeyboardCodec.TryParseBattery(Power().AsSpan(0, 9), out _, out _));
        foreach (var change in new[] { (0, 1), (1, 0x51), (2, 7), (6, 101), (9, 2) })
        {
            var reply = Power(); reply[change.Item1] = (byte)change.Item2;
            Assert.False(AsusKeyboardCodec.TryParseBattery(reply, out _, out _));
        }
    }

    [Fact]
    public void ReceiverKeyGroupsCollectionsButSeparatesPhysicalReceivers()
    {
        string first = @"\\?\HID#VID_0B05&PID_1ACE&MI_02&Col01#8&ABC&0&0000#{guid}";
        string keyboard = @"\\?\HID#VID_0B05&PID_1ACE&MI_02&Col02#8&ABC&0&0001#{guid}";
        string second = keyboard.Replace("ABC", "DEF");
        Assert.Equal(AsusKeyboardCodec.ReceiverKey(first), AsusKeyboardCodec.ReceiverKey(keyboard));
        Assert.NotEqual(AsusKeyboardCodec.ReceiverKey(first), AsusKeyboardCodec.ReceiverKey(second));
        Assert.Null(AsusKeyboardCodec.ReceiverKey("invalid"));
    }

    [Theory]
    [InlineData(0x0B05, 0x1ACE, 0xFF00, 1, 64, 64, 2, "&mi_02&col02#", true)]
    [InlineData(0x0B05, 0x1ACE, 0xFF01, 1, 64, 64, 3, "&mi_02&col03#", false)]
    [InlineData(0x0B05, 0x1ACE, 0xFF00, 1, 64, 64, 1, "&mi_02&col02#", false)]
    [InlineData(0x0B05, 0x1ACE, 0xFF00, 1, 64, 0, 2, "&mi_02&col02#", false)]
    [InlineData(0x0B05, 0x1ACE, 0xFF00, 1, 64, 64, 2, "&mi_03&col02#", false)]
    [InlineData(0x1532, 0x1ACE, 0xFF00, 1, 64, 64, 2, "&mi_02&col02#", false)]
    public void OnlyMatchesKeyboardVendorChannel(int vid, int pid, int page, int usage, int input,
        int output, int id, string path, bool expected) =>
        Assert.Equal(expected, AsusKeyboardCodec.IsKeyboardInterface((ushort)vid, (ushort)pid,
            (ushort)page, (ushort)usage, (ushort)input, (ushort)output, (byte)id, path));
    [Fact]
    public void RejectsDuplicateKeyboardChannelEvenWithKnownModels()
    {
        var reply = Pair();
        reply[3] = 2;
        reply.AsSpan(5, 4).CopyTo(reply.AsSpan(9, 4));
        Assert.False(AsusKeyboardCodec.HasScopeKeyboard(reply));
    }

    [Fact]
    public void PairedCompanionOnAnotherChannelDoesNotChangeKeyboardIdentity()
    {
        var reply = Pair();
        reply[3] = 2;
        reply[9] = 0xFF;
        reply[10] = 0xFF;
        reply[11] = 3;
        reply[12] = 1;
        Assert.True(AsusKeyboardCodec.HasScopeKeyboard(reply));
        reply[11] = 2;
        Assert.False(AsusKeyboardCodec.HasScopeKeyboard(reply));
    }
}
