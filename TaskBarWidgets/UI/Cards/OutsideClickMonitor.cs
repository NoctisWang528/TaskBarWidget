using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TaskBarWidgets.UI.Cards;

// The low-level callback only snapshots a button-down and queues it. It never
// consumes input or performs XAML, pipe, file, or audio work on the hook path.
internal sealed class OutsideClickMonitor : IDisposable
{
    internal enum Button { Left, Right, Middle, Extra }
    internal readonly record struct Click(int X, int Y, Button MouseButton, nint Target, nint Root,
        nint RootOwner, uint ProcessId, bool IsPopup, bool FlyoutWasOpen, bool IsEntryTarget);

    private readonly DispatcherQueue _dispatcher;
    private readonly Func<bool> _flyoutOpen;
    private readonly HookProc _callback;
    private Action<Click>? _clicked;
    private nint _hook, _entryWindow;
    private int _session;

    internal OutsideClickMonitor(DispatcherQueue dispatcher, Func<bool> flyoutOpen)
    {
        _dispatcher = dispatcher;
        _flyoutOpen = flyoutOpen;
        _callback = OnMouse;
    }

    internal void Start(nint entryWindow, Action<Click> clicked)
    {
        Stop();
        _entryWindow = entryWindow;
        _clicked = clicked;
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0); // WH_MOUSE_LL
        if (_hook == 0) CardLifecycleLog.Write($"click-monitor-install-failed error={Marshal.GetLastWin32Error()}");
        else CardLifecycleLog.Write("click-monitor-started");
    }

    internal void Stop()
    {
        _session++;
        _clicked = null;
        var hook = _hook;
        _hook = 0;
        if (hook == 0) return;
        var removed = UnhookWindowsHookEx(hook);
        CardLifecycleLog.Write(removed ? "click-monitor-stopped" : $"click-monitor-remove-failed error={Marshal.GetLastWin32Error()}");
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(0, code, message, data);
        Button? button = (int)message switch { 0x0201 => Button.Left, 0x0204 => Button.Right,
            0x0207 => Button.Middle, 0x020B => Button.Extra, _ => null };
        if (_hook != 0 && button is { } pressed)
        {
            var info = Marshal.PtrToStructure<MouseInfo>(data);
            var target = WindowFromPoint(info.Point);
            var root = GetAncestor(target, 2); // GA_ROOT
            GetWindowThreadProcessId(target, out var process);
            var click = new Click(info.Point.X, info.Point.Y, pressed, target, root, GetAncestor(target, 3), process,
                (GetWindowLongPtr(root, -16).ToInt64() & 0x80000000L) != 0, _flyoutOpen(),
                _entryWindow != 0 && (target == _entryWindow || IsChild(_entryWindow, target)));
            var session = _session;
            _dispatcher.TryEnqueue(() =>
            {
                if (_hook != 0 && session == _session) _clicked?.Invoke(click);
            });
        }
        return CallNextHookEx(0, code, message, data);
    }

    public void Dispose() => Stop();

    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInfo
    { public Point Point; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}
