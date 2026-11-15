using System.Text.RegularExpressions;

namespace VigilWatt.Core.Ble;

/// <summary>
/// 功 能： 蓝牙双通道 ID 归一化：同一台设备的经典与 LE 端点共享同一个 MAC，据此判"是不是同一台"。
/// 2026/10/24 10:40:12 20211
/// 手机在系统里会同时注册 Bluetooth#… 与 BluetoothLE#… 两个 AEP，不归一就会在列表里出现两条。
/// </summary>
public static class DualChannelId
{
    /// <summary>取端点 ID 里的设备 MAC（大写 12 位）；不是 <c>btc:</c> / <c>ble:</c> ID 时返回 null。</summary>
    public static string? Mac(string? deviceId)
    {
        if (deviceId == null || !TryStrip(deviceId, out var aepId)) return null;

        // ⚠️ 必须至少两段 MAC（适配器 + 设备）：只有一段说明 ID 畸形，把适配器 MAC 当设备 MAC
        // 会让该适配器下所有设备互相合并。
        var matches = AepMac.Matches(aepId);
        if (matches.Count < 2) return null;

        return matches[^1].Value.Replace(":", "").Replace("-", "").ToUpperInvariant();
    }

    public static bool IsBle(string? deviceId) => deviceId?.StartsWith("ble:", StringComparison.Ordinal) == true;

    public static bool IsClassic(string? deviceId) => deviceId?.StartsWith("btc:", StringComparison.Ordinal) == true;

    private static bool TryStrip(string id, out string aepId)
    {
        if (IsBle(id) || IsClassic(id))
        {
            aepId = id[4..];
            return true;
        }
        aepId = "";
        return false;
    }

    private static readonly Regex AepMac = new(@"(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);
}
