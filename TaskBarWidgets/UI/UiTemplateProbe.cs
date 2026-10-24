using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using TaskBarWidgets.UI.Cards;
using Windows.Graphics;

namespace TaskBarWidgets.UI;

// Exercise the shipped application's lazy template loader without a native/media
// host, activation, taskbar button, audio operation, or on-screen window.
internal static class UiTemplateProbe
{
    internal static async Task RunAsync(Application app, string[] args)
    {
        SettingsWindow? settings = null;
        using var connection = new UiConnection("TaskBarWidgets.UI.template-probe.disconnected");
        var before = GetForegroundWindow();
        var activated = false;
        void TrackActivation(Window window, bool allowCodeActivation = false) => window.Activated += (_, eventArgs) => {
            if (eventArgs.WindowActivationState == WindowActivationState.PointerActivated ||
                eventArgs.WindowActivationState == WindowActivationState.CodeActivated && !allowCodeActivation)
                activated = true;
        };
        var checks = new List<object>();
        object? layoutChecks = null;
        object? weatherControls = null;
        object? offsetSettings = null;
        object? reorderControls = null;
        string? error = null;
        try
        {
            settings = new SettingsWindow(connection);
            // Opening the in-process overlay drawer raises CodeActivated even for
            // a WS_EX_NOACTIVATE probe HWND. Pointer activation and the final
            // foreground-process check still verify that the probe stays hidden.
            TrackActivation(settings, allowCodeActivation: true);
            settings.AppWindow.IsShownInSwitchers = false;
            ShowOffscreen(settings, 1180, 820);
            await Task.Delay(400);
            var content = (FrameworkElement)settings.Content;
            content.UpdateLayout();
            if (!content.IsLoaded || content.ActualWidth <= 0) throw new InvalidOperationException("Settings templates were not realized.");
            checks.Add(new { surface = "settings", width = content.ActualWidth, loaded = content.IsLoaded });
            var fixtureIndex = Array.IndexOf(args, "--fixture");
            if (fixtureIndex >= 0 && fixtureIndex + 1 < args.Length)
                layoutChecks = await UiLayoutProbe.RunAsync(settings, args[fixtureIndex + 1], Path.Combine(AppContext.BaseDirectory, "..", "ui-layout-check"));
            if (layoutChecks is not null)
            {
                reorderControls = await SettingsWindow.ProbeReorderControlsAsync(settings);
                offsetSettings = await OffsetSettingsProbe.RunAsync(settings);
            }
            weatherControls = await SettingsWindow.ProbeWeatherControlsAsync();
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess);
            if (activated || foregroundProcess == Environment.ProcessId) throw new InvalidOperationException("Probe unexpectedly activated one of its windows.");
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            try { settings?.Shutdown(); }
            catch (Exception exception) { error = (error ?? "") + "\nSettings cleanup: " + exception; }
            var resultIndex = Array.IndexOf(args, "--result");
            var result = resultIndex >= 0 && resultIndex + 1 < args.Length ? args[resultIndex + 1] : Path.Combine(AppContext.BaseDirectory, "template-probe.json");
            var winUiModule = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                .FirstOrDefault(module => string.Equals(module.ModuleName, "Microsoft.ui.xaml.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
            File.WriteAllText(result, JsonSerializer.Serialize(new { success = error is null, error, runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(), winUiModule, checks, weatherControls, offsetSettings, reorderControls, layoutChecks, probeActivated = activated, externalForegroundChanged = GetForegroundWindow() != before }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = error is null ? 0 : 1;
            app.Exit();
        }
    }
    internal static void ShowOffscreen(Window window, int width, int height)
    {
        PositionOffscreen(window, width, height);
        window.AppWindow.Show(false); // Never calls Window.Activate or requests focus.
        AssertOffscreen(window);
    }
    private static void PositionOffscreen(Window window, int width, int height)
    {
        PreventProbeActivation(window);
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, width, height));
    }

    internal static void PreventProbeActivation(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // Automation peers may ask WinUI to focus a control. Isolate test HWNDs
        // at the OS boundary as well as using Show(false); production is unchanged.
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, (nint)(style | 0x08000000L)); // WS_EX_NOACTIVATE
    }
    private static void AssertOffscreen(Window window)
    {
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        if (position.X + size.Width >= -20000 || position.Y + size.Height >= -20000)
        {
            window.AppWindow.Hide();
            throw new InvalidOperationException("Probe window did not stay offscreen.");
        }
    }
    internal static void AssertOwnedWindowsOffscreen()
    {
        string? error = null;
        EnumWindows((handle, _) => {
            GetWindowThreadProcessId(handle, out var process);
            if (process == Environment.ProcessId && IsWindowVisible(handle) && GetWindowRect(handle, out var bounds) &&
                (bounds.Right >= -20000 || bounds.Bottom >= -20000))
            {
                error = $"Probe popup escaped offscreen bounds: {bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}.";
                return false;
            }
            return true;
        }, 0);
        if (error is not null) throw new InvalidOperationException(error);
    }
    private delegate bool EnumWindowCallback(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowBounds bounds);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
