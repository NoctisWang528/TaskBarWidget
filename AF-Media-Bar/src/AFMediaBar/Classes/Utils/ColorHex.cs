using System.Globalization;
using System.Windows.Media;

namespace AFMediaBar.Classes.Utils;

/// <summary>
/// 强调色在设置里的文本形式：读写 <c>#RRGGBB</c>，接受常见的几种写法并拒绝其余输入。
/// 这一层只做文本与颜色之间的换算，不理解"跟随系统"这类语义，因此设置模型与强调色策略可以共用它而不产生反向依赖。
/// The text form of an accent colour in the settings: it reads and writes <c>#RRGGBB</c>, accepts the common spellings, and
/// rejects everything else. This layer only converts between text and colour and knows nothing about semantics such as "follow
/// the system", so both the settings model and the accent policy can share it without a reverse dependency.
/// </summary>
public static class ColorHex
{
    /// <summary>解析十六进制颜色文本：接受 <c>#RRGGBB</c>、<c>RRGGBB</c>、<c>#AARRGGBB</c> 与 <c>AARRGGBB</c>（大小写不限）。 / Parses hexadecimal colour text: <c>#RRGGBB</c>, <c>RRGGBB</c>, <c>#AARRGGBB</c>, and <c>AARRGGBB</c>, case-insensitive.</summary>
    /// <param name="text">待解析文本，可为 null。/ Text to parse, may be null.</param>
    /// <param name="color">解析结果；失败时为默认值。/ Parsed colour, or the default value when parsing fails.</param>
    /// <returns>是否解析成功。/ Whether parsing succeeded.</returns>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        if (value.StartsWith('#'))
            value = value[1..];

        // 只接受 6 位（RGB）与 8 位（ARGB）：3 位缩写与颜色名会让"用户到底选了什么"变得难以核对，
        // 而强调色是会被写回设置文件的持久值。
        // Only 6-digit (RGB) and 8-digit (ARGB) forms are accepted: the 3-digit shorthand and colour names would make "what did
        // the user actually pick" hard to verify, and an accent is a persisted value written back to the settings file.
        if (value.Length is not (6 or 8))
            return false;

        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return false;

        color = value.Length == 6
            ? Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : Color.FromArgb((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return true;
    }

    /// <summary>把颜色写成 <c>#RRGGBB</c>（丢弃 alpha）。 / Formats a colour as <c>#RRGGBB</c>, dropping alpha.</summary>
    /// <param name="color">待格式化的颜色。/ Colour to format.</param>
    public static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
