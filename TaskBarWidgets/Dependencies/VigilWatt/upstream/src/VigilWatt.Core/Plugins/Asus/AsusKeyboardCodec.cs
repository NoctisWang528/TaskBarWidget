namespace VigilWatt.Core.Plugins.Asus;

/// <summary>
/// 功 能： 解析 ROG Omni 配对身份与 Scope II 96 电量响应。
/// 2026/10/25 15:31:00 Mzood
/// </summary>
internal static class AsusKeyboardCodec
{
    public const int ReportLength = 64;
    public const byte KeyboardReportId = 2;

    public static bool IsKeyboardInterface(ushort vid, ushort pid, ushort page, ushort usage,
        ushort inputLength, ushort outputLength, byte? reportId, string path) =>
        vid == 0x0B05 && pid == 0x1ACE && page == 0xFF00 && usage == 1
        && inputLength == ReportLength && outputLength == ReportLength
        && reportId == KeyboardReportId
        && path.Contains("&mi_02&col02#", StringComparison.OrdinalIgnoreCase);

    public static string? ReceiverKey(string path)
    {
        var parts = path.Split('#');
        if (parts.Length < 4) return null;
        int collection = parts[1].IndexOf("&col", StringComparison.OrdinalIgnoreCase);
        int instance = parts[2].LastIndexOf('&');
        return collection < 0 || instance < 0 ? null
            : parts[1][..collection] + "#" + parts[2][..instance];
    }

    public static byte[] CreatePairedQuery() => Frame(1, 0xA0, 0);
    public static byte[] CreateBatteryQuery() => Frame(KeyboardReportId, 0x12, 1);

    public static bool HasScopeKeyboard(ReadOnlySpan<byte> reply)
    {
        if (reply.Length != ReportLength || reply[0] != 1 || reply[1] != 0xA0
            || reply[2] != 0 || reply[4] != 0 || reply[3] > (ReportLength - 5) / 4)
            return false;
        bool found = false;
        for (int i = 0, at = 5; i < reply[3]; i++, at += 4)
        {
            int pid = reply[at] | reply[at + 1] << 8;
            if (reply[at + 2] != KeyboardReportId) continue;
            if (found || reply[at + 3] != 4 || pid is not (0x1AAE or 0x1AAF or 0x1AB0))
                return false;
            found = true;
        }
        return found;
    }

    public static bool TryParseBattery(ReadOnlySpan<byte> reply, out int percent, out bool charging)
    {
        percent = 0;
        charging = false;
        if (reply.Length != ReportLength || reply[0] != KeyboardReportId
            || reply[1] != 0x12 || reply[2] != 1 || reply[6] > 100 || reply[9] > 1)
            return false;
        percent = reply[6];
        charging = reply[9] == 1;
        return true;
    }

    private static byte[] Frame(byte id, byte command, byte subcommand)
    {
        var frame = new byte[ReportLength];
        frame[0] = id;
        frame[1] = command;
        frame[2] = subcommand;
        return frame;
    }
}
