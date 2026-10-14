using System.Runtime.InteropServices;
namespace TaskbarLyrics.App;
internal static class TaskbarPlacementService {
    internal static TaskbarDisplayMetrics ConvertPhysicalDisplayMetrics(
        TaskbarNativeMethods.NativeRect monitor,
        TaskbarNativeMethods.NativeRect workArea,
        double pixelsPerDip)
    {
        pixelsPerDip = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        return new TaskbarDisplayMetrics(
            monitor.Left / pixelsPerDip,
            monitor.Top / pixelsPerDip,
            (monitor.Right - monitor.Left) / pixelsPerDip,
            (monitor.Bottom - monitor.Top) / pixelsPerDip,
            workArea.Left / pixelsPerDip,
            workArea.Top / pixelsPerDip,
            (workArea.Right - workArea.Left) / pixelsPerDip,
            (workArea.Bottom - workArea.Top) / pixelsPerDip);
    }

    internal static TaskbarDisplayMetrics ConvertPhysicalDisplayMetrics(
        NativeRect monitor,
        NativeRect workArea,
        double pixelsPerDip) =>
        ConvertPhysicalDisplayMetrics(
            new TaskbarNativeMethods.NativeRect(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom),
            new TaskbarNativeMethods.NativeRect(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom),
            pixelsPerDip);

    internal static TaskbarBounds ResolveTaskbarBounds(TaskbarDisplayMetrics display)
    {
        const double defaultThickness = 48;
        const double edgeTolerance = 0.5;
        if (display.WorkAreaBottom < display.Bottom - edgeTolerance)
        {
            return new TaskbarBounds(
                display.Left,
                display.WorkAreaBottom,
                display.Width,
                Math.Max(defaultThickness, display.Bottom - display.WorkAreaBottom));
        }

        if (display.WorkAreaTop > display.Top + edgeTolerance)
        {
            return new TaskbarBounds(
                display.Left,
                display.Top,
                display.Width,
                Math.Max(defaultThickness, display.WorkAreaTop - display.Top));
        }

        if (display.WorkAreaLeft > display.Left + edgeTolerance)
        {
            return new TaskbarBounds(
                display.Left,
                display.Top,
                Math.Max(defaultThickness, display.WorkAreaLeft - display.Left),
                display.Height);
        }

        if (display.WorkAreaRight < display.Right - edgeTolerance)
        {
            return new TaskbarBounds(
                display.WorkAreaRight,
                display.Top,
                Math.Max(defaultThickness, display.Right - display.WorkAreaRight),
                display.Height);
        }

        return new TaskbarBounds(
            display.Left,
            display.Bottom - defaultThickness,
            display.Width,
            defaultThickness);
    }

}
internal readonly record struct TaskbarDisplayMetrics(
    double Left,
    double Top,
    double Width,
    double Height,
    double WorkAreaLeft,
    double WorkAreaTop,
    double WorkAreaWidth,
    double WorkAreaHeight)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public double WorkAreaRight => WorkAreaLeft + WorkAreaWidth;

    public double WorkAreaBottom => WorkAreaTop + WorkAreaHeight;
}

internal readonly record struct TaskbarBounds(double Left, double Top, double Width, double Height)
{
    public double CenterY => Top + (Height / 2);
}

internal readonly record struct TaskbarNativeBounds(int Left, int Top, int Width, int Height);

internal static class TaskbarNativeMethods
{
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_ASYNCWINDOWPOS = 0x4000;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}
