using System.Runtime.InteropServices;
using VigilWatt.Core.Hid;

namespace TaskBarWidgets.PeripheralHost;

internal static class Metadata
{
    // Only documented device-body PIDs and the direct Bluetooth controller
    // protocol qualify. A single-target receiver is still a receiver container.
    public static bool IsDirectTarget(HidDeviceInfo info, string plugin) => plugin switch
    {
        "Razer" => info.VendorId == 0x1532 && info.ProductId is 0x00E2 or 0x00E5 or 0x00DF,
        "Vgn" => info.VendorId == 0x3554 && info.ProductId == 0xFB57
            || info.VendorId == 0x391D && info.ProductId == 0x1026,
        "Atk" => info.VendorId == 0x373B && info.ProductId is 0x1015 or 0x12A9 or 0x11E4 or 0x1293 or 0x10C7,
        "DualSense" => true, // Registry admits only the audited Bluetooth input channel.
        _ => false,
    };

    public static string CleanText(string? value, int limit) =>
        new string((value ?? "").Where(c => !char.IsControl(c)).Take(limit).ToArray()).Trim();

    public static string TrustworthySerial(string? value)
    {
        string serial = CleanText(value, 256);
        string key = new(serial.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (key.Length < 4 || key.Distinct().Count() < 2
            || key is "UNKNOWN" or "NONE" or "NULL" or "NA" or "NOTAVAILABLE" or "DEFAULTSTRING"
                or "SERIAL" or "SERIALNUMBER" or "123456" or "12345678" or "123456789" or "1234567890"
                or "0123456789" or "ABCDEF" or "TOBEFILLEDBYOEM") return "";
        return serial;
    }

    // Resolve the exact interface to a devnode through SetupAPI, then read the
    // container through Configuration Manager. Never infer a container from a path.
    public static string ReadContainer(string path) => ReadNode(path, node =>
    {
        var key = NativeHid.DevPropKeyContainerId;
        var bytes = new byte[16];
        uint length = 16;
        if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, bytes, ref length, 0) != 0
            || type != NativeHid.DEVPROP_TYPE_GUID || length != 16) return "";
        var container = new Guid(bytes);
        return container == Guid.Empty ? "" : container.ToString("D");
    });

    // Only the caller's proven direct USB targets use this. UNIQUEID on the USB
    // devnode is required: an arbitrary HID product-string serial is insufficient.
    public static string ReadTargetSerial(HidDeviceInfo info) => ReadNode(info.DevicePath, node =>
    {
        for (int depth = 0; depth < 6; depth++)
        {
            var buffer = new char[1024];
            if (CM_Get_Device_IDW(node, buffer, (uint)buffer.Length, 0) != 0) return "";
            string id = new string(buffer).TrimEnd('\0');
            var parts = id.Split('\\', 3);
            if (parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals($"VID_{info.VendorId:X4}&PID_{info.ProductId:X4}", StringComparison.OrdinalIgnoreCase))
            {
                var key = new NativeHid.DEVPROPKEY
                    { FmtId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 17 };
                var bytes = new byte[4];
                uint length = 4;
                if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, bytes, ref length, 0) == 0
                    && type == 7 && length == 4 && (BitConverter.ToUInt32(bytes) & 0x10) != 0)
                    return TrustworthySerial(parts[2]);
                return "";
            }
            if (CM_Get_Parent(out uint parent, node, 0) != 0) return "";
            node = parent;
        }
        return "";
    });

    private static string ReadNode(string path, Func<uint, string> read)
    {
        var guid = NativeHid.GuidDeviceInterfaceHid;
        IntPtr set = NativeHid.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero,
            NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return "";
        try
        {
            var data = new NativeHid.SP_DEVICE_INTERFACE_DATA
                { CbSize = (uint)Marshal.SizeOf<NativeHid.SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiOpenDeviceInterfaceW(set, path, 0, ref data)) return "";
            var node = new NativeHid.SP_DEVINFO_DATA
                { CbSize = (uint)Marshal.SizeOf<NativeHid.SP_DEVINFO_DATA>() };
            NativeHid.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint needed, ref node);
            if (needed < 8 || needed > 32768) return "";
            IntPtr detail = Marshal.AllocHGlobal((int)needed);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!NativeHid.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, needed, out _, ref node)) return "";
                return read(node.DevInst);
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        finally { NativeHid.SetupDiDestroyDeviceInfoList(set); }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInterfaceW(IntPtr set, string path, uint flags,
        ref NativeHid.SP_DEVICE_INTERFACE_DATA data);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref NativeHid.DEVPROPKEY key,
        out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint node, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
}
