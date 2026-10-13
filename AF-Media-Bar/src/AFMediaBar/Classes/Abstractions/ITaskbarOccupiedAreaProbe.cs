using AFMediaBar.Classes.Interop;
using AFMediaBar.Classes.Models.Layout;

namespace AFMediaBar.Classes.Abstractions;

/// <summary>
/// 在后台探测 Windows 任务栏占用区域的基础设施边界。
/// Infrastructure boundary that probes Windows taskbar occupancy in the background.
/// </summary>
public interface ITaskbarOccupiedAreaProbe
{
    /// <summary>
    /// 排队一次异步平台探测，完成或被新请求替换时调用一个回调；释放时丢弃未完成请求。
    /// Queues an asynchronous probe and invokes one callback on completion or replacement; disposal discards unfinished requests.
    /// </summary>
    /// <param name="taskbarHandle">任务栏窗口句柄 / Taskbar window handle.</param>
    /// <param name="taskbarRect">任务栏屏幕矩形 / Taskbar screen rectangle.</param>
    /// <param name="orientation">布局主轴方向 / Layout primary-axis orientation.</param>
    /// <param name="dpiScale">任务栏 DPI 缩放 / Taskbar DPI scale.</param>
    /// <param name="edgePaddingPixels">媒体栏边缘留白 / Media-bar edge padding.</param>
    /// <param name="succeeded">成功时接收安全主轴区间的回调 / Callback receiving safe primary-axis ranges on success.</param>
    /// <param name="failed">线程启动、探测失败或请求被替换时的回调 / Callback invoked on worker startup failure, probe failure or request replacement.</param>
    void Start(
        IntPtr taskbarHandle,
        NativeMethods.RECT taskbarRect,
        LayoutOrientation orientation,
        double dpiScale,
        int edgePaddingPixels,
        Action<IReadOnlyList<TaskbarPrimaryRange>> succeeded,
        Action failed);
}
