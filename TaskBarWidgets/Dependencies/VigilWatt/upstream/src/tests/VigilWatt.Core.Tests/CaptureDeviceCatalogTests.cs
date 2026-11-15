using VigilWatt.Core.Capture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 设备分组与推荐接口：把 30 个接口收成几台设备，并挑出「最可能采到东西」的那个接口。
/// 2026/10/24 13:10:00 20211
/// 回归的是真事：用户第一次采集选了 MI_02（513 字节大缓冲通道），被动监听零报文。
/// </summary>
public sealed class CaptureDeviceCatalogTests
{
    private static CaptureDeviceInfo If(string key, ushort inLen = 0, ushort outLen = 0, ushort featLen = 0,
        ushort usagePage = 0xFF00, ushort usage = 0x0002, bool denied = false, byte[]? inIds = null,
        ushort vid = 0x24AE, ushort pid = 0x1412, string? groupKey = null, string product = "Test Dongle")
    {
        return new CaptureDeviceInfo
        {
            DeviceId = $"cap:{vid:X4}:{pid:X4}:{key}",
            VendorId = vid,
            ProductId = pid,
            Product = product,
            Manufacturer = "TestVendor",
            SerialNumber = "SN-0001",
            InterfacePath = $@"\\?\hid#{key}#8&abc&0&0001#{{guid}}",
            InterfaceKey = key,
            GroupKey = groupKey ?? DeviceGroupKey.FromVidPid(vid, pid),
            UsagePage = usagePage,
            Usage = usage,
            InputReportLength = inLen,
            OutputReportLength = outLen,
            FeatureReportLength = featLen,
            InputReportIds = inIds ?? (inLen > 0 ? [(byte)0x07] : []),
            AccessDenied = denied,
            OpenError = denied ? 5 : null,
            CapsAvailable = !denied,
        };
    }

    [Fact]
    public void Group_MergesInterfacesOfSamePhysicalDevice()
    {
        var devices = new[]
        {
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, outLen: 19),
            If("VID_24AE&PID_1412&MI_01&Col06", featLen: 33),
            If("VID_24AE&PID_1412&MI_01&Col02", denied: true),
            If("VID_41E4&PID_2001&MI_01&Col01", inLen: 20, vid: 0x41E4, pid: 0x2001),
        };

        var groups = CaptureDeviceCatalog.Group(devices);

