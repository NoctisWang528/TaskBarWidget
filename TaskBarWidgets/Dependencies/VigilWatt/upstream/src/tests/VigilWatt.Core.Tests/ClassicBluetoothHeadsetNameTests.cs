using VigilWatt.Core.Ble;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能：罗技经典蓝牙耳机名称归一化测试。
/// 2026/10/23 20:58:09 nyamu
/// </summary>
public sealed class ClassicBluetoothHeadsetNameTests
{
    [Theory]
    [InlineData("PRO X 2", "PRO X 2 LIGHTSPEED")]
    [InlineData(" pro x 2 ", "PRO X 2 LIGHTSPEED")]
    [InlineData("PRO X SUPERLIGHT 2", "PRO X SUPERLIGHT 2")]
    [InlineData("WH-1000XM5", "WH-1000XM5")]
    public void NormalizeBluetoothHeadsetName_OnlyNormalizesKnownHeadset(string input, string expected)
        => Assert.Equal(expected, ClassicDeviceName.NormalizeBluetoothHeadsetName(input));
}
