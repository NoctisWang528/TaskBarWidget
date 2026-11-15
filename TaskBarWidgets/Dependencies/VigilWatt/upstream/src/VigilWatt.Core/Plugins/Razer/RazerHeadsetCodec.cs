namespace VigilWatt.Core.Plugins.Razer;

/// <summary>
/// 功 能： 编解码 BlackShark V2 Pro 2023 的 PA 电量协议。
/// 2026/10/25 15:31:00 Mzood
/// </summary>
internal static class RazerHeadsetCodec
{
    public const int ReportLength = 64;
    public const byte ReportId = 2;
    public const byte BatteryCommand = 0x21;
    public const byte ChargingCommand = 0x2A;

    public static byte[] CreateQuery(byte command)
    {
        if (command is not (BatteryCommand or ChargingCommand))
            throw new ArgumentOutOfRangeException(nameof(command));
        var frame = Envelope();
        frame[2] = 8;
        frame[7] = 8;
        frame[9] = 3;
        frame[10] = command;
        return frame;
    }

    public static byte[] CreateRemoteMode(bool enabled)
    {
        var frame = Envelope();
        frame[2] = 7;
        frame[7] = 0x0E;
        frame[9] = 2;
        frame[10] = 0xE1;
        frame[11] = enabled ? (byte)1 : (byte)0;
        return frame;
    }

    public static bool TryParseBattery(ReadOnlySpan<byte> report, out int percent)
    {
        percent = 0;
        if (!TryParse(report, BatteryCommand, out byte value) || value > 100) return false;
        percent = value;
        return true;
    }

    public static bool TryParseCharging(ReadOnlySpan<byte> report, out bool charging)
    {
        charging = false;
        if (!TryParse(report, ChargingCommand, out byte value)) return false;
        charging = value != 0;
        return true;
    }

    private static bool TryParse(ReadOnlySpan<byte> report, byte command, out byte value)
    {
        value = 0;
        if (report.Length != ReportLength || report[0] != ReportId
            || report[2] != 0x50 || report[3] != 0x49
            || report[12] != command || report[13] != 1 || report[14] != 1)
            return false;
        value = report[15];
        return true;
    }

    private static byte[] Envelope()
    {
        var frame = new byte[ReportLength];
        frame[0] = ReportId;
        frame[1] = 0x80;
        frame[5] = 0x50;
        frame[6] = 0x41;
        return frame;
    }
}
