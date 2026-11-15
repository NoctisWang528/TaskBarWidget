using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VigilWatt.Core.Hid;

/// <summary>
/// 功 能： Win32 原生声明：CfgMgr32 枚举 HID 接口 + kernel32/hid.dll 设备 I/O。
/// 2026/10/06 15:53:36 xliu
/// 不用 WinRT HumanInterfaceDevice（系统级 collection 被排除、私有协议访问受限）。
/// </summary>
internal static class NativeHid
{
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_DEVICE_IN_USE = 2404; // Win32 error code for exclusive HID lock

    public static readonly Guid GuidDeviceInterfaceHid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;
    public const uint DEVPROP_TYPE_GUID = 0x0000000D;

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    /// <summary>DEVPKEY_Device_ContainerId：同一台物理设备的全部接口共用一个 GUID（见 docs/project/capture.md）。</summary>
    public static readonly DEVPROPKEY DevPropKeyContainerId = new()
    {
        FmtId = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        Pid = 2,
    };

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(
        out uint size, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(
        ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint size, uint flags);

    // ContainerId 走 SetupAPI 而不是 CM_Get_Device_Interface_PropertyW：后者在本机对 HID 接口路径一律
    // 返回 CR_INVALID_FLAG(4)，SetupAPI 这条路实测可用（2026-10-24）。
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr devInfoSet, IntPtr devInfo, ref Guid interfaceClassGuid, uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr devInfoSet, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize,
        out uint requiredSize, ref SP_DEVINFO_DATA devInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDevicePropertyW(
        IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfoData, ref DEVPROPKEY key, out uint propertyType,
        [Out] byte[] buffer, uint bufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll")]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(
        SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

    /// <summary>HidP_* 的成功返回码（NTSTATUS，注意不是 0）。</summary>
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    // ---------------------------------------------------------------- report ID 读取
    // 用户态**没有**拿原始报告描述符的 API（hid.dll 不导出 HidP_GetReportDescriptor，实测 AttributeError），
    // 故改用 caps 里的 ReportID 字段——HID 规范里 report ID 的唯一权威来源（HidP_GetCaps/GetButtonCaps 本机实测可用）。

    public enum HIDP_REPORT_TYPE { HidP_Input = 0, HidP_Output = 1, HidP_Feature = 2 }

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetButtonCaps(
        HIDP_REPORT_TYPE reportType, IntPtr buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetValueCaps(
        HIDP_REPORT_TYPE reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    /// <summary>HIDP_BUTTON_CAPS（hidpi.h，pshpack4，共 72 字节）。只用到前 3 个字段，其余按原布局占位。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public uint Reserved1, Reserved2, Reserved3, Reserved4, Reserved5;
        public uint Reserved6, Reserved7, Reserved8, Reserved9, Reserved10;
        public ushort UsageMin, UsageMax, StringMin, StringMax;
        public ushort DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    /// <summary>HIDP_VALUE_CAPS（hidpi.h，pshpack4，共 72 字节）。同样只取 ReportID。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2A, Reserved2B, Reserved2C, Reserved2D, Reserved2E;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, StringMin, StringMax;
        public ushort DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    /// <summary>取某报文类型的 report ID：caps 里第一个非 0 的 ReportID 即该接口的 ID；caps 全为 0 → 不使用 report ID；无 caps → null。</summary>
    public static byte? ReadReportId(IntPtr preparsedData, HIDP_REPORT_TYPE reportType)
    {
        const int max = 64;
        int btnSize = Marshal.SizeOf<HIDP_BUTTON_CAPS>();
        int valSize = Marshal.SizeOf<HIDP_VALUE_CAPS>();

        IntPtr btnBuf = Marshal.AllocHGlobal(btnSize * max);
        IntPtr valBuf = Marshal.AllocHGlobal(valSize * max);
        try
        {
            bool any = false;

            ushort n = max;
            if (HidP_GetButtonCaps(reportType, btnBuf, ref n, preparsedData) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    var c = Marshal.PtrToStructure<HIDP_BUTTON_CAPS>(btnBuf + i * btnSize);
                    any = true;
                    if (c.ReportID != 0) return c.ReportID;
                }
            }

            n = max;
            if (HidP_GetValueCaps(reportType, valBuf, ref n, preparsedData) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    var c = Marshal.PtrToStructure<HIDP_VALUE_CAPS>(valBuf + i * valSize);
                    any = true;
                    if (c.ReportID != 0) return c.ReportID;
                }
            }

            return any ? (byte)0 : null;
        }
        finally
        {
            Marshal.FreeHGlobal(btnBuf);
            Marshal.FreeHGlobal(valBuf);
        }
    }

    /// <summary>取某报文类型**全部**出现过的 report ID（去重升序）。采集页只能从这些 ID 里选，禁止枚举猜。</summary>
    public static IReadOnlyList<byte> ReadReportIds(IntPtr preparsedData, HIDP_REPORT_TYPE reportType)
    {
        const int max = 64;
        var ids = new SortedSet<byte>();
        int btnSize = Marshal.SizeOf<HIDP_BUTTON_CAPS>();
        int valSize = Marshal.SizeOf<HIDP_VALUE_CAPS>();

        IntPtr btnBuf = Marshal.AllocHGlobal(btnSize * max);
        IntPtr valBuf = Marshal.AllocHGlobal(valSize * max);
        try
        {
            ushort n = max;
            if (HidP_GetButtonCaps(reportType, btnBuf, ref n, preparsedData) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    var id = Marshal.PtrToStructure<HIDP_BUTTON_CAPS>(btnBuf + i * btnSize).ReportID;
                    if (id != 0) ids.Add(id);
                }
            }

            n = max;
            if (HidP_GetValueCaps(reportType, valBuf, ref n, preparsedData) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    // caps 里的 0 = 该接口不使用 report ID（与 ReadReportId 同一条约定），不是 ID 0。
                    // 混进列表会让采集把报文首字节当 ID，按首字节把同一路报文拆成多个分组。
                    var id = Marshal.PtrToStructure<HIDP_VALUE_CAPS>(valBuf + i * valSize).ReportID;
                    if (id != 0) ids.Add(id);
                }
            }

            return ids.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(btnBuf);
            Marshal.FreeHGlobal(valBuf);
        }
    }

