using System.Runtime.InteropServices;
using VigilWatt.Core.Hid;

namespace VigilWatt.Core.Capture.Hid;
/// <summary>
/// 功 能： 读每个 HID 接口的 ContainerId（DEVPKEY_Device_ContainerId），供采集页按物理设备分组。
/// 2026/10/24 20:05:00 20211
/// 一次 SetupAPI 枚举建成 map；某条读不到就不进 map，调用方退回 VID/PID 分组（见 DeviceGroupKey）。
/// </summary>
internal static class HidContainerMap
{
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>接口路径 → ContainerId。键不区分大小写：SetupAPI 与 CfgMgr32 返回的路径大小写不一致。</summary>
    public static Dictionary<string, Guid> Build()
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var hid = NativeHid.GuidDeviceInterfaceHid;
        var set = NativeHid.SetupDiGetClassDevsW(ref hid, null, IntPtr.Zero,
            NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_DEVICEINTERFACE);
        if (set == InvalidHandle) return map;

        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new NativeHid.SP_DEVICE_INTERFACE_DATA
                {
                    CbSize = (uint)Marshal.SizeOf<NativeHid.SP_DEVICE_INTERFACE_DATA>(),
                };
                if (!NativeHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, index, ref iface)) break;

                var (path, container) = ReadOne(set, ref iface);
                if (path != null && container is { } id) map[path] = id;
            }
        }
        catch (Exception ex)
        {
            // 读不到就整体退回 VID/PID 分组，不能因为一台设备读属性失败就把枚举搞崩
            Utils.Log.Debug($"读取 HID ContainerId 失败: {ex.Message}");
        }
        finally
        {
            NativeHid.SetupDiDestroyDeviceInfoList(set);
        }
        return map;
    }

    private static (string? Path, Guid? Container) ReadOne(IntPtr set, ref NativeHid.SP_DEVICE_INTERFACE_DATA iface)
    {
        // 先问长度，再分配：SP_DEVICE_INTERFACE_DETAIL_DATA_W 是变长结构
        var probe = NewDevInfo();
        NativeHid.SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out var required, ref probe);
        if (required == 0) return (null, null);

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize 在 x64 是 8（4 字节 cbSize + 4 字节对齐），x86 是 6 —— 不是结构体总长度
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            var info = NewDevInfo();
            if (!NativeHid.SetupDiGetDeviceInterfaceDetailW(set, ref iface, buffer, required, out _, ref info))
                return (null, null);

            var path = Marshal.PtrToStringUni(buffer + 4);
            return string.IsNullOrEmpty(path) ? (null, null) : (path, ReadContainer(set, ref info));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Guid? ReadContainer(IntPtr set, ref NativeHid.SP_DEVINFO_DATA info)
    {
        var key = NativeHid.DevPropKeyContainerId;
        var value = new byte[16];
        if (!NativeHid.SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, value, 16, out _, 0))
            return null;
        return type == NativeHid.DEVPROP_TYPE_GUID ? new Guid(value) : null;
    }

    private static NativeHid.SP_DEVINFO_DATA NewDevInfo()
        => new() { CbSize = (uint)Marshal.SizeOf<NativeHid.SP_DEVINFO_DATA>() };
}
