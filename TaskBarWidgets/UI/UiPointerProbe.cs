using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.UI.Settings;
using Windows.Foundation;

namespace TaskBarWidgets.UI;

// Only targets the caller's already-offscreen HWND. Never uses SendInput,
// SetCursorPos, SetForegroundWindow, Window.Activate or a native/media host.
internal static class UiPointerProbe
{
    internal static async Task<object> RunAsync(SettingsWindow window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var position = window.AppWindow.Position; var size = window.AppWindow.Size;
        if (position.X + size.Width >= -20000 || position.Y + size.Height >= -20000) throw new InvalidOperationException("Pointer probe requires a strictly offscreen window.");
        var content = (FrameworkElement)window.Content;
        var source = Walk(content).OfType<PointerDragButton>().FirstOrDefault(button => button.Tag is string id && !id.StartsWith("source:") && button.ActualWidth > 0);
        var lanes = Walk(content).OfType<Border>().Where(border => border.Tag is string tag && tag.StartsWith("ModuleLane:")).ToArray();
        if (source?.Tag is not string sourceId || lanes.Length < 2) throw new InvalidOperationException("Pointer probe requires two realized module lanes.");
        var before = window.PointerProbeLayout;
        var row = before[$"items.{sourceId}.row"]?.GetValue<int>() ?? 0;
        var targetRow = row == 0 ? 1 : 0;
        var target = lanes.First(lane => Equals(lane.Tag, "ModuleLane:" + targetRow));
        var start = source.TransformToVisual(content).TransformPoint(new Point(source.ActualWidth / 2, source.ActualHeight / 2));
        var end = target.TransformToVisual(content).TransformPoint(new Point(15, target.ActualHeight / 2));
        var foreground = GetForegroundWindow(); GetCursorPos(out var cursor);
        if ((GetAsyncKeyState(1) & 0x8000) != 0) return new { kind = "modulePointerMessages", available = false, reason = "userDesktopActivity", detail = "Mouse button is currently pressed; safe probe skipped." };
        var candidates = new List<nint>();
        EnumChildWindows(handle, (child, _) => {
            GetWindowThreadProcessId(child, out var process);
            var name = new StringBuilder(256); GetClassName(child, name, name.Capacity);
            if (process == Environment.ProcessId && name.ToString().Contains("Content", StringComparison.Ordinal)) candidates.Add(child);
            return true;
        }, 0);
        candidates.Add(handle);
        var first = window.PointerGestureEntries.Length;
        object UserActivity() => new { kind = "modulePointerMessages", available = false, reason = "userDesktopActivity", detail = "Desktop focus/cursor changed; the attempt was stopped without moving the cursor or restoring focus.", entries = window.PointerGestureEntries.Skip(first).ToArray() };
        try {
            foreach (var receiver in candidates) {
                if (DesktopChanged(foreground, cursor)) return UserActivity();
                PostMouse(receiver, handle, content, 0x0200, 0, start);
                PostMouse(receiver, handle, content, 0x0201, 1, start);
                await Task.Delay(55);
                if (DesktopChanged(foreground, cursor)) return UserActivity();
                PostMouse(receiver, handle, content, 0x0200, 1, new Point(start.X + 12, start.Y));
                await Task.Delay(45);
                if (DesktopChanged(foreground, cursor)) return UserActivity();
                PostMouse(receiver, handle, content, 0x0200, 1, end);
                await Task.Delay(55);
                if (DesktopChanged(foreground, cursor)) return UserActivity();
                PostMouse(receiver, handle, content, 0x0202, 0, end);
                await Task.Delay(150); content.UpdateLayout();
                if (DesktopChanged(foreground, cursor)) return UserActivity();
                if (window.PointerGestureEntries.Skip(first).Any(entry => entry.Contains(" press ", StringComparison.Ordinal))) break;
            }
            var entries = window.PointerGestureEntries.Skip(first).ToArray();
            if (!entries.Any(entry => entry.Contains(" press ", StringComparison.Ordinal))) return new {
                kind = "modulePointerMessages", available = false,
                reason = entries.Any(entry => entry.Contains("capture-failed", StringComparison.Ordinal))
                    ? "Posted press reached CapturePointer, but WinUI returned false for contact/capture in the inactive offscreen HWND; no actual drag pass is claimed."
                    : "PostMessage succeeded, but WinUI did not promote posted WM_MOUSE messages into PointerPressed for the inactive offscreen HWND; no actual drag pass is claimed.", receiversTried = candidates.Count, entries };
            if (!entries.Any(entry => entry.Contains(" drag-start ", StringComparison.Ordinal))) return new {
                kind = "modulePointerMessages", available = false,
                reason = "Posted press was received, but the inactive WinUI input source did not deliver a held-button move; the complete pointer path remains unverified.", entries };
            var applied = entries.Count(entry => entry.Contains(" module-applied ", StringComparison.Ordinal));
            var dispatches = entries.Count(entry => entry.Contains(" drop-dispatch ", StringComparison.Ordinal));
            var after = window.PointerProbeLayout;
            if (applied != 1 || dispatches != 1 || JsonNode.DeepEquals(before, after) || after[$"items.{sourceId}.row"]?.GetValue<int>() != targetRow)
                throw new InvalidOperationException("Actual posted pointer path failed to commit exactly one module move.\n" + string.Join("\n", entries));
            return new { kind = "modulePointerMessages", available = true, actualPointerEvents = true, sourceId, targetRow, applied, dispatches, entries };
        }
        finally { window.CancelPointerProbe(); AssertNotForeground(); }
    }
    private static bool DesktopChanged(nint foreground, NativePoint cursor)
    {
        AssertNotForeground();
        GetCursorPos(out var after);
        return GetForegroundWindow() != foreground || cursor.X != after.X || cursor.Y != after.Y;
    }
    private static void AssertNotForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        if (process == Environment.ProcessId) throw new InvalidOperationException("Pointer probe unexpectedly activated one of its own windows.");
    }
    private static void PostMouse(nint receiver, nint owner, FrameworkElement content, uint message, nuint buttons, Point point)
    {
        var scale = content.XamlRoot.RasterizationScale;
        var native = new NativePoint { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) };
        ClientToScreen(owner, ref native); ScreenToClient(receiver, ref native);
        if (!PostMessage(receiver, message, buttons, (nint)((native.Y & 0xffff) << 16 | (native.X & 0xffff)))) throw new InvalidOperationException("Own-window mouse message could not be posted.");
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject item)
    {
        yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++) foreach (var child in Walk(VisualTreeHelper.GetChild(item, index))) yield return child;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate bool EnumWindowProc(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint handle, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint handle, uint message, nuint wparam, nint lparam);
}