    /// <summary>caps 里的一个字段结构（value caps 或 button caps）。只做数据搬运，判断在采集层。</summary>
    public readonly record struct HidFieldRaw(
        HIDP_REPORT_TYPE ReportType, byte ReportId, ushort UsagePage, ushort UsageMin, ushort UsageMax,
        ushort BitSize, ushort ReportCount, int LogicalMin, int LogicalMax, bool IsAbsolute, bool IsButton);

    /// <summary>读某报文类型的全部字段结构（value + button caps）。只读 caps，不发任何报文。</summary>
    public static IReadOnlyList<HidFieldRaw> ReadFields(IntPtr preparsedData, HIDP_REPORT_TYPE reportType)
    {
        const int max = 64;
        var list = new List<HidFieldRaw>();
        int btnSize = Marshal.SizeOf<HIDP_BUTTON_CAPS>();
        int valSize = Marshal.SizeOf<HIDP_VALUE_CAPS>();

        IntPtr btnBuf = Marshal.AllocHGlobal(btnSize * max);
        IntPtr valBuf = Marshal.AllocHGlobal(valSize * max);
        try
        {
            ushort n = max;
            if (HidP_GetValueCaps(reportType, valBuf, ref n, preparsedData) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    var c = Marshal.PtrToStructure<HIDP_VALUE_CAPS>(valBuf + i * valSize);
                    // IsRange=0 时 UsageMin 这个位置其实是单个 Usage（联合体）
                    ushort min = c.UsageMin;
                    ushort maxUsage = c.IsRange != 0 ? c.UsageMax : min;
                    list.Add(new HidFieldRaw(reportType, c.ReportID, c.UsagePage, min, maxUsage,
                        c.BitSize, c.ReportCount, c.LogicalMin, c.LogicalMax, c.IsAbsolute != 0, false));
                }
            }

            n = max;
            if (HidP_GetButtonCaps(reportType, btnBuf, ref n, preparsedData) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    var c = Marshal.PtrToStructure<HIDP_BUTTON_CAPS>(btnBuf + i * btnSize);
                    ushort min = c.UsageMin;
                    ushort maxUsage = c.IsRange != 0 ? c.UsageMax : min;
                    var count = maxUsage >= min ? maxUsage - min + 1 : 1;
                    list.Add(new HidFieldRaw(reportType, c.ReportID, c.UsagePage, min, maxUsage,
                        1, (ushort)count, 0, 1, true, true));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(btnBuf);
            Marshal.FreeHGlobal(valBuf);
        }
        return list;
    }

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle handle, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetInputReport(SafeFileHandle handle, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] reportBuffer, uint reportBufferLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    /// <summary>
    /// HIDP_CAPS —— **字段顺序必须与 Windows SDK 的 hidpi.h 一致：Usage 在前，UsagePage 在后**。
    /// 早期版本把 UsagePage 写在前（错的），扫描日志里 usage_page/usage 会颠倒（如 Consumer Control 打成 `usage=0x0001/0x000C`），排查协议时被误导过。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }
}
