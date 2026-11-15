using System.Collections.Concurrent;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Razer;

/// <summary>
/// 功 能： 雷蛇 Kraken V4 接收器的电量查询（电池与充电状态交替，每轮只发 1 帧）；与 RazerPlugin 共用品牌开关。
/// 2026/10/19 22:26:13 kaniu
/// </summary>
public sealed class RazerKrakenPlugin : BatteryPluginBase
{
    public override string Name => "Razer";
    public override string DisplayName => "雷蛇";
    public override string DisplayNameEn => "RAZER";
    public override DeviceKind Kind => DeviceKind.Headset;
    protected override int MinValidPercent(HidDeviceInfo info) => 0;
    protected override OfflinePolicy Offline { get; } = new() { CountPollFailures = false };

    /// <summary>电池与充电是两条命令，按帧限频每轮只能发一条，故按设备交替（见 razer-kraken.md）。</summary>
    private readonly ConcurrentDictionary<string, int> _turns = new(StringComparer.OrdinalIgnoreCase);

    protected override bool IsCandidate(HidDeviceInfo info)
        => info.VendorId == RazerKrakenCodec.VendorId && info.ProductId == RazerKrakenCodec.KrakenV4DonglePid
           && info.UsagePage == RazerKrakenCodec.UsagePage && info.Usage == RazerKrakenCodec.Usage
           && info.OutputReportId == RazerKrakenCodec.ReportId
           && info.InputReportLength == RazerKrakenCodec.ReportLength
           && info.OutputReportLength == RazerKrakenCodec.ReportLength;

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        => QueryAsync(handle, RazerKrakenCodec.GetBatteryStatus).GetAwaiter().GetResult() is { } level
            ? (RazerKrakenCodec.Protocol, level, ChargingState.Unknown) : null;

    protected override async Task<(string Protocol, int? Percent, ChargingState Charging)?> ProbeAsync(
        HidDeviceHandle handle)
        => await QueryAsync(handle, RazerKrakenCodec.GetBatteryStatus).ConfigureAwait(false) is { } level
            ? (RazerKrakenCodec.Protocol, level, ChargingState.Unknown) : null;

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;

        bool askBattery = (_turns.AddOrUpdate(dev.Status.Id, 0, (_, turn) => turn + 1) & 1) == 0;
        byte command = askBattery ? RazerKrakenCodec.GetBatteryStatus : RazerKrakenCodec.GetChargingStatus;
        int? reply = QueryAsync(dev.Handle, command).GetAwaiter().GetResult();
        if (!reply.HasValue)
        {
            ReportPollFailure(dev);
            return null;
        }

        ReportPollAnswered(dev);
        return askBattery
            ? (reply, dev.Status.Charging)
            : (dev.Status.BatteryPercent, RazerKrakenCodec.ToChargingState((byte)reply.Value, dev.Status.BatteryPercent));
    }

    public override void Detach()
    {
        _turns.Clear();
        base.Detach();
    }

    /// <summary>写一条查询并等它的应答；返回应答里的单字节载荷，失败返回 null。</summary>
    private async Task<int?> QueryAsync(HidDeviceHandle handle, byte command)
    {
        if (!WritesEnabled || !handle.WriteOutput(RazerKrakenCodec.BuildQuery(command), TimeSpan.FromMilliseconds(500)))
            return null;

        var deadline = DateTime.UtcNow.AddSeconds(1.2);
        while (DateTime.UtcNow < deadline)
        {
            var input = await handle.ReadInputAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            if (input == null) continue;

            if (command == RazerKrakenCodec.GetBatteryStatus)
            {
                if (RazerKrakenCodec.TryParseBattery(input, out int percent)) return percent;
            }
            else if (RazerKrakenCodec.TryParseCharging(input, out byte flag))
            {
                return flag;
            }
        }
        return null;
    }

    protected override string BuildDisplayName(HidDeviceInfo info) => "Razer Kraken V4";
}
