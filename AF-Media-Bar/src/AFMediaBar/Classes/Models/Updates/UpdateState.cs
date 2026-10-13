namespace AFMediaBar.Classes.Models.Updates;

/// <summary>
/// 更新下载器的阶段。
///
/// <see cref="ManualOnly"/> 不是失败：清单有效、确实有新版本，但它没有提供带哈希的安装包直链，
/// 因此程序只能打开下载页。把它与 <see cref="Failed"/> 分开，用户才不会以为网络出了问题。
/// Phases of the update downloader.
///
/// <see cref="ManualOnly"/> is not a failure: the manifest is valid and a newer version exists, but it offers no
/// hashed installer link, so the application can only open the download page. Keeping it apart from
/// <see cref="Failed"/> stops the user from blaming the network for a publishing gap.
/// </summary>
public enum UpdatePhase
{
    /// <summary>尚未检查或已完成一次检查但还没有结论。/ Not checked yet.</summary>
    Idle = 0,

    /// <summary>正在读取版本清单。/ Reading the version manifest.</summary>
    Checking = 1,

    /// <summary>当前版本已是最新。/ The current version is the newest one.</summary>
    UpToDate = 2,

    /// <summary>发现新版本，尚未开始下载。/ A newer version exists and downloading has not started.</summary>
    Available = 3,

    /// <summary>正在下载安装包。/ Downloading the installer.</summary>
    Downloading = 4,

    /// <summary>正在校验已下载的安装包。/ Verifying the downloaded installer.</summary>
    Verifying = 5,

    /// <summary>安装包已通过校验，等待安装。/ The installer passed verification and is waiting to be applied.</summary>
    Ready = 6,

    /// <summary>用户已跳过该版本。/ The user skipped this version.</summary>
    Skipped = 7,

    /// <summary>有新版本但清单没有可自动安装的安装包。/ A newer version exists but the manifest has no installable package.</summary>
    ManualOnly = 8,

    /// <summary>检查、下载或校验失败。/ The check, download or verification failed.</summary>
    Failed = 9
}

/// <summary>
/// 更新下载器对外发布的不可变状态。
///
/// 所有界面（设置页、托盘菜单）都只读取这一个对象：状态机在服务内部流转，界面不需要知道任何转换规则，
/// 也不会出现"设置页说正在下载、托盘菜单说已是最新"的分裂。
/// Immutable state published by the update downloader.
///
/// Every surface (settings page, tray menu) reads this single object: the state machine lives inside the service,
/// so no surface needs to know the transitions and the two can never disagree about what is happening.
/// </summary>
/// <param name="Phase">当前阶段。/ Current phase.</param>
/// <param name="CurrentVersion">正在运行的程序版本，已按显示格式去掉尾部零。/ Running application version, formatted without trailing zeros.</param>
/// <param name="Manifest">最近一次成功读取到的清单；没有时为 null。/ Most recently parsed manifest, or null.</param>
/// <param name="ProgressPercent">下载或校验进度（0–100）。/ Download or verification progress (0–100).</param>
/// <param name="FailureReason">失败原因；非失败阶段为 null。/ Failure reason, or null outside a failure.</param>
/// <param name="ActiveSource">当前使用的下载渠道；未下载时为 null。/ Download source in use, or null when nothing is being downloaded.</param>
/// <param name="IsMandatory">清单是否要求必须更新。/ Whether the manifest requires this update.</param>
/// <param name="IsSkipped">用户是否跳过了清单中的版本。/ Whether the user skipped the manifest version.</param>
/// <param name="InstallBlockedReason">安装被阻止的原因（便携版或其它实例在运行）；可安装时为 null。/ Reason installing is blocked (portable copy or another running instance), or null.</param>
public sealed record UpdateState(
    UpdatePhase Phase,
    string CurrentVersion,
    UpdateManifest? Manifest,
    double ProgressPercent,
    string? FailureReason,
    UpdateDownloadSource? ActiveSource,
    bool IsMandatory,
    bool IsSkipped,
    string? InstallBlockedReason)
{
    /// <summary>清单声明的可用版本；没有清单时为 null。/ Version offered by the manifest, or null.</summary>
    public string? AvailableVersion => Manifest?.Version;

    /// <summary>是否正在下载或校验（两类进度共用同一个进度条）。/ Whether a download or verification is in progress.</summary>
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Verifying;

    /// <summary>是否已有通过校验、等待安装的安装包。/ Whether a verified installer is waiting to be applied.</summary>
    public bool IsReady => Phase == UpdatePhase.Ready;

    /// <summary>安装当前是否被阻止。/ Whether installing is currently blocked.</summary>
    public bool IsInstallBlocked => InstallBlockedReason is not null;

    /// <summary>创建初始状态：尚未检查，当前版本已知。/ Creates the initial state: nothing checked yet, current version known.</summary>
    /// <param name="currentVersion">正在运行的版本。/ Running version.</param>
    public static UpdateState Initial(string currentVersion) =>
        new(UpdatePhase.Idle, currentVersion, null, 0d, null, null, false, false, null);
}
