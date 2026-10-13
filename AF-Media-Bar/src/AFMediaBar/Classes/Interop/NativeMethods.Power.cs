// 电源、空闲与进程内存相关的原生互操作声明（后台剪枝用）。
// Native interop declarations for power, idle, and process memory, used by the background pruner.
//
// 单独一个文件而不是继续塞进 NativeMethods.cs：那一份已经是"任务栏与窗口"的清单，而这里声明的是"系统会在什么时候告诉我该省电了"，
// 两者改动的原因不同。
// This lives in its own file instead of growing NativeMethods.cs, which is already the taskbar-and-window list, while these declarations answer
// a different question: when does the system tell me it is time to save power.
using System.Runtime.InteropServices;

namespace AFMediaBar.Classes.Interop;

/// <summary>
/// AFMediaBar 使用的电源与进程内存原生互操作常量、结构与函数。
/// Power and process-memory interop constants, structures, and functions used by AFMediaBar.
/// </summary>
public static partial class NativeMethods
{
    // ── 电源广播 ──────────────────────────────────────────────────────────────
    /// <summary>电源管理广播消息。/ The power-management broadcast message.</summary>
    public const int WM_POWERBROADCAST = 0x0218;

    /// <summary>系统即将睡眠，应用此时不应启动新工作。/ The system is about to suspend, and an application must not start new work now.</summary>
    public const int PBT_APMSUSPEND = 0x0004;

    /// <summary>系统已从睡眠自动恢复。/ The system resumed automatically from suspend.</summary>
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    /// <summary>用户主动唤醒后的恢复通知。/ The resume notification that follows a user-initiated wake.</summary>
    public const int PBT_APMRESUMESUSPEND = 0x0007;

    /// <summary>某项电源设置的取值发生变化（本应用只关心显示器开关）。/ A power setting changed; this app only cares about the display state.</summary>
    public const int PBT_POWERSETTINGCHANGE = 0x8013;

    /// <summary>把电源设置通知投递到窗口句柄（而不是服务回调）。/ Delivers power-setting notifications to a window handle instead of a service callback.</summary>
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    /// <summary>
    /// 显示器开关状态对应的电源设置 GUID（`GUID_CONSOLE_DISPLAY_STATE`，取值为 0 关闭、1 打开、2 变暗）。
    /// The power-setting GUID for the display state (`GUID_CONSOLE_DISPLAY_STATE`; the payload is 0 off, 1 on, 2 dimmed).
    /// </summary>
    public static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    /// <summary>显示器已关闭。/ The display is off.</summary>
    public const uint ConsoleDisplayStateOff = 0;

    /// <summary>显示器已打开。/ The display is on.</summary>
    public const uint ConsoleDisplayStateOn = 1;

    /// <summary>显示器变暗（屏保等），按"用户暂时不在"处理。/ The display is dimmed, as by a screen saver, which also means the user is away for now.</summary>
    public const uint ConsoleDisplayStateDimmed = 2;

    /// <summary>注册电源设置通知，返回的句柄必须在退出前注销。/ Registers for power-setting notifications; the returned handle has to be unregistered before exit.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, uint flags);

    /// <summary>注销电源设置通知。/ Unregisters a power-setting notification.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    /// <summary>
    /// 电源设置变化通知的载荷：设置 GUID、新取值长度与取值本身。
    /// The payload of a power-setting change: the setting GUID, the length of its new value, and the value itself.
    ///
    /// 原生结构里的取值是 `UCHAR Data[1]`；这里直接声明成 `uint`，因为本应用只关心 `GUID_CONSOLE_DISPLAY_STATE`，它的取值恰好是一个 DWORD。
    /// 这样做省掉一次按偏移量的手工读取，布局也与原生结构一致（16 + 4 + 4）。
    /// The native structure declares the value as `UCHAR Data[1]`; it is declared as `uint` here because the only setting this app cares about,
    /// `GUID_CONSOLE_DISPLAY_STATE`, carries exactly one DWORD. That removes a hand-computed offset read while keeping the native layout, 16 + 4 + 4.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PowerBroadcastSetting
    {
        /// <summary>发生变化的设置 GUID。/ The GUID of the setting that changed.</summary>
        public Guid PowerSetting;

        /// <summary>新取值的数据长度（字节）。/ Length in bytes of the new value.</summary>
        public uint DataLength;

        /// <summary>新取值的第一个 DWORD；只有 <see cref="DataLength"/> 至少为 4 时才有效。
        /// The first DWORD of the new value, valid only while <see cref="DataLength"/> is at least four.</summary>
        public uint Data;
    }

