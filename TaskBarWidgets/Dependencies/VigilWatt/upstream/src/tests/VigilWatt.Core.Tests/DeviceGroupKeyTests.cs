using VigilWatt.Core.Capture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 物理设备分组键：有 ContainerId 用它，读不到才退回 VID/PID。
/// 2026/10/24 20:05:00 20211
/// 回归的是真事：雷柏 dongle 的 MI_00/MI_01/MI_02 曾各自成组，列表里三行同名，用户选了 513 字节那条。
/// </summary>
public sealed class DeviceGroupKeyTests
{
    private static readonly Guid ContainerA = new("163a1127-c311-50ff-b518-fabe5764c865");
    private static readonly Guid ContainerB = new("9f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f");

    [Fact]
    public void For_UsesContainer_WhenAvailable()
    {
        var key = DeviceGroupKey.For(ContainerA, 0x24AE, 0x1412);

        Assert.Equal($"container:{ContainerA:D}", key);
        Assert.StartsWith("container:", key, StringComparison.Ordinal);
    }

    [Fact]
    public void For_FallsBackToVidPid_WhenContainerMissing()
    {
        var fallback = DeviceGroupKey.For(null, 0x24AE, 0x1412);

        Assert.Equal("vidpid:24AE:1412", fallback);
        // 预检与报告靠这个区分「系统给的归属」和「兜底归组」，别让兜底看起来像精确结果
        Assert.False(DeviceGroupKey.IsContainer(fallback));
        Assert.True(DeviceGroupKey.IsContainer(DeviceGroupKey.For(ContainerA, 0x24AE, 0x1412)));
    }

    [Fact]
    public void DifferentInterfacesOfOneDevice_ShareTheKey()
    {
        // 同一个 dongle 的 MI_00 / MI_01 / MI_02 在系统里是三个设备节点，但共用一个 ContainerId
        var mi00 = DeviceGroupKey.For(ContainerA, 0x24AE, 0x1412);
        var mi01 = DeviceGroupKey.For(ContainerA, 0x24AE, 0x1412);
        var mi02 = DeviceGroupKey.For(ContainerA, 0x24AE, 0x1412);

        Assert.Equal(mi00, mi01);
        Assert.Equal(mi01, mi02);
    }

    [Fact]
    public void TwoIdenticalDongles_StayApart()
    {
        // VID/PID 相同但容器不同 = 两只同型号设备，不能并成一台（VID/PID 兜底会并，这是它已知的取舍）
        var first = DeviceGroupKey.For(ContainerA, 0x24AE, 0x1412);
        var second = DeviceGroupKey.For(ContainerB, 0x24AE, 0x1412);

        Assert.NotEqual(first, second);
        Assert.Equal(DeviceGroupKey.FromVidPid(0x24AE, 0x1412), DeviceGroupKey.For(null, 0x24AE, 0x1412));
    }
}
