using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Ajazz;

/// <summary>
/// 功 能： 查询黑爵 AK870 接收器的电量与在线状态。
/// 2026/10/18 14:45:00 KNA7022
/// 仅使用已核实的 F7 状态命令，不发送配置、配对或固件命令。
/// </summary>
public sealed class AjazzPlugin : BatteryPluginBase
{
    public override string Name => "Ajazz";
    public override string DisplayName => "黑爵";
    public override string DisplayNameEn => "AJAZZ";
    public override DeviceKind Kind => DeviceKind.Keyboard;

    protected override bool IsCandidate(HidDeviceInfo info) =>
        info.VendorId == 0x3151 && info.ProductId == 0x4011
        && info.UsagePage == 0xFFFF && info.Usage == 2 && info.FeatureReportLength == 65;

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var result = Query(handle);
        return result is { Online: true } value
            ? ("royuan-f7", value.Percent, ChargingState.Unknown) : null;
    }

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;
        var result = Query(dev.Handle);
        if (result is not { } value) return null;
        if (!value.Online)
        {
            RequestOffline(dev);
            return null;
        }
        return (value.Percent, ChargingState.Unknown);
    }

    private (int Percent, bool Online)? Query(HidDeviceHandle handle)
    {
        if (!WritesEnabled || !handle.SetFeature(0, AjazzCodec.CreateStatusQuery())) return null;
        Thread.Sleep(100);
        var report = handle.GetFeature(0);
        return report != null && AjazzCodec.TryParse(report, out int percent, out bool online)
            ? (percent, online) : null;
    }

    protected override string BuildDisplayName(HidDeviceInfo info) => "黑爵 AK870";
}
