using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Thunderobot;

/// <summary>
/// 功 能： 通过官方设备信息查询读取雷神 ML903 NearLink Se 电量。
/// 2026/10/18 14:55:00 KNA7022
/// </summary>
public sealed class ThunderobotPlugin : BatteryPluginBase
{
    public override string Name => "Thunderobot";
    public override string DisplayName => "雷神";
    public override string DisplayNameEn => "THUNDEROBOT";
    public override DeviceKind Kind => DeviceKind.Mouse;

    protected override bool IsCandidate(HidDeviceInfo info) =>
        info.VendorId == 0x369B && info.ProductId == 0x0234
        && info.UsagePage == 0xFFA0 && info.Usage == 2
        && info.OutputReportId == 0xA0 && info.OutputReportLength == 64 && info.InputReportLength == 64;

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var percent = Query(handle);
        return percent.HasValue ? ("iton-device-info", percent, ChargingState.Unknown) : null;
    }

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;
        var percent = Query(dev.Handle);
        return percent.HasValue ? (percent, ChargingState.Unknown) : null;
    }

    private int? Query(HidDeviceHandle handle)
    {
        if (!WritesEnabled || !handle.WriteOutput(ThunderobotCodec.CreateQuery(), TimeSpan.FromSeconds(1))) return null;
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < deadline)
        {
            var report = handle.ReadInput(TimeSpan.FromMilliseconds(100));
            if (report != null && ThunderobotCodec.TryParse(report, out int percent)) return percent;
        }
        return null;
    }

    protected override string BuildDisplayName(HidDeviceInfo info) => "雷神 ML903 NearLink Se";
}