        Assert.Equal(2, groups.Count);
        var rap = groups.Single(g => g.VendorId == 0x24AE);
        Assert.Equal(3, rap.Interfaces.Count);
        Assert.Equal("SN-0001", rap.SerialNumber);
        Assert.False(rap.IsAdapted);
    }

    [Fact]
    public void Group_MergesAllInterfacesOfOneContainer_AcrossMiNumbers()
    {
        // 真机形态：雷柏 24AE:1412 的 MI_00 / MI_01 / MI_02 共用一个 ContainerId
        var container = DeviceGroupKey.FromContainer(new Guid("163a1127-c311-50ff-b518-fabe5764c865"));
        var devices = new[]
        {
            If("VID_24AE&PID_1412&MI_00", groupKey: container),
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, outLen: 19, groupKey: container),
            If("VID_24AE&PID_1412&MI_02", inLen: 513, outLen: 513, groupKey: container),
        };

        var groups = CaptureDeviceCatalog.Group(devices);

        var single = Assert.Single(groups);
        Assert.Equal(3, single.Interfaces.Count);
        // 收成一台之后，推荐才能落到 19 字节那条，而不是 513 字节大缓冲
        Assert.Equal(19, single.Recommended!.InputReportLength);
    }

    [Fact]
    public void Group_KeepsTwoIdenticalDonglesApart_WhenContainersDiffer()
    {
        var first = DeviceGroupKey.FromContainer(new Guid("163a1127-c311-50ff-b518-fabe5764c865"));
        var second = DeviceGroupKey.FromContainer(new Guid("9f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f"));
        var devices = new[]
        {
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, groupKey: first),
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, groupKey: second),
        };

        var groups = CaptureDeviceCatalog.Group(devices);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Interfaces));
    }

    [Fact]
    public void Group_TakesProductNameFromAnyInterface_NotJustTheFirst()
    {
        // 真机形态：MI_00 打不开 → 读不到产品名；它按接口段排序又在最前，整组标题不能因此退成 VID/PID
        var container = DeviceGroupKey.FromContainer(new Guid("163a1127-c311-50ff-b518-fabe5764c865"));
        var devices = new[]
        {
            If("VID_24AE&PID_1412&MI_00", denied: true, product: "", groupKey: container),
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, outLen: 19, groupKey: container),
        };

        var group = Assert.Single(CaptureDeviceCatalog.Group(devices));

        Assert.Equal("Test Dongle", group.Title);
        Assert.Equal("TestVendor", group.Manufacturer);
        // 打不开的接口沉底，排最前的是能采的那条
        Assert.False(group.Interfaces[0].AccessDenied);
        Assert.True(group.Interfaces[^1].AccessDenied);
    }

    [Fact]
    public void Recommend_SkipsHugeBufferChannel()
    {
        var interfaces = new[]
        {
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, outLen: 19),
            If("VID_24AE&PID_1412&MI_02", inLen: 513, outLen: 513, usage: 0x0001),
        };

        var recommended = CaptureDeviceCatalog.Recommend(interfaces);

        Assert.NotNull(recommended);
        Assert.Equal("VID_24AE&PID_1412&MI_01&Col09", recommended!.InterfaceKey);
    }

    [Fact]
    public void Recommend_PrefersClaimedInterfaceOverHeuristic()
    {
        var devices = new[]
        {
            If("VID_24AE&PID_1412&MI_01&Col05", inLen: 33, outLen: 33, usage: 0x000E),
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, outLen: 19),
        };
        var claimedPath = devices[1].InterfacePath;

        var group = Assert.Single(CaptureDeviceCatalog.Group(devices,
            path => string.Equals(path, claimedPath, StringComparison.OrdinalIgnoreCase) ? "Rapoo" : null));

        Assert.True(group.IsAdapted);
        Assert.Equal("Rapoo", group.ClaimedBy);
        Assert.Equal("VID_24AE&PID_1412&MI_01&Col09", group.Recommended!.InterfaceKey);
    }

    [Fact]
    public void ForTask_Unknown_ListsAll_AndPutsUnadaptedFirst()
    {
        var adapted = new CaptureDeviceGroup
        {
            GroupKey = "a", VendorId = 1, ProductId = 1, Product = "A Device",
            Interfaces = [If("VID_0001&PID_0001&Col01", inLen: 8, vid: 1, pid: 1)],
            ClaimedBy = "SomePlugin", ClaimedPath = null,
        };
        var unknown = new CaptureDeviceGroup
        {
            GroupKey = "b", VendorId = 2, ProductId = 2, Product = "B Device",
            Interfaces = [If("VID_0002&PID_0002&Col01", inLen: 8, vid: 2, pid: 2)],
        };

        var result = CaptureDeviceCatalog.ForTask([adapted, unknown], CaptureTaskKind.UnknownDeviceInvestigation);

        Assert.Equal(2, result.Count);
        Assert.False(result[0].IsAdapted);
        Assert.True(result[1].IsAdapted);
    }

    [Fact]
    public void ForTask_Supported_OnlyClaimedDevices()
    {
        var adapted = new CaptureDeviceGroup
        {
            GroupKey = "a", VendorId = 1, ProductId = 1, Product = "A Device",
            Interfaces = [If("VID_0001&PID_0001&Col01", inLen: 8, vid: 1, pid: 1)],
            ClaimedBy = "SomePlugin",
        };
        var unknown = new CaptureDeviceGroup
        {
            GroupKey = "b", VendorId = 2, ProductId = 2, Product = "B Device",
            Interfaces = [If("VID_0002&PID_0002&Col01", inLen: 8, vid: 2, pid: 2)],
        };

        var supported = CaptureDeviceCatalog.ForTask([adapted, unknown], CaptureTaskKind.SupportedDeviceDiagnosis);
        Assert.Single(supported);
        Assert.Equal("A Device", supported[0].Title);

        var none = CaptureDeviceCatalog.ForTask([unknown], CaptureTaskKind.SupportedDeviceDiagnosis);
        Assert.Empty(none);
    }

    [Fact]
    public void DescribeInterface_TellsWhetherItCanProduceData()
    {
        var denied = CaptureDeviceCatalog.DescribeInterface(If("VID_0001&PID_0001&Col01", denied: true));
        Assert.Contains("打不开", denied);
        Assert.Contains("Win32 5", denied);

        var input = CaptureDeviceCatalog.DescribeInterface(If("VID_0001&PID_0001&Col09", inLen: 19));
        Assert.Contains("有输入通道", input);
        Assert.Contains("0x07", input);

        var featureOnly = CaptureDeviceCatalog.DescribeInterface(If("VID_0001&PID_0001&Col06", featLen: 33));
        Assert.Contains("仅 Feature 通道", featureOnly);
        // 实测：雷柏 Col06 的 feature 0x08 全程返回同一帧，选接口时就得让人知道可能没有状态数据
        Assert.Contains("配置通道", featureOnly);

        var dead = CaptureDeviceCatalog.DescribeInterface(If("VID_0001&PID_0001&Col03"));
        Assert.Contains("采不到东西", dead);

        var huge = CaptureDeviceCatalog.DescribeInterface(If("VID_0001&PID_0001&MI_02", inLen: 513));
        Assert.Contains("大缓冲通道", huge);
    }

    [Fact]
    public void Recommend_ReturnsNull_WhenDeviceOnlyExposesStandardPages()
    {
        // 弱水时砂 38FF:0001 的真实形态：只有 MI_03 消费类接口（in=3），没有 0xFF00 厂商集合
        var interfaces = new[]
        {
            If("VID_38FF&PID_0001&MI_03", inLen: 3, usagePage: 0x000C, usage: 0x0001, vid: 0x38FF, pid: 0x0001),
        };

        Assert.Null(CaptureDeviceCatalog.Recommend(interfaces));
    }

    [Fact]
    public void DescribeInterface_SaysStandardPageHasNoVendorProtocol()
    {
        var consumer = CaptureDeviceCatalog.DescribeInterface(
            If("VID_38FF&PID_0001&MI_03", inLen: 3, usagePage: 0x000C, usage: 0x0001));
        Assert.Contains("标准用途页", consumer);
        Assert.Contains("不会有厂商协议", consumer);

        var vendor = CaptureDeviceCatalog.DescribeInterface(If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19));
        Assert.DoesNotContain("标准用途页", vendor);
    }

    [Fact]
    public void IsInputPeripheral_DetectsMouseAndKeyboard()
    {
        // 迈从/雷柏这类 dongle：同一台设备既有厂商页，也有被系统保护的鼠标集合
        var dongle = new[]
        {
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, outLen: 19),
            If("VID_24AE&PID_1412&MI_01&Col01", inLen: 7, usagePage: 0x0001, usage: 0x02, denied: true),
        };
        var group = Assert.Single(CaptureDeviceCatalog.Group(dongle));
        Assert.True(group.IsInputPeripheral);

        var pureVendor = new[]
        {
            If("VID_24AE&PID_1412&MI_01&Col09", inLen: 19, outLen: 19),
        };
        Assert.False(Assert.Single(CaptureDeviceCatalog.Group(pureVendor)).IsInputPeripheral);
    }

    [Fact]
    public void ShortInterface_TakesLastSegment()
    {
        Assert.Equal("Col09", CaptureDeviceCatalog.ShortInterface("VID_24AE&PID_1412&MI_01&Col09"));
        Assert.Equal("MI_02", CaptureDeviceCatalog.ShortInterface("VID_24AE&PID_1412&MI_02"));
        Assert.Equal("bare", CaptureDeviceCatalog.ShortInterface("bare"));
    }

    [Fact]
    public void TaskKind_WireAndLabelRoundTrip()
    {
        foreach (var task in Enum.GetValues<CaptureTaskKind>())
        {
            Assert.Equal(task, CaptureNames.ParseTask(task.ToWire()));
            Assert.False(string.IsNullOrWhiteSpace(task.Label()));
        }
        Assert.Equal("supported-diagnosis", CaptureTaskKind.SupportedDeviceDiagnosis.ToWire());
    }
}
