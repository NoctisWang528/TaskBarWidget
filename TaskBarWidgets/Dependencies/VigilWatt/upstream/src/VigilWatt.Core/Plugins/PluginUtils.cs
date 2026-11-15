namespace VigilWatt.Core.Plugins;
/// <summary>
/// 功 能： 插件共用的路径哈希等小工具。
/// 2026/10/06 15:53:36 xliu
/// </summary>

public static class PluginUtils
{
    /// <summary>FNV-1a 64：把字符串映射成稳定短 ID（重启、重扫后不变）。</summary>
    public static string StableHash(string s)
    {
        ulong h = 1469598103934665603;
        foreach (var c in s) { h ^= c; h *= 1099511628211; }
        return h.ToString("X16");
    }

    /// <summary>抹掉 HID 实例段的前两段（随 USB 端口生成），保留集合序号；格式不认识就原样返回。见 docs/project/code-flow.md §2.2。</summary>
    public static string StableDeviceKey(string devicePath)
    {
        // 实例段 7&15dc4901&0&0001：前两段随 USB 口变，后两段（0&集合序号）稳定
        var parts = devicePath.Split('#');
        if (parts.Length != 4) return devicePath;
        var inst = parts[2].Split('&');
        if (inst.Length < 4) return devicePath;
        return $"{parts[0]}#{parts[1]}#{string.Join('&', inst[2..])}#{parts[3]}";
    }

    /// <summary>设备 ID 的哈希源：换 USB 口不产生新 ID（直接哈希整条路径会让同一个接收器变成两台设备）。</summary>
    public static string StableDeviceHash(string devicePath) => StableHash(StableDeviceKey(devicePath));
}
