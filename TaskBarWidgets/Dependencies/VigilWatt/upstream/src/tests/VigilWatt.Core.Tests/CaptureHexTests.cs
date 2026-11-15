using VigilWatt.Core.Capture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 手动发帧的十六进制解析：分隔符、奇偶位、长度上限、非法字符。
/// 2026/10/24 22:25:00 20211
/// </summary>
public sealed class CaptureHexTests
{
    [Theory]
    [InlineData("13 4A 00", new byte[] { 0x13, 0x4A, 0x00 })]
    [InlineData("134A00", new byte[] { 0x13, 0x4A, 0x00 })]
    [InlineData("13,4a,00", new byte[] { 0x13, 0x4A, 0x00 })]
    [InlineData("13-4A-00", new byte[] { 0x13, 0x4A, 0x00 })]
    [InlineData("  13:4a:00  ", new byte[] { 0x13, 0x4A, 0x00 })]
    public void TryParse_AcceptsCommonSeparators(string text, byte[] expected)
    {
        Assert.True(CaptureHex.TryParse(text, out var bytes, out var error), error);
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("", "先填一帧")]
    [InlineData("   ", "先填一帧")]
    [InlineData("134", "偶数")]
    [InlineData("13 4G", "不是合法十六进制")]
    public void TryParse_ExplainsWhatIsWrong(string text, string expectedFragment)
    {
        Assert.False(CaptureHex.TryParse(text, out _, out var error));
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void TryParse_RejectsFramesLongerThanLimit()
    {
        var text = string.Join(' ', Enumerable.Repeat("00", CaptureHex.MaxBytes + 1));
        Assert.False(CaptureHex.TryParse(text, out _, out var error));
        Assert.Contains("最多", error);
    }

    [Fact]
    public void Format_UsesSpacedUpperHex()
        => Assert.Equal("13 4A 00", CaptureHex.Format([0x13, 0x4A, 0x00]));
}
