using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskBarWidgets.Media.Peripherals;

internal sealed record HeadsetControlDevice(string Id, string Name, string Pair, string[] Paths);

internal static class HeadsetControlDevices
{
    // Audited subset of the official 4.1.0 registry, not a claim of hardware verification.
    // Maxwell is excluded: 4.1.0's battery initialization sends a setting packet.
    // Earbuds are excluded: this CLI loses independent left/right/case roles.
    private static readonly HashSet<string> Supported = new((
        "046d:0a66 046d:0ac4 046d:0a1f 046d:0a5c 046d:0a89 046d:0a5b 046d:0a87 046d:0ab5 046d:0afe 046d:0b1f " +
        "046d:0aa7 046d:0aaa 046d:0aba 046d:0afb 046d:0afc 046d:0af7 046d:0b18 046d:0b1c " +
        "03f0:0696 0951:1718 03f0:098d 0951:16c4 0951:1723 17ef:a07d 047f:02ee 054c:0ebf " +
        "1038:12b3 1038:12b6 1038:12d7 1038:12d5 1038:1260 1038:12ad 1038:1252 1038:1280 " +
        "1038:220e 1038:2212 1038:2216 1038:2236 1038:12c2 1038:2269 1038:226d 1038:2232 1038:2253 " +
        "1038:2202 1038:22a1 1038:227e 1038:2206 1038:2258 1038:229e 1038:22ad 1038:223a 1038:22a9 " +
        "1038:227a 1038:22a4 1038:22a5 1038:220a 1038:22a7 1038:2298 1038:12e0 1038:12e5 1038:1290"
        ).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static readonly HashSet<string> Discrete = new(
        ["1038:2202", "1038:2206", "1038:223a", "1038:227a", "1038:22a4", "1038:220a", "1038:1290"], StringComparer.Ordinal);

    internal static string? Pair(WindowsPeripheralNode node)
    {
        foreach (var hardwareId in node.HardwareIds.Append(node.InstanceId))
        {
            var match = Regex.Match(hardwareId, @"(?:^|[\\&])VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&|$|\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) return (match.Groups[1].Value + ":" + match.Groups[2].Value).ToLowerInvariant();
        }
        return null;
    }

    internal static Dictionary<string, HeadsetControlDevice> Map(IReadOnlyList<WindowsPeripheralNode> nodes)
    {
        var result = new Dictionary<string, HeadsetControlDevice>(StringComparer.Ordinal);
        // Include every present matching node when checking ambiguity, including unknown containers.
        foreach (var pair in nodes.Where(n => n.Present).Select(n => (Node: n, Pair: Pair(n)))
            .Where(n => n.Pair is not null && Supported.Contains(n.Pair)).GroupBy(n => n.Pair!))
        {
            if (pair.Any(n => n.Node.Container == Guid.Empty || n.Node.Bluetooth)) continue;
            var containers = pair.Select(n => n.Node.Container).Distinct().ToArray();
            if (containers.Length != 1) continue;
            var container = containers[0];
            var all = nodes.Where(n => n.Container == container).ToArray();
            if (all.Any(n => n.BluetoothLE || WindowsPeripheralIdentity.Percentage(n.Battery).HasValue)) continue;
            var key = WindowsPeripheralIdentity.Key(pair.First().Node, nodes);
            if (key is null) continue;
            var id = PeripheralIdentity.FromKey(key);
            result[id] = new(id, all.Select(n => n.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Headset",
                pair.Key, pair.Select(n => n.Node.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        return result;
    }

    internal static PeripheralBatteryReading? Reading(string text, string pair, out bool disconnected)
    {
        disconnected = false;
        if (text.Length > 65536 || text.Contains("Error:", StringComparison.OrdinalIgnoreCase)) return null;
        // Pin the exact standard-output grammar. JSON would silently query all INFO capabilities.
        if (Regex.Matches(text, @"(?m)^Found 1 supported device\(s\):\r?$").Count != 1 ||
            Regex.Matches(text, @"(?m)^Battery:\r?$").Count != 1 ||
            !text.Contains("[0x" + pair[..4] + ":0x" + pair[5..] + "]", StringComparison.OrdinalIgnoreCase)) return null;
        var status = Regex.Match(text, @"(?m)^\s*Status: (BATTERY_AVAILABLE|BATTERY_CHARGING|BATTERY_UNAVAILABLE)\r?$");
        if (!status.Success) return null;
        if (status.Groups[1].Value == "BATTERY_UNAVAILABLE") { disconnected = true; return null; }
        var charging = status.Groups[1].Value == "BATTERY_CHARGING";
        var level = Regex.Match(text, @"(?m)^\s*Level: (-?\d{1,3})%\r?$");
        if (!level.Success || !int.TryParse(level.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var percentage)) return null;
        if (percentage == -1 && charging) return new() { Charging = true };
        if (percentage is < 0 or > 100) return null;
        if (Discrete.Contains(pair))
        {
            if (percentage % 25 != 0) return null;
            return new() { LevelText = $"{percentage / 25}/4", Charging = charging ? true : null };
        }
        if (pair is "1038:12e0" or "1038:12e5")
        {
            var levelIndex = Enumerable.Range(0, 9).FirstOrDefault(i => i * 100 / 8 == percentage, -1);
            return levelIndex < 0 ? null : new() { LevelText = $"{levelIndex}/8", Charging = charging ? true : null };
        }
        // The HID++ models derive capacity from voltage/calibration rather than absolute charge.
        var estimated = pair is "046d:0a66" or "046d:0ac4" or "046d:0a1f" or "046d:0a5c" or "046d:0a89"
            or "046d:0a5b" or "046d:0a87" or "046d:0ab5" or "046d:0afe" or "046d:0b1f"
            or "046d:0aa7" or "046d:0aaa" or "046d:0aba" or "046d:0afb" or "046d:0afc" or "1038:12c2";
        return new() { Percentage = percentage, Charging = charging ? true : null, Estimated = estimated };
    }
}
