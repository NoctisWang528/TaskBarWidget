using System.Text.Json.Nodes;
using TaskBarWidgets.UI.Settings;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class WinUiSettingsLayoutTests
{
    [Fact]
    public void PoolAndIndependentRowsKeepConfigurationAndSubmitWholeLayout()
    {
        var state = new SettingsDraft();
        var baseline = JsonNode.Parse("""{"taskbar.horizontal_arrange":false,"items.native_0.show":true,"items.native_0.row":0,"items.native_0.order":1,"items.native_0.label":"↑ ","items.native_1.show":true,"items.native_1.row":1,"items.native_1.order":2,"items.native_1.label":"↓ ","items.builtin_FxOP34Lm.show":false,"items.builtin_FxOP34Lm.row":0,"items.builtin_FxOP34Lm.order":3,"items.builtin_FxOP34Lm.label":"天气 "}""")!.AsObject();
        state.Readback(baseline);
        var layout = new ModuleLayout(state, [new("native_0", "上传", "1 KB/s"), new("native_1", "下载", "2 KB/s"), new("builtin_FxOP34Lm", "天气", "晴")]);
        layout.Move("builtin_FxOP34Lm", 1, 0);
        Assert.Equal(new[] { "builtin_FxOP34Lm", "native_1" }, layout.InRow(1).Select(item => item.Id));
        layout.Move("native_0", 1, 1);
        Assert.Empty(layout.InRow(0));
        Assert.Equal(new[] { "builtin_FxOP34Lm", "native_0", "native_1" }, layout.InRow(1).Select(item => item.Id));
        layout.Remove("builtin_FxOP34Lm");
        Assert.Equal("天气 ", state.Text("items.builtin_FxOP34Lm.label"));
        Assert.False(layout.Enabled("builtin_FxOP34Lm"));
        var patch = new Dictionary<string, JsonNode?>(); layout.IncludeWholeLayout(patch);
        Assert.Equal(9, patch.Count);
        Assert.Equal(new[] { 1, 2, 3 }, patch.Where(pair => pair.Key.EndsWith(".order")).Select(pair => (int)pair.Value!).Order());
        state.Set("taskbar.horizontal_arrange", true);
        Assert.Equal(2, layout.InRow(0).Count);
        state.Set("taskbar.horizontal_arrange", false);
        Assert.Equal(2, layout.InRow(1).Count);
    }

    [Fact]
    public void SaveReadbackKeepsASecondReorderEvenWhenItRevertsTheOriginalValue()
    {
        var state = new SettingsDraft();
        var before = JsonNode.Parse("""{"items.native_0.order":1,"items.native_1.order":2,"showLyricTranslation":false}""")!.AsObject();
        state.Readback(before);
        state.Set("items.native_0.order", 2); state.Set("items.native_1.order", 1);
        var submitted = state.Capture();
        state.Set("items.native_0.order", 1); state.Set("items.native_1.order", 2);
        state.Readback(JsonNode.Parse("""{"items.native_0.order":2,"items.native_1.order":1,"showLyricTranslation":false}""")!.AsObject(), submitted);
        Assert.Equal(1, state.Number("items.native_0.order"));
        Assert.Equal(2, state.Number("items.native_1.order"));
        Assert.Equal(2, state.Pending.Count);
        state.Set("showLyricTranslation", true);
        var command = state.Capture();
        state.Readback(before, command, preservePending: true);
        Assert.True(state.Bool("showLyricTranslation"));
        state.Readback(JsonNode.Parse("""{"items.native_0.order":1,"items.native_1.order":2,"showLyricTranslation":true}""")!.AsObject(), state.Capture());
        Assert.Empty(state.Pending);
    }
}
