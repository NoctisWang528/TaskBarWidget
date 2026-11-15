using VigilWatt.App.Services;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 贴片选屏：换显示模式后"该去哪块屏"必须可复现。
/// 回归的是 2026/10/20 的 bug——配置存 \\.\DISPLAYn，切到"仅电脑屏幕"后序号消失，贴片直接不回来。
/// </summary>
public class MonitorSelectionTests
{
    private const string DellId = @"\\?\DISPLAY#DEL4189#4&194fdf6a&0&UID8259#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string PanelId = @"\\?\DISPLAY#AUO5B2D#4&194fdf6a&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static MonitorScreen Screen(string gdi, string id, string label, bool primary, int w = 2560, int h = 1440) =>
        new(gdi, id, label, new PixelRect(0, 0, w, h), new PixelRect(0, 0, w, h - 48), primary);

    /// <summary>扩展模式：外接 Dell 在主屏，内置屏在右。</summary>
    private static List<MonitorScreen> Extended() =>
    [
        Screen(@"\\.\DISPLAY1", DellId, "DELL S2719DC", primary: true),
        Screen(@"\\.\DISPLAY2", PanelId, "Dell XPS AUO5B2D Display", primary: false, w: 1920, h: 1200),
    ];

    /// <summary>"仅电脑屏幕"：只剩内置屏，而且它现在是 DISPLAY1。</summary>
    private static List<MonitorScreen> PcScreenOnly() =>
    [
        Screen(@"\\.\DISPLAY1", PanelId, "Dell XPS AUO5B2D Display", primary: true, w: 1920, h: 1200),
    ];

    [Fact]
    public void Resolve_PrimaryToken_FollowsPrimary()
    {
        var target = MonitorSelection.Resolve(Extended(), ":primary:", out string note);

        Assert.Equal(DellId, target?.Id);
        Assert.Equal("", note);
    }

    [Fact]
    public void Resolve_Empty_FollowsPrimary()
    {
        var target = MonitorSelection.Resolve(PcScreenOnly(), "", out _);

        Assert.Equal(PanelId, target?.Id);
    }

    /// <summary>核心回归：配的外接屏在"仅电脑屏幕"下不在了 → 贴主屏，而不是整个消失。</summary>
    [Fact]
    public void Resolve_ConfiguredScreenGone_FallsBackToPrimaryWithNote()
    {
        var target = MonitorSelection.Resolve(PcScreenOnly(), DellId, out string note);

        Assert.Equal(PanelId, target?.Id);
        Assert.NotEqual("", note);
    }

    /// <summary>同一块屏换到别的 DISPLAY 序号下（扩展↔仅第二屏）仍要认得出来。</summary>
    [Fact]
    public void Resolve_SameMonitorUnderNewGdiIndex_KeepsThatMonitor()
    {
        // 外接屏变成 DISPLAY2（主屏仍是内置屏）——身份没变，不该跑到主屏去
        var screens = new List<MonitorScreen>
        {
            Screen(@"\\.\DISPLAY1", PanelId, "Dell XPS AUO5B2D Display", primary: true, w: 1920, h: 1200),
            Screen(@"\\.\DISPLAY2", DellId, "DELL S2719DC", primary: false),
        };

        var target = MonitorSelection.Resolve(screens, DellId, out string note);

        Assert.Equal(DellId, target?.Id);
        Assert.Equal("", note);
    }

    /// <summary>老配置存的是 \\.\DISPLAY2：身份匹配不上时按 GDI 名兜底，不能一律回主屏。</summary>
    [Fact]
    public void Resolve_LegacyGdiName_StillMatches()
    {
        var target = MonitorSelection.Resolve(Extended(), @"\\.\DISPLAY2", out string note);

        Assert.Equal(PanelId, target?.Id);
        Assert.Equal("", note);
    }

    /// <summary>同一台显示器换了接口（UID 段变），型号段唯一命中就认它。</summary>
    [Fact]
    public void Match_SameModelOnDifferentPort_MatchesByModelKey()
    {
        var moved = @"\\?\DISPLAY#DEL4189#5&2b1c3d4e&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        var hit = MonitorSelection.Match(Extended(), moved);

        Assert.Equal(DellId, hit?.Id);
    }

    /// <summary>两块同型号显示器：分不清就不猜，交回主屏。</summary>
    [Fact]
    public void Match_TwoIdenticalModels_DoesNotGuess()
    {
        var twin = @"\\?\DISPLAY#DEL4189#5&2b1c3d4e&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        var screens = new List<MonitorScreen>
        {
            Screen(@"\\.\DISPLAY1", DellId, "DELL S2719DC", primary: true),
            Screen(@"\\.\DISPLAY2", twin, "DELL S2719DC", primary: false),
        };
        var unknown = @"\\?\DISPLAY#DEL4189#6&9f8e7d6c&0&UID9999#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        Assert.Null(MonitorSelection.Match(screens, unknown));
    }

    [Fact]
    public void Resolve_NoScreens_ReturnsNull()
    {
        var target = MonitorSelection.Resolve([], DellId, out _);

        Assert.Null(target);
    }

    [Fact]
    public void Describe_AppendsResolutionAndPrimaryMark()
    {
        var screens = Extended();

        Assert.Equal("DELL S2719DC · 2560×1440（主显示器）", MonitorSelection.Describe(screens[0]));
        Assert.Equal("Dell XPS AUO5B2D Display · 1920×1200", MonitorSelection.Describe(screens[1]));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY3", "显示器 3")]
    [InlineData(@"\\.\DISPLAY12", "显示器 12")]
    public void FallbackLabel_StripsGdiPrefix(string gdi, string expected)
    {
        Assert.Equal(expected, MonitorSelection.FallbackLabel(gdi));
    }
}
