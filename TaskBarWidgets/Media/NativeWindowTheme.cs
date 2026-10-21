namespace TaskbarLyrics.App;

// Tracks the user's tool-window theme choice. The media helper no longer owns a
// window or a WebView2 surface, so only the shared mode lives here; WinUI applies
// the actual appearance from the same setting.
internal static class NativeWindowTheme
{
    private static ToolWindowTheme _mode = ToolWindowTheme.System;

    public static event EventHandler? ThemeChanged;

    public static bool FollowsSystem => _mode == ToolWindowTheme.System;

    public static bool IsLight => _mode switch
    {
        ToolWindowTheme.Light => true,
        ToolWindowTheme.Dark => false,
        _ => App.IsApplicationUsingLightTheme()
    };

    public static void SetMode(ToolWindowTheme mode)
    {
        var normalized = Enum.IsDefined(mode)
            ? mode
            : ToolWindowTheme.System;
        if (_mode == normalized)
        {
            return;
        }

        _mode = normalized;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void RefreshSystemTheme()
    {
        if (FollowsSystem)
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
