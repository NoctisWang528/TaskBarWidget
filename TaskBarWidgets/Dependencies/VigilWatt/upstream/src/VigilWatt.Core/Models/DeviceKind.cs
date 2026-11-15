namespace VigilWatt.Core.Models;

/// <summary>
/// 功 能： 设备类型（UI 用来选图标）。
/// 2026/10/06 15:53:36 xliu
/// 详见 docs/project/architecture.md §6。
/// ⚠️ 新值**只能追加在末尾**：IconKind 以数字存进 settings.json，插在中间会让老用户的自定义图标整体错位。
/// </summary>
public enum DeviceKind
{
    Unknown,
    Mouse,
    Keyboard,
    Headset,
    Speaker,
    Gamepad,

    /// <summary>本机（笔记本电池 / 系统电源）。</summary>
    Computer,

    Phone,
    Tablet,
    Watch,
}
