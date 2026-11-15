using System.Runtime.InteropServices;
using VigilWatt.Core.Hid;

namespace VigilWatt.Core.Capture.Hid;
/// <summary>
/// 功 能： 采集用的 HID 接口枚举器。**打不开的接口也产出条目**（标 AccessDenied），不静默丢弃。
/// 2026/10/24 12:40:43 20211
/// 不复用 HidDeviceScanner.Scan()：它打不开就 return null 且吞异常、还带缓存，采集侧看不到失败原因。
/// </summary>
public static class HidCaptureEnumerator
{
    public static List<CaptureDeviceInfo> Enumerate()
    {
        var result = new List<CaptureDeviceInfo>();
        // ContainerId 一次建表：同一台物理设备的全部接口共用一个值，靠它把 MI_00/MI_01/MI_02 收成一台
        var containers = HidContainerMap.Build();
        foreach (var path in HidDeviceScanner.EnumeratePaths())
        {
            try
            {
                Guid? container = containers.TryGetValue(path, out var id) ? id : null;
                result.Add(Inspect(path, container));
            }
            catch (Exception ex)
            {
                Utils.Log.Debug($"采集枚举跳过 {path}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>从设备路径里取接口段（`hid#vid_x&amp;pid_y&amp;mi_01&amp;col06#...` → `vid_x&amp;pid_y&amp;mi_01&amp;col06`）。</summary>
    public static string ParseInterfaceKey(string path)
    {
        var parts = path.Split('#');
        return parts.Length > 1 ? parts[1] : path;
    }

    /// <summary>从接口段里取 VID/PID；取不到返回 0（调用方应优先用 HidD_GetAttributes 的结果）。</summary>
    public static (ushort Vid, ushort Pid) ParseVidPid(string interfaceKey)
    {
        return (ParseHex(interfaceKey, "vid_"), ParseHex(interfaceKey, "pid_"));
    }

    private static ushort ParseHex(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return 0;
        var start = index + marker.Length;
        var end = start;
        while (end < text.Length && Uri.IsHexDigit(text[end])) end++;
        return end - start == 4 && ushort.TryParse(text.AsSpan(start, 4),
            System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? value
            : (ushort)0;
    }

    private static CaptureDeviceInfo Inspect(string path, Guid? containerId)
    {
        var interfaceKey = ParseInterfaceKey(path);
        var (pathVid, pathPid) = ParseVidPid(interfaceKey);

        // 只申请读权限：从访问权限层面保证采集不可能写设备。
        var handle = NativeHid.CreateFileW(path, NativeHid.GENERIC_READ,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, IntPtr.Zero,
            NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

        using var safe = handle;
        if (safe.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return new CaptureDeviceInfo
            {
                DeviceId = MakeDeviceId(pathVid, pathPid, interfaceKey),
                VendorId = pathVid,
                ProductId = pathPid,
                InterfacePath = path,
                InterfaceKey = interfaceKey,
                GroupKey = DeviceGroupKey.For(containerId, pathVid, pathPid),
                AccessDenied = error == NativeHid.ERROR_ACCESS_DENIED,
                OpenError = error,
            };
        }

        var attrs = new NativeHid.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<NativeHid.HIDD_ATTRIBUTES>() };
        var hasAttrs = NativeHid.HidD_GetAttributes(safe, ref attrs);
        var vid = hasAttrs ? attrs.VendorID : pathVid;
        var pid = hasAttrs ? attrs.ProductID : pathPid;

        ushort usagePage = 0, usage = 0, inLen = 0, outLen = 0, featLen = 0;
        var capsAvailable = false;
        IReadOnlyList<byte> inputIds = [], outputIds = [], featureIds = [];
        var fields = new List<HidFieldInfo>();
        if (NativeHid.HidD_GetPreparsedData(safe, out var preparsed))
        {
            try
            {
                var caps = new NativeHid.HIDP_CAPS();
                if (NativeHid.HidP_GetCaps(preparsed, ref caps) == NativeHid.HIDP_STATUS_SUCCESS)
                {
                    capsAvailable = true;
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                    featLen = caps.FeatureReportByteLength;
                }
                inputIds = NativeHid.ReadReportIds(preparsed, NativeHid.HIDP_REPORT_TYPE.HidP_Input);
                outputIds = NativeHid.ReadReportIds(preparsed, NativeHid.HIDP_REPORT_TYPE.HidP_Output);
                featureIds = NativeHid.ReadReportIds(preparsed, NativeHid.HIDP_REPORT_TYPE.HidP_Feature);
                ReadFields(preparsed, NativeHid.HIDP_REPORT_TYPE.HidP_Input, "input", fields);
                ReadFields(preparsed, NativeHid.HIDP_REPORT_TYPE.HidP_Output, "output", fields);
                ReadFields(preparsed, NativeHid.HIDP_REPORT_TYPE.HidP_Feature, "feature", fields);
            }
            finally
            {
                NativeHid.HidD_FreePreparsedData(preparsed);
            }
        }

        return new CaptureDeviceInfo
        {
            DeviceId = MakeDeviceId(vid, pid, interfaceKey),
            VendorId = vid,
            ProductId = pid,
            VersionNumber = hasAttrs ? attrs.VersionNumber : (ushort)0,
            Product = ReadString(safe, NativeHid.HidD_GetProductString) ?? "",
            Manufacturer = ReadString(safe, NativeHid.HidD_GetManufacturerString) ?? "",
            SerialNumber = ReadString(safe, NativeHid.HidD_GetSerialNumberString),
            InterfacePath = path,
            InterfaceKey = interfaceKey,
            GroupKey = DeviceGroupKey.For(containerId, vid, pid),
            UsagePage = usagePage,
            Usage = usage,
            InputReportLength = inLen,
            OutputReportLength = outLen,
            FeatureReportLength = featLen,
            CapsAvailable = capsAvailable,
            InputReportIds = inputIds,
            OutputReportIds = outputIds,
            FeatureReportIds = featureIds,
            Fields = fields,
        };
    }

    /// <summary>把 caps 明细搬进纯模型。字段顺序就是描述符里的顺序，分析时能对上偏移。</summary>
    private static void ReadFields(IntPtr preparsed, NativeHid.HIDP_REPORT_TYPE type, string name, List<HidFieldInfo> target)
    {
        foreach (var raw in NativeHid.ReadFields(preparsed, type))
        {
            target.Add(new HidFieldInfo
            {
                ReportType = name,
                // caps 里 0 = 该接口不使用 report ID，写成 null 才和 InputReportIds 为空一致
                ReportId = raw.ReportId == 0 ? null : raw.ReportId,
                UsagePage = raw.UsagePage,
                UsageMin = raw.UsageMin,
                UsageMax = raw.UsageMax,
                BitSize = raw.BitSize,
                ReportCount = raw.ReportCount,
                LogicalMin = raw.LogicalMin,
                LogicalMax = raw.LogicalMax,
                IsAbsolute = raw.IsAbsolute,
                IsButton = raw.IsButton,
            });
        }
    }

    /// <summary>设备不提供序列号时返回 null（空串会让人以为读到了空值）。</summary>
    private static string? ReadString(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        Func<Microsoft.Win32.SafeHandles.SafeFileHandle, char[], uint, bool> api)
    {
        var buffer = new char[127];
        if (!api(handle, buffer, (uint)(buffer.Length * 2))) return null;
        var text = new string(buffer).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string MakeDeviceId(ushort vid, ushort pid, string interfaceKey)
        => $"cap:{vid:X4}:{pid:X4}:{interfaceKey}";
}
