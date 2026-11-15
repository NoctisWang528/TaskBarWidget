// Adapted from VigilWatt (MIT, upstream/LICENSE). Battery/status queries only.
using System.Collections.Concurrent;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Razer;

public sealed class RazerPlugin : BatteryPluginBase
{
    // A transaction learned on one receiver must never affect another receiver.
    private static readonly ConcurrentDictionary<string, byte> LearnedTransaction = new(StringComparer.OrdinalIgnoreCase);
    public override string Name => "Razer";
    public override string DisplayName => "雷蛇";
    public override string DisplayNameEn => "RAZER";
    public override DeviceKind Kind => DeviceKind.Mouse;
    protected override bool FeatureOnly(HidDeviceInfo info) => true;
    protected override int MinValidPercent(HidDeviceInfo info) => 0;

    protected override bool IsCandidate(HidDeviceInfo info) =>
        RazerDeviceProfile.Matches(info.VendorId, info.ProductId, info.UsagePage,
            info.Usage, info.FeatureReportLength, info.DevicePath);

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var result = Query(handle);
        return result is { } value ? (handle.Info.ProductId == 0x00DF ? "razer-90b" : "razer-hid",
            value.Percent, value.Charging) : null;
    }

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev) =>
        CanWrite(dev) ? Query(dev.Handle) : null;

    private (int? Percent, ChargingState Charging)? Query(HidDeviceHandle handle)
    {
        // V3 SE uses its own fixed-transaction codec. Never learn/fall back to a
        // V4 transaction or reinterpret its response through the legacy parser.
        if (handle.Info.ProductId == 0x00DF) return QueryViperSe(handle);
        string path = handle.Info.DevicePath;
        var candidates = LearnedTransaction.TryGetValue(path, out byte learned)
            ? new[] { learned }.Concat(RazerCodec.TransactionCandidates(handle.Info.ProductId)).Distinct().ToArray()
            : RazerCodec.TransactionCandidates(handle.Info.ProductId);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            foreach (byte tx in candidates)
            {
                var battery = Exchange(handle, RazerCodec.CommandGetBattery, tx);
                if (!RazerCodec.TryParseBattery(battery, out int raw, tx)) continue;
                if (LearnedTransaction.Count > 1024) LearnedTransaction.Clear();
                LearnedTransaction[path] = tx;
                int percent = RazerCodec.ToPercent(handle.Info.ProductId, raw);
                var charging = ChargingState.Unknown;
                var response = Exchange(handle, RazerCodec.CommandGetCharging, tx);
                if (RazerCodec.TryParseCharging(response, out var value, tx)) charging = value;
                return (percent, charging);
            }
        }
        return null;
    }

    private (int? Percent, ChargingState Charging)? QueryViperSe(HidDeviceHandle handle)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!WritesEnabled || !handle.SetFeature(0, RazerViperSeCodec.CreateQuery(RazerViperSeCodec.BatteryCommand))) continue;
            Thread.Sleep(60);
            var report = handle.GetFeature(0, RazerViperSeCodec.ReportLength);
            if (report == null || !RazerViperSeCodec.TryParseBattery(report, out int percent)) continue;
            var charging = ChargingState.Unknown;
            if (WritesEnabled && handle.SetFeature(0, RazerViperSeCodec.CreateQuery(RazerViperSeCodec.ChargingCommand)))
            {
                Thread.Sleep(60);
                var charge = handle.GetFeature(0, RazerViperSeCodec.ReportLength);
                if (charge != null && RazerViperSeCodec.TryParseCharging(charge, out bool flag))
                    charging = ChargingStateMap.FromChargingFlag(flag, percent);
            }
            return (percent, charging);
        }
        return null;
    }

    private byte[]? Exchange(HidDeviceHandle handle, byte command, byte tx)
    {
        if (!WritesEnabled || !handle.SetFeature(0, RazerCodec.BuildRequest(command, tx))) return null;
        Thread.Sleep(60);
        return handle.GetFeature(0, RazerCodec.BufferLength);
    }

    protected override string BuildDisplayName(HidDeviceInfo info) => info.ProductId switch
    {
        0x00E2 or 0x00E3 => "Razer Basilisk V4 Pro",
        0x00E5 or 0x00E6 => "Razer Viper V4 Pro",
        0x00DF => "Razer Viper V3 Pro SE",
        _ => base.BuildDisplayName(info),
    };
}
