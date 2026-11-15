using Microsoft.Win32.SafeHandles;
using VigilWatt.Core.Hid;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证 Feature 专用句柄不会执行输入输出流访问且能正确释放。
/// 2026/10/24 19:33:47 Mzood
/// </summary>
public class HidDeviceHandleTests
{
    [Fact]
    public async Task FeatureOnlyHandleDoesNotReadOrWriteInputOutputStreams()
    {
        var info = new HidDeviceInfo { DevicePath = "test", InputReportLength = 9, OutputReportLength = 9 };
        using var safe = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
        using var handle = new HidDeviceHandle(info, safe);
        Assert.Null(await handle.ReadInputAsync(TimeSpan.FromMilliseconds(1)));
        Assert.False(handle.WriteOutput([0], TimeSpan.FromMilliseconds(1)));
        Assert.Null(handle.GetInputReport(0));
        Assert.False(handle.SendOutputReport([0]));
    }

    [Fact]
    public void DisposingFeatureOnlyHandleClosesOwnedSafeHandle()
    {
        var safe = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
        var handle = new HidDeviceHandle(new HidDeviceInfo { DevicePath = "test" }, safe);
        handle.Dispose();
        handle.Dispose();
        Assert.True(safe.IsClosed);
    }
}
