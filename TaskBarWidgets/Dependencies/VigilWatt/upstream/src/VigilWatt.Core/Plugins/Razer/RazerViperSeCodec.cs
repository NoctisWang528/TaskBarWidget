namespace VigilWatt.Core.Plugins.Razer;

/// <summary>
/// 功 能： 编解码雷蛇电量与充电状态查询及响应。
/// 2026/10/24 19:30:14 Mzood
/// </summary>
internal static class RazerViperSeCodec
{
    public const int PayloadLength = 90;
    public const int ReportLength = PayloadLength + 1;
    public const byte BatteryCommand = 0x80;
    public const byte ChargingCommand = 0x84;
    private const byte TransactionId = 0x1F;
    private const byte CommandClass = 0x07;

    public static byte[] CreateQuery(byte command)
    {
        if (!IsReadCommand(command))
            throw new ArgumentOutOfRangeException(nameof(command));
        var payload = new byte[PayloadLength];
        payload[1] = TransactionId;
        payload[5] = 2;
        payload[6] = CommandClass;
        payload[7] = command;
        payload[88] = Checksum(payload);
        return payload;
    }

    public static bool TryParseBattery(ReadOnlySpan<byte> report, out int percent)
    {
        percent = 0;
        if (!TryParse(report, BatteryCommand, out byte raw)) return false;
        percent = (raw * 100 + 127) / 255;
        return true;
    }

    public static bool TryParseCharging(ReadOnlySpan<byte> report, out bool charging)
    {
        charging = false;
        if (!TryParse(report, ChargingCommand, out byte raw) || raw > 1) return false;
        charging = raw == 1;
        return true;
    }

    private static bool TryParse(ReadOnlySpan<byte> report, byte command, out byte value)
    {
        value = 0;
        if (report.Length != ReportLength || report[0] != 0) return false;
        var payload = report[1..];
        if (payload[0] != 2 || payload[1] != TransactionId
            || payload[2] != 0 || payload[3] != 0 || payload[4] != 0
            || payload[5] != 2 || payload[6] != CommandClass || payload[7] != command
            || payload[88] != Checksum(payload) || payload[89] != 0)
            return false;
        value = payload[9];
        return true;
    }

    private static byte Checksum(ReadOnlySpan<byte> payload)
    {
        byte checksum = 0;
        for (int i = 2; i < 88; i++) checksum ^= payload[i];
        return checksum;
    }

    private static bool IsReadCommand(byte command) =>
        command is BatteryCommand or ChargingCommand;
}
