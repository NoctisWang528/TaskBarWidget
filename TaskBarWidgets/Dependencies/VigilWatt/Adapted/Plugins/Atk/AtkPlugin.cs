// Adapted from VigilWatt (MIT). Copyright (c) 2026 evren; see upstream/LICENSE.
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Atk;

/// <summary>
/// 功 能： ATK/VXE 无线鼠标电量插件——主动查询 FF02 口的 17 字节命令-应答协议。
/// 2026/10/20 22:14:00 Yang
/// 2026/10/26 22:40:03 ranxue：接入 A9 Air 大师版+（接收器 0x1278 / 本体 0x1293），双连接改留本体
/// 帧结构与实测依据见 docs/hardware/atk.md（§2.2 §3.3 §4）；编解码在 AtkCodec。
/// </summary>
public sealed class AtkPlugin : BatteryPluginBase
{
    public override string Name => "Atk";                 // AppSettings.PluginEnabled 的 key
    public override string DisplayName => "ATK";
    public override string DisplayNameEn => "ATK";
    /// <summary>一套开关管鼠标与键盘两条链路：具体图标由 <see cref="ResolveKind"/> 按链路给。</summary>
    public override DeviceKind Kind => DeviceKind.Unknown;

    protected override DeviceKind ResolveKind(string protocol)
        => protocol == ProtocolVia ? DeviceKind.Keyboard : DeviceKind.Mouse;

    // 参与 15 秒发现节拍：基类的"熔断期半开探活"（NudgeHalfOpen）挂在这条节拍上，
    // 鼠标睡醒后最多 15~30 秒被发现，而不是干等一整轮 60 秒主轮询。
    // 前提（接口约定）：ProbeCooldown 必须明显小于 15 秒 —— 这里是 10 秒，满足；
    // 已认领的设备 Attach 直接返回、零写入，所以这条节拍不会抬高实际写入频率。
    // 注：原注释按社区指南"主动通信不进 15 秒节拍"保持 false，2026-10-27 因唤醒感知需求改为 true，
    // 写入仍由 ProbeCooldown 兜住，实测探活最坏 ≈ 1 帧/30 秒。
    public override bool SupportsDiscoveryProbe => true;

    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(10);

    // 熔断期半开探活（基类 NudgeHalfOpen）：鼠标休眠是常态，每睡一次就顶高一档退避，
    // 用户真去用时正好撞在 4~5 分钟的退避里，看到的就是"电量卡死、要手动刷新"
    // （2026-10-27 实机日志：熔断第 4 次 240 秒 → 4 分半后才恢复）。30 秒 = 每轮主轮询都允许一帧探活，
    // 失败不记账、不升档；设备一应答就由 ReportPollAnswered 清零档位。
    protected override TimeSpan BreakerProbeInterval => TimeSpan.FromSeconds(30);

    // null 与"被熔断挡住"不可区分 → 基类不自动记账，由 PollDevice 自报账
    protected override OfflinePolicy Offline => new() { Silence = TimeSpan.Zero, CountPollFailures = false };

    private const ushort VendorId = 0x373B;               // ⚠ 实测修正：非 0x375B
    private const ushort DonglePid = 0x101B;              // 8K dongle（产品名 "ATK Mouse 8K Dongle"）
    private const ushort DonglePid1K = 0x101A;            // 1K dongle
    private const ushort DonglePidV3 = 0x1303;            // 8K dongle（产品名 "Wireless mouse 8k dongle-M"）
    private const ushort DonglePidF1Ultimate = 0x11D9;    // F1 Ultimate 2.0 接收器（产品名 "Wireless mouse 8k dongle-L"）
    private const ushort DonglePidA9Air = 0x1278;         // A9 Air 大师版+ GEM 8K 接收器（产品名同为 "...8k dongle-L"）
    private const ushort DonglePidR1Nk = 0x10C9;          // VXE R1 Nearlink 接收器（产品名 "NK mouse NANO dongle"）

    /// <summary>A9 Air 大师版+ 的**本体** PID：插数据线时枚举出来（产品名 "ATK A9 Air+"）。
    /// 与 F1 Ultimate 的 0x11E4 同类——**不是接收器**，但插线时只有它应答（atk.md §3.3）。</summary>
    private const ushort BodyPidA9Air = 0x1293;

    /// <summary>F1 Ultimate 2.0 的**本体** PID：插充电线时枚举出来（产品名 "ATK F1 ULTIMATE 2.0"）。
    /// 它**不是接收器**——2026-10-27 实测纠正：曾按"同型号另一只"登记进 <see cref="IsReceiverPid"/>，
    /// 结果插着充电线时 dongle 与本体各认领一条、ID 又不同，设备页出现 `ATK F1 Ultimate 2.0` 与
    /// `ATK F1 Ultimate 2.0 (#2)` 两个条目。本宿主按独立端点保留两者。</summary>
    private const ushort BodyPidF1Ultimate = 0x11E4;

