using VigilWatt.App.Services;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： release-notes.md 格式契约的回归测试（打包截段与应用内渲染都按这个结果走）。
/// 2026/10/18 22:30:00 xliu
/// </summary>
public sealed class ReleaseNotesTests
{
    [Fact]
    public void ParseDocument_SplitsSectionsAndIgnoresHeader()
    {
        const string md = """
            # 更新日志
            > 维护者说明，不给用户看

            ## 1.2.1
            2026-10-18
            - 第一条
            - 第二条

            ## 1.2.0
            - 旧版本条目
            """;

        var sections = ReleaseNotes.ParseDocument(md);

        Assert.Equal(2, sections.Count);
        Assert.Equal("1.2.1", sections[0].Version);
        Assert.Equal("2026-10-18", sections[0].Date);
        Assert.Equal(2, sections[0].Lines.Count);
        Assert.All(sections[0].Lines, line => Assert.Equal(ReleaseNoteKind.Bullet, line.Kind));
        Assert.Equal("第一条", sections[0].Lines[0].Text);
        Assert.Equal("1.2.0", sections[1].Version);
        Assert.Null(sections[1].Date);
    }

    [Fact]
    public void ParseDocument_OnlyTreatsFirstLineAsDate()
    {
        const string md = """
            ## 1.2.1
            - 2026-10-18 这一行是正文
            """;

        var section = ReleaseNotes.ParseDocument(md)[0];

        Assert.Null(section.Date);
        Assert.Equal("2026-10-18 这一行是正文", section.Lines[0].Text);
    }

    [Fact]
    public void ParseSection_SkipsVersionHeadingWhenGivenWholeFile()
    {
        const string block = """
            ## 1.2.2
            2026-10-18
            - 只有一条
            """;

        var section = ReleaseNotes.ParseSection(block, "1.2.2");

        Assert.Single(section.Lines);
        Assert.Equal("只有一条", section.Lines[0].Text);
        Assert.Equal("2026-10-18", section.Date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromFeed_ReturnsNullWhenEmpty(string? markdown)
    {
        Assert.Null(ReleaseNotes.FromFeed(markdown, "1.2.2"));
    }
}
