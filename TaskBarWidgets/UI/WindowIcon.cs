using Microsoft.UI.Xaml;

namespace TaskBarWidgets.UI;

internal static class WindowIcon
{
    internal static void Apply(Window window) =>
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TaskBarWidgets.ico"));
}
