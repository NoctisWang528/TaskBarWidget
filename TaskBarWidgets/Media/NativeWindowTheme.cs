using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Drawing = System.Drawing;
using WpfColor = System.Windows.Media.Color;

namespace TaskbarLyrics.App;

internal static class NativeWindowTheme
{
    private static readonly WpfColor DarkBackground = WpfColor.FromRgb(10, 10, 10);
    private static readonly WpfColor DarkForeground = WpfColor.FromRgb(250, 250, 250);
    private static readonly WpfColor LightBackground = WpfColor.FromRgb(250, 250, 250);
    private static readonly WpfColor LightForeground = WpfColor.FromRgb(24, 24, 27);

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

    public static void Apply(Window window, WebView2 webView, bool useMica = false)
    {
        var isLight = IsLight;
        var background = isLight ? LightBackground : DarkBackground;
        var foreground = isLight ? LightForeground : DarkForeground;

        window.Background = CreateBrush(background);
        window.Foreground = CreateBrush(foreground);
        webView.DefaultBackgroundColor = useMica ? Drawing.Color.Transparent : Drawing.Color.FromArgb(
            byte.MaxValue,
            background.R,
            background.G,
            background.B);

        if (webView.CoreWebView2 is not null)
        {
            webView.CoreWebView2.Profile.PreferredColorScheme = isLight
                ? CoreWebView2PreferredColorScheme.Light
                : CoreWebView2PreferredColorScheme.Dark;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } compositionTarget })
        {
            compositionTarget.BackgroundColor = background;
        }

        var darkMode = isLight ? 0 : 1;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmWindowAttributeUseImmersiveDarkMode,
            ref darkMode,
            Marshal.SizeOf<int>());

        var cornerPreference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmWindowAttributeWindowCornerPreference,
            ref cornerPreference,
            Marshal.SizeOf<int>());

        if (useMica)
        {
            const int styleIndex = -16;
            const long nativeFrame = 0x00C00000L | 0x00040000L; // WS_CAPTION | WS_THICKFRAME preserve DWM shadow.
            var style = GetWindowLongPtr(hwnd, styleIndex).ToInt64();
            if ((style & nativeFrame) != nativeFrame)
            {
                _ = SetWindowLongPtr(hwnd, styleIndex, (IntPtr)(style | nativeFrame));
                _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
            }
            var nonClientRendering = 2; // DWMNCRP_ENABLED.
            _ = DwmSetWindowAttribute(hwnd, 2, ref nonClientRendering, Marshal.SizeOf<int>());
            var backdrop = SystemParameters.HighContrast ? 1 : 2; // DWMSBT_NONE / DWMSBT_MAINWINDOW (Mica).
            var result = DwmSetWindowAttribute(hwnd, 38, ref backdrop, Marshal.SizeOf<int>());
            var micaEnabled = result >= 0 && !SystemParameters.HighContrast;
            var margins = new Margins { Left = micaEnabled ? -1 : 0, Right = micaEnabled ? -1 : 0,
                Top = micaEnabled ? -1 : 0, Bottom = micaEnabled ? -1 : 0 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            window.Background = micaEnabled ? System.Windows.Media.Brushes.Transparent : CreateBrush(background);
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                target.BackgroundColor = micaEnabled ? Colors.Transparent : background;
        }
    }

    private static SolidColorBrush CreateBrush(WpfColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private const int DwmWindowAttributeUseImmersiveDarkMode = 20;
    private const int DwmWindowAttributeWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
