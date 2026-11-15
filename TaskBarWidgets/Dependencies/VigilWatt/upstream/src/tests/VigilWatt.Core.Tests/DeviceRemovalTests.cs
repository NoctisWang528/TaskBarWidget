using VigilWatt.Core.Devices;
using VigilWatt.Core.Settings;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 删除设备后的两份名单判定（黑名单 / 删除标记）。
/// 2026/10/20 20:30:00 20211
/// 判错就是"删了又复活"或"设备回来了却不显示"，两种都是用户直接看得见的错。
/// </summary>
public sealed class DeviceRemovalTests
{
    private static readonly Dictionary<string, BlockedDevice> Blocked = new()
    {
        ["kb"] = new BlockedDevice { Name = "键盘", BlockedAt = new DateTimeOffset(2026, 10, 20, 20, 0, 0, TimeSpan.Zero) },
    };

    private static readonly Dictionary<string, long> Dismissed = new() { ["mouse"] = 1 };

    [Theory]
    [InlineData("kb", true, true)]      // 黑名单：在线也丢，永久生效
    [InlineData("kb", false, true)]
    [InlineData("mouse", false, true)]  // 删除标记：离线才挡
    [InlineData("mouse", true, false)]  // 设备真回来了，放行
    [InlineData("other", false, false)]
    [InlineData("other", true, false)]
    public void R1_按名单与在线状态决定丢弃(string id, bool isOnline, bool expected)
        => Assert.Equal(expected, DeviceRemoval.ShouldDrop(Blocked, Dismissed, id, isOnline));

    [Theory]
    [InlineData("mouse", true, true)]
    [InlineData("mouse", false, false)]
    [InlineData("kb", true, false)]     // 黑名单不在删除标记里，不能被"上线"顺手解除
    [InlineData("other", true, false)]
    public void R2_只有删除标记且已上线才解除(string id, bool isOnline, bool expected)
        => Assert.Equal(expected, DeviceRemoval.ShouldClearDismissal(Dismissed, id, isOnline));
}
