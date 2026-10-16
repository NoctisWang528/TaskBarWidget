using Lyricify.Lyrics.Helpers.General;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Services;
using Xunit;

namespace TaskbarLyrics.Core.Tests;

public sealed class VendoredLyricifyTests
{
    [Theory]
    [InlineData("音乐与爱 Music 🎵 e\u0301\r\n", "音樂與愛 Music 🎵 e\u0301\r\n")]
    [InlineData("汉语国风", "漢語國風")]
    public void GenericScriptConversionPreservesOtherUnicodeAndRefOverloads(string simplified, string traditional)
    {
        Assert.Equal(traditional, ChineseHelper.S2T(simplified));
        Assert.Equal(simplified, ChineseHelper.T2S(traditional));
        var text = simplified;
        ChineseHelper.S2T(ref text);
        Assert.Equal(traditional, text);
        ChineseHelper.T2S(ref text);
        Assert.Equal(simplified, text);
    }

    [Fact]
    public void NullEmptyAndForcedConversionKeepTheirApiContract()
    {
        Assert.Equal(string.Empty, ChineseHelper.S2T(null!));
        Assert.Equal(string.Empty, ChineseHelper.T2S(null!));
        Assert.Equal(string.Empty, ChineseHelper.S2T(string.Empty));
        Assert.Equal(string.Empty, ChineseHelper.T2S(string.Empty));
        string text = null!;
        ChineseHelper.S2T(ref text);
        Assert.Equal(string.Empty, text);
        text = null!;
        ChineseHelper.T2S(ref text);
        Assert.Equal(string.Empty, text);
        Assert.Equal("借嘴升脏", "藉咀昇髒".ToSC(force: true));
        Assert.Equal("音樂與愛", "音乐与爱".ToTC(force: true));
        Assert.Equal("音乐与爱", "音樂與愛".ToSC());
    }

    // Synthetic ciphertext generated once and checked against the previous pinned
    // NuGet 0.2.0 decrypters. It contains no provider traffic or third-party lyrics.
    [Theory]
    [InlineData(LyricPayloadFormat.Qrc,
        "D6A2BE95D6447372232FA87285185913ACCDAB8B48D2A4FEEC24AAADCA125221",
        "[1000,1800]fixture(1000,1800)")]
    [InlineData(LyricPayloadFormat.Krc,
        "a3JjMTjbGsglTcDm0Lewo29X7yjsSuWhb4I/jP0eGACE127Mdk67",
        "[1000,1800]<0,1800,0>fixture")]
    public async Task ExistingEncryptedPayloadsStillDecodeAndParse(LyricPayloadFormat format, string ciphertext, string plaintext)
    {
        var provider = format == LyricPayloadFormat.Qrc ? KnownLyricProviders.QQMusic : KnownLyricProviders.Kugou;
        var raw = new RawLyricPayload(provider, "synthetic-vendor-regression", format,
            ciphertext, null, true, false, new Dictionary<string, string>());
        var decoded = await new LyricifyPayloadDecoder().DecodeAsync(raw);
        Assert.Equal(plaintext, decoded.OriginalLyrics);
        var parsed = await new LyricifyPayloadParser().ParseAsync(decoded);
        var line = Assert.Single(parsed.Lines);
        Assert.Equal("fixture", line.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), line.StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(2800), line.EndTime);
        var segment = Assert.Single(line.Segments);
        Assert.Equal(line.StartTime, segment.StartTime);
        Assert.Equal(line.EndTime, segment.EndTime);
    }
}