    /// <summary>VXE R1 Nearlink 的**本体** PID：插线时枚举；FF02 上有 20B 干扰集合，故走严格判据（atk.md §9）。</summary>
    private const ushort BodyPidR1Nk = 0x10C7;

    /// <summary>电量口的 usage page；必须精确匹配，否则会对 FF03/FF04/FF05/FF06 等厂商口各白发一帧查询。</summary>
    private const ushort UsagePageBattery = 0xFF02;

    /// <summary>已知的接收器 PID：都认同一条查询帧、响应结构一致。11D9/11E4 漏登记过一次，
    /// 结果它们走"本体"分支又把自己算成"接收器在位"，整只鼠标不认领（atk.md §8）。
    /// 11E4 虽然是本体，也必须留在这里——插着充电线时 dongle 收不到无线包、读不到电量，
    /// 若让本体"让位给在位的接收器"，会变成 dongle 认不上 + 本体不许认，整只鼠标消失（atk.md §4.3）。
    /// A9 Air 的**本体 0x1293 是例外，故意不列**：它的产品名 `ATK A9 Air+` 是唯一准确的机型来源，
    /// 留在外面才能被 <see cref="BuildDisplayName"/> 学到；它的"可独立认领"由 IsCandidate 的
    /// 已登记分组分支保证（atk.md §4.1）。</summary>
    private static bool IsReceiverPid(ushort pid) =>
        pid is DonglePid or DonglePid1K or DonglePidV3 or DonglePidF1Ultimate or BodyPidF1Ultimate
            or DonglePidA9Air or DonglePidR1Nk;

    /// <summary>鼠标链路（op 帧）与键盘链路（VIA）各一套协议名。</summary>
    private const string ProtocolMouse = "atk-v1";
    private const string ProtocolVia = "atk-keyboard-via";

    /// <summary>仅按本接口指纹筛选；独立端点不因其他接收器在位而被排除。</summary>
    protected override bool IsCandidate(HidDeviceInfo info)
    {
        // 键盘链路（VIA，page 0xFF60）：与鼠标同 VID，靠 usage page 分流，必须先判。
        if (AtkViaCodec.IsSupportedInterface(info.VendorId, info.UsagePage,
                info.InputReportLength, info.OutputReportLength, info.OutputReportId))
            return true;

        if (info.VendorId != VendorId || info.UsagePage != UsagePageBattery)
            return false;

        // 长度判据：必须能收发 17 字节电量帧，否则写不进/读不出完整帧。
        // 接收器用严格判据（in=17 / out=17，0x101B/0x101A/0x1303/0x11D9 上游实测、0x1278 作者实测均一致）；
        // 本体大多只要求"容得下"，但 R1 Nearlink 的 FF02 有 20B 干扰集合，必须一起走严格判据。
        if (IsReceiverPid(info.ProductId) || info.ProductId == BodyPidR1Nk)
            return AtkCodec.IsSupportedInterface(info.InputReportLength, info.OutputReportLength);

        if (info.InputReportLength < AtkCodec.ReportLength || info.OutputReportLength < AtkCodec.ReportLength)
            return false;

        // A sibling or unrelated receiver is not physical pairing evidence.
        return true;
    }

    /// <summary>已验证型号表（键盘 A98 Pro、F1 ULTIMATE 2.0 与 VXE R1 Nearlink）。</summary>
    private static readonly Dictionary<ushort, string> KnownModelByPid = new()
    {
        [0x1152] = "ATK A98 Pro",
        [DonglePidF1Ultimate] = "ATK F1 Ultimate 2.0",   // 接收器形态
        [BodyPidF1Ultimate] = "ATK F1 Ultimate 2.0",     // 本体形态（插充电线），同一台设备
        [DonglePidR1Nk] = "VXE R1 Nearlink",             // 接收器形态（NK mouse NANO dongle）
        [BodyPidR1Nk] = "VXE R1 Nearlink",               // 本体形态（插数据线），同一台设备
    };

    protected override string BuildDisplayName(HidDeviceInfo info) => NameFor(info);

    internal static string NameFor(HidDeviceInfo info)
    {
        if (KnownModelByPid.TryGetValue(info.ProductId, out var known)) return known;
        return string.IsNullOrWhiteSpace(info.Product) ? $"ATK ({info.VidPid})" : info.Product;
    }

    // ------------------------------------------------------------------ 写读

    /// <summary>写查询帧；WriteOutput 不可用时回落 HidD_SetOutputReport（该设备独占 WriteFile 不可用）。</summary>
    private static bool WriteQuery(HidDeviceHandle handle, byte[] frame)
    {
        if (handle.WriteOutput(frame, TimeSpan.FromSeconds(1), quiet: true))
            return true;
        return NativeSetOutputReport(handle.Info.DevicePath, frame);
    }

