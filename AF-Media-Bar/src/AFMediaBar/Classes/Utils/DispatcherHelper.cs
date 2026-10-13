using System.Windows;
using System.Windows.Threading;

namespace AFMediaBar.Classes.Utils;

/// <summary>
/// 把动作送回指定的调度器执行。
///
/// 设置变化、服务回调这类事件可能从后台线程发出，而订阅者里有绑定到界面的集合：跨线程改
/// <c>ObservableCollection</c> 会让 WPF 的 <c>CollectionView</c> 抛
/// “该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改”。
/// 这里给出唯一一份判定（已在目标线程就直接执行、否则排队、正在关闭就丢弃），避免每个订阅者各写一遍。
/// Runs an action on the given dispatcher.
///
/// Events such as a settings change or a service callback can be raised from a background thread, while their subscribers include
/// collections bound to the interface: mutating an <c>ObservableCollection</c> across threads makes WPF's <c>CollectionView</c>
/// throw "this type of CollectionView does not support changes to its SourceCollection from a thread different from the Dispatcher
/// thread". This keeps one copy of that decision — run directly when already on the target thread, queue otherwise, drop while
/// shutting down — instead of one per subscriber.
/// </summary>
public static class DispatcherHelper
{
    /// <summary>
    /// 在调度器上执行动作：已在目标线程就直接执行，否则排队；调度器为空或正在关闭时丢弃。
    /// Runs the action on the dispatcher: directly when already on the target thread, queued otherwise, and dropped when the
    /// dispatcher is missing or shutting down.
    /// </summary>
    /// <param name="dispatcher">目标调度器，可为 null（此时直接执行）。/ Target dispatcher, may be null, in which case the action runs directly.</param>
    /// <param name="action">要执行的动作。/ Action to run.</param>
    /// <param name="priority">排队优先级，默认 <see cref="DispatcherPriority.Normal"/>。/ Queue priority, <see cref="DispatcherPriority.Normal"/> by default.</param>
    public static void Run(Dispatcher? dispatcher, Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        dispatcher.BeginInvoke(action, priority);
    }

    /// <summary>当前应用的 UI 调度器，取不到时退回当前线程的调度器。/ The application's UI dispatcher, falling back to the current thread's when it cannot be read.</summary>
    public static Dispatcher Current => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
}
