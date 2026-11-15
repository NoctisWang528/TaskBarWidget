using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Razer;

/// <summary>
/// 功 能： 缓存雷蛇读数并交替调度电量与充电查询。
/// 2026/10/24 19:30:14 Mzood
/// </summary>
internal sealed class RazerReadingState
{
    private readonly bool _headset;
    private bool? _charging;
    public byte NextCommand { get; private set; }
    public int? Percent { get; private set; }
    public ChargingState Charging => _charging.HasValue && Percent.HasValue
        ? ChargingStateMap.FromChargingFlag(_charging.Value, Percent.Value)
        : ChargingState.Unknown;

    public RazerReadingState(bool headset = false)
    {
        _headset = headset;
        NextCommand = BatteryCommand;
    }

    private byte BatteryCommand => _headset ? RazerHeadsetCodec.BatteryCommand : RazerViperSeCodec.BatteryCommand;
    private byte ChargingCommand => _headset ? RazerHeadsetCodec.ChargingCommand : RazerViperSeCodec.ChargingCommand;

    public bool TryApply(ReadOnlySpan<byte> report)
    {
        if (NextCommand == BatteryCommand && TryReadBattery(report, out int percent))
        {
            Percent = percent;
            NextCommand = ChargingCommand;
            return true;
        }
        if (NextCommand == ChargingCommand && Percent.HasValue
            && TryReadCharging(report, out bool charging))
        {
            _charging = charging;
            NextCommand = BatteryCommand;
            return true;
        }
        RecordFailure();
        return false;
    }

    public void RecordFailure()
    {
        // 充电查询失败也先恢复读电量，避免可选状态阻断后续电量刷新。
        NextCommand = BatteryCommand;
        _charging = null;
    }

    private bool TryReadBattery(ReadOnlySpan<byte> report, out int percent) => _headset
        ? RazerHeadsetCodec.TryParseBattery(report, out percent)
        : RazerViperSeCodec.TryParseBattery(report, out percent);

    private bool TryReadCharging(ReadOnlySpan<byte> report, out bool charging) => _headset
        ? RazerHeadsetCodec.TryParseCharging(report, out charging)
        : RazerViperSeCodec.TryParseCharging(report, out charging);
}
