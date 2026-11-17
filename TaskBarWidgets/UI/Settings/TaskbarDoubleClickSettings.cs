using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    private void RenderTaskbarDoubleClick(IReadOnlyList<JsonObject> fields)
    {
        const string ActionKey = "taskbar.double_click_action", PathKey = "taskbar.double_click_exe";
        var section = Section("任务栏双击");
        var custom = new StackPanel { Spacing = 8 };
        void UpdatePathVisibility() => custom.Visibility = _native.Number(ActionKey, 7) == 5 ? Visibility.Visible : Visibility.Collapsed;
        var action = fields.First(field => Text(field["key"]) == ActionKey);
        section.Children.Add(FieldEditor(action, _native, UpdatePathVisibility));
        var path = new TextBox { Header = "应用程序路径", Text = _native.Text(PathKey), MaxLength = 1024, PlaceholderText = "选择 .exe 或 .com 文件" };
        AutomationProperties.SetAutomationId(path, "setting-" + PathKey);
        path.TextChanged += (_, _) => { if (!_refreshingControls) { _native.Set(PathKey, path.Text); Changed(); } };
        RegisterSettingReadback(path, _native, PathKey, () => path.Text = _native.Text(PathKey));
        custom.Children.Add(path);
        var browse = ActionButton("选择程序…", async () => {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".exe"); picker.FileTypeFilter.Add(".com");
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !_closed) path.Text = file.Path;
        });
        custom.Children.Add(browse);
        RegisterSettingReadback(custom, _native, ActionKey, UpdatePathVisibility);
        UpdatePathVisibility();
        section.Children.Add(custom);
        section.Children.Add(Copy("启用双击后，单击最多等待 250 毫秒。"));
    }
}
