using System.Globalization;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 解析用户在「手动发帧」里输入的十六进制。
/// 2026/10/24 22:25:00 20211
/// 允许空格/逗号/短横线/冒号分隔；一帧最多 64 字节（够所有 HID output 报文，也挡住误粘大段文本）。
/// </summary>
public static class CaptureHex
{
    public const int MaxBytes = 64;

    public static bool TryParse(string? text, out byte[] bytes, out string? error)
    {
        bytes = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "先填一帧十六进制";
            return false;
        }

        var cleaned = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not (',' or '-' or ':')).ToArray());
        if (cleaned.Length == 0)
        {
            error = "没解析到任何十六进制字符";
            return false;
        }

        if (cleaned.Length % 2 != 0)
        {
            error = "十六进制位数必须是偶数（两个字符一个字节）";
            return false;
        }

        if (cleaned.Length / 2 > MaxBytes)
        {
            error = $"一帧最多 {MaxBytes} 字节";
            return false;
        }

        var result = new byte[cleaned.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out result[i]))
            {
                error = $"第 {i + 1} 个字节不是合法十六进制";
                return false;
            }
        }

        bytes = result;
        return true;
    }

    public static string Format(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2")));
}