    // ── 用户空闲 ──────────────────────────────────────────────────────────────
    /// <summary>最近一次输入的信息。/ Information about the most recent input.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        /// <summary>结构大小，调用前必须填写。/ The size of the structure, which the caller must set.</summary>
        public uint Size;

        /// <summary>最近一次输入的 tick 计数（毫秒），需要用 `Environment.TickCount64` 作差才会得到时长。
        /// The tick count in milliseconds at the last input, which only becomes a duration when subtracted from `Environment.TickCount64`.</summary>
        public uint Time;
    }

    /// <summary>
    /// 读取系统范围内的最近一次输入时间。它返回的是"整个系统"的空闲，而不是本进程的窗口消息，因此锁屏时依然有效。
    /// Reads the system-wide time of the last input. It reports the idle time of the whole system rather than this process's window messages, which
    /// is why it still works while the session is locked.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    // ── 进程内存 ──────────────────────────────────────────────────────────────
    /// <summary>
    /// 清空进程工作集：把可换出的物理页交还系统。数据没有被丢弃，只是重新变成换页文件里的内容，下次访问会重新读回。
    /// Empties the working set, returning pageable physical pages to the system. No data is lost: it becomes pagefile-backed and is read back on
    /// the next access.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool K32EmptyWorkingSet(IntPtr process);

    /// <summary>
    /// 设置进程工作集的下限与上限。传入 `-1, -1` 是系统约定：把工作集剥离到当前可能的最小值，语义上与
    /// <see cref="K32EmptyWorkingSet"/> 一致，两者一起调用能覆盖"API 之后被立即摸回来的少量页"。
    /// Sets the minimum and maximum working-set size of a process. Passing `-1, -1` is the documented convention for trimming the working set to
    /// whatever is possible now; it means the same thing as <see cref="K32EmptyWorkingSet"/>, and calling both covers the few pages that get touched
    /// back immediately after the first call.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

    /// <summary>进程信息类别的 `ProcessMemoryPriority`。/ The `ProcessMemoryPriority` process-information class.</summary>
    public const int ProcessMemoryPriority = 0;

    /// <summary>进程信息类别的 `ProcessPowerThrottling`。/ The `ProcessPowerThrottling` process-information class.</summary>
    public const int ProcessPowerThrottling = 4;

    /// <summary>内存优先级：很低。/ Memory priority: very low.</summary>
    public const uint MemoryPriorityVeryLow = 1;

    /// <summary>内存优先级：低。/ Memory priority: low.</summary>
    public const uint MemoryPriorityLow = 2;

    /// <summary>内存优先级：中（Windows 默认值）。/ Memory priority: medium, the Windows default.</summary>
    public const uint MemoryPriorityMedium = 3;

    /// <summary>内存优先级：低于正常。/ Memory priority: below normal.</summary>
    public const uint MemoryPriorityBelowNormal = 4;

    /// <summary>内存优先级：正常。/ Memory priority: normal.</summary>
    public const uint MemoryPriorityNormal = 5;

    /// <summary>电源节流：限制执行速度（EcoQoS），让调度器把本进程放到效率核与低优先级时间片。
    /// Power throttling: caps the execution speed (EcoQoS) so the scheduler keeps this process on efficiency cores and low-priority slices.</summary>
    public const uint ProcessPowerThrottlingExecutionSpeed = 0x00000001;

    /// <summary>电源节流状态结构的版本号，当前只支持 1。/ The version of the power-throttling state structure; only 1 exists today.</summary>
    public const uint ProcessPowerThrottlingVersion = 1;

    /// <summary>进程内存优先级信息。/ The process memory-priority information.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryPriorityInformation
    {
        /// <summary>优先级取值，见 `MemoryPriority*` 常量。/ The priority value, one of the `MemoryPriority*` constants.</summary>
        public uint MemoryPriority;
    }

    /// <summary>进程电源节流状态。/ The process power-throttling state.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessPowerThrottlingState
    {
        /// <summary>结构版本，必须为 <see cref="ProcessPowerThrottlingVersion"/>。/ The structure version, which must be <see cref="ProcessPowerThrottlingVersion"/>.</summary>
        public uint Version;

        /// <summary>要修改哪些控制项（位掩码）。/ A bitmask of the controls being changed.</summary>
        public uint ControlMask;

        /// <summary>这些控制项的目标状态。/ The desired state of those controls.</summary>
        public uint StateMask;
    }

    /// <summary>设置进程的内存优先级。/ Sets the memory priority of a process.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(
        IntPtr process,
        int processInformationClass,
        ref MemoryPriorityInformation processInformation,
        uint processInformationSize);

    /// <summary>设置进程的电源节流状态。/ Sets the power-throttling state of a process.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(
        IntPtr process,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);
}