    /// <summary>读电量响应：最多约 1.5 秒，过滤掉队列里的普通鼠标报文。</summary>
    private static byte[]? ReadBatteryResponse(HidDeviceHandle handle, TimeSpan window)
    {
        var deadline = DateTimeOffset.UtcNow + window;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(50));
            if (AtkCodec.TryParseReply(input) is not null)
                return input;
        }
        return null;
    }

    // ------------------------------------------------------------------ 基类钩子

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        // 接口预检查：写不进 17 字节 output 的接口直接排除（北通经验：长度从 caps 读）
        // 键盘链路先判：同一接收器 VID 相同，反过来会把 VIA 查询帧发给鼠标。
        if (AtkViaCodec.IsSupportedInterface(handle.Info.VendorId, handle.Info.UsagePage,
                handle.Info.InputReportLength, handle.Info.OutputReportLength, handle.Info.OutputReportId))
        {
            var kb = QueryVia(handle);
            return kb.HasValue
                ? (ProtocolVia, kb.Value.Percent, kb.Value.Charging ? ChargingState.Charging : ChargingState.Discharging)
                : null;
        }

        if (handle.Info.OutputReportLength < AtkCodec.ReportLength) return null;

        if (!WriteQuery(handle, AtkCodec.BuildQuery())) return null;
        if (AtkCodec.TryParseReply(ReadBatteryResponse(handle, TimeSpan.FromSeconds(2))) is not { } r)
            return null;
        return (ProtocolMouse, r.Percent, r.Charging);
    }

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev))
            return null;                          // 未发帧（开关关闭/熔断中），不计失败

        if (dev.Protocol == ProtocolVia)
        {
            var via = QueryVia(dev.Handle);
            if (via.HasValue) ReportPollAnswered(dev);
            else ReportPollFailure(dev);
            return via.HasValue
                ? (via.Value.Percent, via.Value.Charging ? ChargingState.Charging : ChargingState.Discharging)
                : null;
        }

        if (!WriteQuery(dev.Handle, AtkCodec.BuildQuery()))
        {
            // 写失败 = 句柄已失效（设备被拔出，或 Windows 重新枚举了 HID 设备），
            // **不计失败**：基类下一轮扫描会移除旧记录并重新认领。计失败会误触熔断，
            // 让另一台设备假离线 30~60 秒（2026-10-23 实机复现）。
            return null;
        }

        var parsed = AtkCodec.TryParseReply(ReadBatteryResponse(dev.Handle, TimeSpan.FromSeconds(2)));
        if (parsed.HasValue) ReportPollAnswered(dev);   // 应答 → 清零失败计数、提前解除熔断
        else ReportPollFailure(dev);                    // 读得到句柄但不回帧 = 鼠标睡了，该熔断
        return parsed;
    }

    // ------------------------------------------------------------------ P/Invoke 后备：HidD_SetOutputReport

    [System.Runtime.InteropServices.DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr fileHandle, byte[] reportBuffer, uint reportBufferLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string path, uint desiredAccess, int shareMode,
        IntPtr securityAttributes, int creationDisposition, int flags, IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>独占 WriteFile 不可用时的写入后备：重新以 GENERIC_WRITE 打开并走 HidD_SetOutputReport。</summary>
    private static bool NativeSetOutputReport(string devicePath, byte[] report)
    {
        const uint GENERIC_WRITE = 0x40000000;
        const int FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
        IntPtr handle = CreateFileW(devicePath, GENERIC_WRITE, FILE_SHARE_RW,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        try
        {
            return HidD_SetOutputReport(handle, report, (uint)report.Length);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>键盘链路：发一条 VIA 查询，在 1 秒内等电量响应。</summary>
    private static (int Percent, bool Charging)? QueryVia(HidDeviceHandle handle)
    {
        if (!IsWritesEnabled("Atk")) return null;
        if (!handle.WriteOutput(AtkViaCodec.CreateQuery(handle.Info.OutputReportLength),
                TimeSpan.FromMilliseconds(500)))
            return null;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;
            if (!AtkViaCodec.TryParse(input, out int percent, out bool charging))
            {
                if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"ATK 键盘忽略非电量响应: {Log.Hex(input)}");
                continue;
            }
            Log.Info($"ATK 键盘 VIA 电量查询: {percent}% 充电={charging} 原始={Log.Hex(input)}");
            return (percent, charging);
        }
        Log.Once(LogLevel.Debug, $"atk-kb-silent:{handle.Info.DevicePath}",
            $"ATK 键盘查询无响应（多半在睡，恢复应答后会再记一条）: {handle.Info.DevicePath}");
        return null;
    }
}
