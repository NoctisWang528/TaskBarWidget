using VigilWatt.Core.Plugins.Razer;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证雷蛇认领只命中实测型号的电量接口。
/// 2026/10/24 19:30:14 Mzood
/// </summary>
public class RazerDeviceProfileTests
{
    [Theory]
    [InlineData(@"\\?\hid#vid_1532&pid_00df&mi_00#receiver")]
    [InlineData(@"\\?\HID#VID_1532&PID_00DF&MI_00#receiver")]
    public void AcceptsKnownWirelessMouseInterface(string path) =>
        Assert.True(RazerDeviceProfile.Matches(0x1532, 0x00DF, 1, 2, 91, path));

    [Theory]
    [InlineData(0x1533, 0x00DF, 1, 2, 91, "&mi_00#")]
    [InlineData(0x1532, 0x00DE, 1, 2, 91, "&mi_00#")]
    [InlineData(0x1532, 0x00C1, 1, 2, 91, "&mi_00#")]
    [InlineData(0x1532, 0x00DF, 1, 6, 91, "&mi_00#")]
    [InlineData(0x1532, 0x00DF, 0xFF00, 2, 91, "&mi_00#")]
    [InlineData(0x1532, 0x00DF, 1, 2, 90, "&mi_00#")]
    [InlineData(0x1532, 0x00DF, 1, 2, 0, "&mi_00#")]
    [InlineData(0x1532, 0x00DF, 1, 2, 91, "&mi_01#")]
    [InlineData(0x1532, 0x00DF, 1, 2, 91, "&mi_00&col03#")]
    public void RejectsOtherModelsAndCollections(int vid, int pid, int page, int usage, int length, string path) =>
        Assert.False(RazerDeviceProfile.Matches((ushort)vid, (ushort)pid,
            (ushort)page, (ushort)usage, (ushort)length, path));
}
