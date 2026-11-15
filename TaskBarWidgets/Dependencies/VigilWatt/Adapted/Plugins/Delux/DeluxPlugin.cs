// Adapted from VigilWatt (MIT). Copyright (c) 2026 evren; see upstream/LICENSE.
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Delux;

/// <summary>
/// 功 能： DELUX M900 Pro 2.4G 鼠标插件，只被动读取 5 字节状态帧。
/// 2026/10/19 15:10:00 xliu
/// 帧布局见 docs/hardware/delux.md §1。通道 usage 0x000A/0x0000，与英菲克集合不同。
/// </summary>
public sealed class DeluxPlugin : BatteryPluginBase
{
    public const ushort DeluxVid = 0x1D57;
    public const ushort M900ProPid = 0xFA65;

    public override string Name => "Delux";
    public override string DisplayName => "多彩";
    public override string DisplayNameEn => "DELUX";
    public override DeviceKind Kind => DeviceKind.Mouse;

    /// <summary>纯被动零写入，允许 15 秒发现节拍；鼠标睡前也能在下一拍重试。</summary>
    public override bool SupportsFastDiscovery => true;

    /// <summary>纯被动，不存在写入路径；置 false 作为纵深防御。</summary>
    protected override bool WritesEnabled => false;

    /// <summary>探测只读状态帧，不发任何字节——不受写入开关约束。</summary>
    protected override bool ProbeWrites => false;

    // A validated dock status may know charging without a trustworthy percentage.
    protected override bool NullPercentMeansOffline => false;

    /// <summary>认领后后台只读 5 字节状态帧，电量/充电变化即时上报。</summary>
    protected override bool SupportsPassiveListen => true;

    /// <summary>状态帧约 2 秒一条；90 秒静音兜底。没有查询命令，Poll 返回 null 是形态不是失联。</summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Confirm = TimeSpan.FromSeconds(6),
        Silence = TimeSpan.FromSeconds(90),
        CountPollFailures = false,
    };

    /// <summary>纯被动探测失败只说明鼠标睡着，5 秒后再试；必须小于 15 秒发现节拍。</summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(5);

    /// <summary>候选接口：1D57:FA65 的 0x000A/0x0000 集合（5 字节输入、无输出、无 feature）。</summary>
    protected override bool IsCandidate(HidDeviceInfo info)
        => info.VendorId == DeluxVid && info.ProductId == M900ProPid
           && info.UsagePage == 0x000A && info.Usage == 0x0000
           && info.InputReportLength >= DeluxCodec.FrameLength
           && info.OutputReportLength == 0
           && info.FeatureReportLength == 0;

    /// <summary>探测 = 被动听一帧（约 2 秒一条），听不到返回 null 进冷却。</summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        => ProbeAsync(handle).GetAwaiter().GetResult();

    protected override async Task<(string Protocol, int? Percent, ChargingState Charging)?> ProbeAsync(
        HidDeviceHandle handle)
    {
        var status = await ListenStatusAsync(handle, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return status is { } value ? ("compx-status5", value.Percent, value.Charging) : null;
    }

    /// <summary>读数由后台被动监听负责；轮询不再占 ReadInput。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice _) => null;

    // Only filter state survives request-scoped plugin instances; no handles or listeners.
    private sealed class DockState
    {
        public bool Charging;
        public DateTimeOffset SettleUntil;
        public DateTimeOffset LastSeen;
    }
    private static readonly Dictionary<string, DockState> DockStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object DockGate = new();
    private const int MaximumDockStates = 512;

    internal static (int? Percent, ChargingState Charging)? FilterStatus(
        string path, byte[] input, DateTimeOffset now)
    {
        if (!DeluxCodec.TryParse(input, out int percent, out _, out byte flag)
            || flag is not (DeluxCodec.FlagDischarging or DeluxCodec.FlagCharging or DeluxCodec.FlagFull))
            return null;
        lock (DockGate)
        {
            if (!DockStates.TryGetValue(path, out var state))
            {
                if (DockStates.Count >= MaximumDockStates)
                    DockStates.Remove(DockStates.MinBy(p => p.Value.LastSeen).Key);
                DockStates[path] = state = new DockState();
            }
            state.LastSeen = now;
            if (flag == DeluxCodec.FlagCharging)
            {
                state.Charging = true;
                return (null, ChargingState.Charging);
            }
            if (flag == DeluxCodec.FlagFull)
            {
                state.Charging = false;
                state.SettleUntil = default;
                return (percent, ChargingState.Full);
            }
            if (state.Charging)
            {
                state.Charging = false;
                state.SettleUntil = now + TimeSpan.FromSeconds(8);
            }
            return (now >= state.SettleUntil ? percent : null, ChargingState.Discharging);
        }
    }

    protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice dev, byte[] input)
        => FilterStatus(dev.Info.DevicePath, input, DateTimeOffset.UtcNow) is { } status
            ? (status.Percent, status.Charging, false) : null;

    private static async Task<(int? Percent, ChargingState Charging)?> ListenStatusAsync(
        HidDeviceHandle handle, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var slice = remaining < TimeSpan.FromMilliseconds(400) ? remaining : TimeSpan.FromMilliseconds(400);
            if (slice <= TimeSpan.Zero) break;
            var input = await handle.ReadInputAsync(slice).ConfigureAwait(false);
            if (input == null) continue;
            if (FilterStatus(handle.Info.DevicePath, input, DateTimeOffset.UtcNow) is { } status)
                return status;
        }
        return null;
    }

    protected override string BuildDisplayName(HidDeviceInfo info) => "DELUX M900 Pro";
}
