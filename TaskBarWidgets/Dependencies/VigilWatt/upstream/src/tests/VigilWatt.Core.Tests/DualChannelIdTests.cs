using VigilWatt.Core.Ble;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： DualChannelId —— 同一台设备的经典/LE 端点必须归一到同一个 MAC（手机双条目去重的判据）。
/// 2026/10/24 10:42:18 20211
/// ID 取自真机：手机「十月微凉」的经典 AEP 与 LE AEP。
/// </summary>
public class DualChannelIdTests
{
    private const string ClassicId = "btc:Bluetooth#Bluetooth1c:ce:51:23:28:7b-f4:1a:9c:bc:0e:d1";
    private const string BleId = "ble:BluetoothLE#BluetoothLE1c:ce:51:23:28:7b-f4:1a:9c:bc:0e:d1";

    [Fact]
    public void D1_同一台手机的经典与LE端点取到同一个MAC()
    {
        Assert.Equal("F41A9CBC0ED1", DualChannelId.Mac(ClassicId));
        Assert.Equal(DualChannelId.Mac(ClassicId), DualChannelId.Mac(BleId));
    }

    [Fact]
    public void D2_不同设备的MAC不同()
    {
        var other = "btc:Bluetooth#Bluetooth1c:ce:51:23:28:7b-41:42:02:d6:1e:40";
        Assert.NotEqual(DualChannelId.Mac(ClassicId), DualChannelId.Mac(other));
    }

    [Fact]
    public void D3_非蓝牙ID返回null()
    {
        Assert.Null(DualChannelId.Mac(@"hid:\\?\hid#vid_41e4&pid_2001"));
        Assert.Null(DualChannelId.Mac("sys:battery"));
        Assert.Null(DualChannelId.Mac("dualsense:f4:1a:9c:bc:0e:d1"));
        Assert.Null(DualChannelId.Mac(null));
    }

    [Fact]
    public void D4_只有适配器MAC的畸形ID不参与合并()
    {
        // 只有一段 MAC 时不能拿适配器 MAC 当设备 MAC，否则该适配器下所有设备会互相合并
        Assert.Null(DualChannelId.Mac("btc:Bluetooth#Bluetooth1c:ce:51:23:28:7b"));
    }

    [Fact]
    public void D5_通道判定()
    {
        Assert.True(DualChannelId.IsClassic(ClassicId));
        Assert.True(DualChannelId.IsBle(BleId));
        Assert.False(DualChannelId.IsClassic(BleId));
        Assert.False(DualChannelId.IsBle("hid:xxx"));
    }
}
