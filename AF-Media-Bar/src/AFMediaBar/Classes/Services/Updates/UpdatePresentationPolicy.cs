using AFMediaBar.Classes.Models.Updates;
using AFMediaBar.Resources;

namespace AFMediaBar.Classes.Services.Updates;

/// <summary>
/// 界面应当提供的主要动作。
/// Primary action a surface should offer.
/// </summary>
public enum UpdatePrimaryAction
{
    /// <summary>当前没有可执行动作。/ Nothing can be done right now.</summary>
    None = 0,

    /// <summary>检查更新（也用于失败后重试）。/ Check for updates, also used to retry after a failure.</summary>
    Check = 1,

    /// <summary>下载并安装更新。/ Download and install the update.</summary>
    Download = 2,

    /// <summary>取消正在进行的下载或校验。/ Cancel an in-flight download or verification.</summary>
    Cancel = 3,

    /// <summary>立即重启并安装。/ Restart and install immediately.</summary>
    InstallAndRestart = 4,

    /// <summary>只能打开下载页手动下载。/ Only manual download is available.</summary>
    OpenDownloadPage = 5
}

/// <summary>
/// 托盘菜单项被点击时应当执行的动作。
///
/// 它与设置页的主要动作刻意分开：托盘菜单只有一行，因此"发现新版本（点击查看）"必须先带用户去看亮点，
/// 而不是在菜单里直接开始一次 70 MB 的下载。
/// Action performed when the tray entry is clicked.
///
/// It is deliberately separate from the settings page's primary action: the tray offers a single line, so
/// "a newer version exists (click to view)" must take the user to the highlights instead of silently starting a
/// 70 MB download from a menu.
/// </summary>
public enum UpdateTrayAction
{
    /// <summary>当前不可点击。/ Nothing can be done right now.</summary>
    None = 0,

    /// <summary>立即检查（也用于失败后重试）。/ Check right away, also used to retry after a failure.</summary>
    Check = 1,

    /// <summary>取消正在进行的下载或校验。/ Cancel an in-flight download or verification.</summary>
    Cancel = 2,

    /// <summary>打开设置窗口的「应用与关于」页。/ Open the "application and about" settings page.</summary>
    OpenUpdatePage = 3,

    /// <summary>立即重启并安装。/ Restart and install immediately.</summary>
    InstallAndRestart = 4
}

/// <summary>
/// 更新状态到界面文案的纯策略。
///
/// 托盘菜单的一项标题和设置页的一行状态都由这里产出：两处必须永远说同一件事，否则用户会看到"托盘说已就绪、
/// 设置页说正在下载"。文案遵循仓库的语域约定——说明当前发生了什么，不用比喻，也不用第二人称。
/// Pure policy turning update state into interface text.
///
/// The tray entry title and the settings-page status both come from here: the two must always say the same thing,
/// otherwise the tray says "ready" while the settings page says "downloading". The wording follows the repository's
/// register — it states what is happening, without metaphors or second-person phrasing.
/// </summary>
public static class UpdatePresentationPolicy
{
    /// <summary>
    /// 设置页里的一行状态文本。
    /// One-line status text for the settings page.
    /// </summary>
    /// <param name="state">当前状态。/ Current state.</param>
    /// <param name="lastCheckUtc">上次检查时间；为 null 时省略该段。/ Time of the last check, omitted when null.</param>
    /// <param name="now">当前时间，仅用于判断是否显示"上次检查"。/ Current time, used only to decide whether to show the last check.</param>
    /// <returns>可直接显示的状态文本。/ Status text ready to display.</returns>
    public static string ResolveStatusText(UpdateState state, DateTimeOffset? lastCheckUtc, DateTimeOffset now)
    {
        var suffix = ResolveCheckSuffix(state.Phase, lastCheckUtc, now);
        var version = state.AvailableVersion;
        return state.Phase switch
        {
            UpdatePhase.Idle => Translations.Get("Update.Status.Idle") + suffix,
            UpdatePhase.Checking => Translations.Get("Update.Status.Checking"),
            UpdatePhase.UpToDate => Translations.Format("Update.Status.UpToDate", state.CurrentVersion) + suffix,
            UpdatePhase.Available =>
                Translations.Format("Update.Status.Available", version, state.CurrentVersion)
                + MandatorySuffix(state)
                + suffix,
            // 下载阶段同时流式计算 SHA-256，因此这里说"下载并校验"：安装前不会再有第二遍读取。
            // The download phase computes the SHA-256 while streaming, hence "download and verify": there is no
            // second pass before installing.
            UpdatePhase.Downloading =>
                Translations.Format("Update.Status.Downloading", FormatPercent(state.ProgressPercent))
                + ChannelSuffix(state),
            UpdatePhase.Verifying => Translations.Format("Update.Status.Verifying", FormatPercent(state.ProgressPercent)),
            UpdatePhase.Ready => ResolveReadyText(state),
            UpdatePhase.Skipped => Translations.Format("Update.Status.Skipped", version, state.CurrentVersion),
            UpdatePhase.ManualOnly =>
                Translations.Format("Update.Status.ManualOnly", version) + MandatorySuffix(state),
            UpdatePhase.Failed =>
                Translations.Format(
                    "Update.Status.Failed",
                    state.FailureReason ?? Translations.Get("Update.Reason.Unknown"))
                + suffix,
            _ => string.Empty
        };
    }

    /// <summary>
    /// 托盘菜单项的标题。它与设置页状态一一对应，只在长短上更适合菜单宽度。
    /// Title of the tray menu entry, mapping one-to-one onto the settings status but sized for a menu.
    /// </summary>
    /// <param name="state">当前状态。/ Current state.</param>
    public static string ResolveTrayHeader(UpdateState state)
    {
        var version = state.AvailableVersion;
        return state.Phase switch
        {
            UpdatePhase.Idle or UpdatePhase.UpToDate => Translations.Get("Update.Tray.Check"),
            UpdatePhase.Checking => Translations.Get("Update.Tray.Checking"),
            UpdatePhase.Downloading => Translations.Format("Update.Tray.Downloading", FormatPercent(state.ProgressPercent)),
            UpdatePhase.Verifying => Translations.Format("Update.Tray.Verifying", FormatPercent(state.ProgressPercent)),
            UpdatePhase.Available => Translations.Format("Update.Tray.Available", version),
            UpdatePhase.Ready => Translations.Format("Update.Tray.Ready", version),
            UpdatePhase.Skipped or UpdatePhase.ManualOnly => Translations.Format("Update.Tray.Available", version),
            UpdatePhase.Failed => Translations.Get("Update.Tray.Failed"),
            _ => Translations.Get("Update.Tray.Check")
        };
    }

    /// <summary>仅在已发现新版本时显示右键菜单入口；失败时只有保留了新版本清单才继续显示。/ Shows the context-menu entry only for a known newer version, including a failed download with its manifest retained.</summary>
    public static bool ShouldShowTrayNotice(UpdateState state) =>
        state.Manifest is not null &&
        UpdateVersionPolicy.IsUpdateAvailable(state.CurrentVersion, state.Manifest.Version) &&
        state.Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Verifying or
            UpdatePhase.Ready or UpdatePhase.ManualOnly or UpdatePhase.Failed;

    /// <summary>
    /// 托盘菜单项当前是否可点击：检查、下载与校验期间不可点击，因为再点一次不会改变正在发生的事。
    /// Whether the tray entry is clickable now: it is not during a check, download or verification, because
    /// clicking again cannot change what is already happening.
    /// </summary>
    /// <param name="state">当前状态。/ Current state.</param>
    public static bool IsTrayHeaderEnabled(UpdateState state) => !state.IsBusy;

    /// <summary>
    /// 当前的主要动作。界面据此决定按钮文案与点击结果。
    ///
    /// 失败之后默认是"再检查一次"，但清单里仍有可用安装包时（也就是下载或校验失败）应当重试的正是下载：
    /// 让用户重新走一遍检查只会得到同一个结论。
    /// Primary action for the current state, which surfaces use for the button label and click result.
    ///
    /// After a failure the default is another check, but when the manifest still offers a usable package — that is,
    /// the download or verification failed — the action worth retrying is the download: asking the user to check
    /// again would only reach the same conclusion.
    /// </summary>
    /// <param name="state">当前状态。/ Current state.</param>
    public static UpdatePrimaryAction ResolvePrimaryAction(UpdateState state) => state.Phase switch
    {
        UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Skipped => UpdatePrimaryAction.Check,
        UpdatePhase.Checking => UpdatePrimaryAction.None,
        UpdatePhase.Available => UpdatePrimaryAction.Download,
        UpdatePhase.Downloading or UpdatePhase.Verifying => UpdatePrimaryAction.Cancel,
        UpdatePhase.Ready => state.IsInstallBlocked ? UpdatePrimaryAction.OpenDownloadPage : UpdatePrimaryAction.InstallAndRestart,
        UpdatePhase.ManualOnly => UpdatePrimaryAction.OpenDownloadPage,
        UpdatePhase.Failed => state.Manifest?.HasInstallablePackage == true
            ? UpdatePrimaryAction.Download
            : UpdatePrimaryAction.Check,
        _ => UpdatePrimaryAction.None
    };

    /// <summary>
    /// 设置窗口导航里的更新提示文案；没有可提示的内容时返回空字符串（调用方据此隐藏该行）。
    ///
    /// 它比设置页的状态行短：导航项的标题栏放不下"发现新版本 v1.2.0（当前 v1.1.1）· 上次检查 3 小时前"。
    /// Wording of the update notice in the settings navigation, or an empty string when there is nothing to show,
    /// which tells the caller to hide the row.
    ///
    /// It is shorter than the settings status line because a navigation item cannot hold
    /// "发现新版本 v1.2.0（当前 v1.1.1）· 上次检查 3 小时前".
    /// </summary>
    /// <param name="state">当前状态。/ Current state.</param>
    public static string ResolveNavigationNoticeText(UpdateState state)
    {
        var version = state.AvailableVersion;
        return state.Phase switch
        {
            UpdatePhase.Available or UpdatePhase.Skipped or UpdatePhase.ManualOnly => Translations.Format("Update.Navigation.Available", version),
            UpdatePhase.Downloading => Translations.Format("Update.Navigation.Downloading", FormatPercent(state.ProgressPercent)),
            UpdatePhase.Verifying => Translations.Get("Update.Navigation.Verifying"),
            UpdatePhase.Ready => Translations.Format("Update.Navigation.Ready", version),
            _ => string.Empty
        };
    }

    /// <summary>
    /// 当前是否允许"跳过此版本"：必须更新时不允许，避免用户把自己留在一个不再受支持的版本上。/ Whether skipping is allowed: a required update cannot be skipped, which would strand the user on an unsupported version.</summary>
    /// <param name="state">当前状态。/ Current state.</param>
    public static bool CanSkipVersion(UpdateState state) =>
        state.Phase is UpdatePhase.Available or UpdatePhase.ManualOnly && !state.IsMandatory;

    /// <summary>
    /// 托盘菜单项被点击时执行的动作。
    /// Action performed when the tray entry is clicked.
    /// </summary>
    /// <param name="state">当前状态。/ Current state.</param>
    public static UpdateTrayAction ResolveTrayAction(UpdateState state) => state.Phase switch
    {
        UpdatePhase.Checking => UpdateTrayAction.None,
        UpdatePhase.Downloading or UpdatePhase.Verifying => UpdateTrayAction.Cancel,
        UpdatePhase.Available or UpdatePhase.Skipped or UpdatePhase.ManualOnly => UpdateTrayAction.OpenUpdatePage,
        UpdatePhase.Ready => state.IsInstallBlocked ? UpdateTrayAction.OpenUpdatePage : UpdateTrayAction.InstallAndRestart,
        // 下载或校验失败时清单还在，用户需要的是回到那一页看到原因并重试，而不是再检查一遍版本。
        // When a download or verification failed the manifest is still there, so the user needs that page with its
        // reason and a retry rather than another version check.
        UpdatePhase.Failed => state.Manifest?.HasInstallablePackage == true ? UpdateTrayAction.OpenUpdatePage : UpdateTrayAction.Check,
        _ => UpdateTrayAction.Check
    };

    /// <summary>当前是否允许立即重启并安装。/ Whether "restart and install now" is available.</summary>
    /// <param name="state">当前状态。/ Current state.</param>
    public static bool CanInstallNow(UpdateState state) => state.IsReady && !state.IsInstallBlocked;

    /// <summary>当前是否应显示进度条。/ Whether a progress bar should be shown.</summary>
    /// <param name="state">当前状态。/ Current state.</param>
    public static bool IsProgressVisible(UpdateState state) =>
        state.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying;

    /// <summary>
    /// 下载渠道的说明文本，例如"GitHub 直连"或"加速站点 ghfast.top"。
    /// Channel description such as "GitHub 直连" or "加速站点 ghfast.top".
    /// </summary>
    /// <param name="source">正在使用的来源；为 null 时返回空字符串。/ Source in use, or an empty string when null.</param>
    public static string ResolveChannelText(UpdateDownloadSource? source) => source is null
        ? string.Empty
        : source.IsAccelerated
            ? Translations.Format("Update.Channel.Accelerated", source.HostName)
            : Translations.Get("Update.Channel.Direct");

    private static string ResolveReadyText(UpdateState state)
    {
        var version = state.AvailableVersion;
        if (state.IsInstallBlocked)
        {
            return Translations.Format("Update.Status.ReadyBlocked", version, state.InstallBlockedReason);
        }

        return Translations.Format("Update.Status.Ready", version);
    }

    private static string ResolveCheckSuffix(UpdatePhase phase, DateTimeOffset? lastCheckUtc, DateTimeOffset now)
    {
        if (lastCheckUtc is not { } lastCheck || phase == UpdatePhase.Checking)
        {
            return string.Empty;
        }

        return Translations.Format("Update.Suffix.LastCheck", RelativeTime(now - lastCheck));
    }

    private static string MandatorySuffix(UpdateState state) =>
        state.IsMandatory ? Translations.Get("Update.Suffix.Mandatory") : string.Empty;

    private static string ChannelSuffix(UpdateState state) =>
        state.ActiveSource is { } source
            ? Translations.Format("Update.Suffix.Channel", ResolveChannelText(source))
            : string.Empty;

    private static int FormatPercent(double percent) =>
        (int)Math.Clamp(Math.Round(percent, MidpointRounding.AwayFromZero), 0d, 100d);

    private static string RelativeTime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return Translations.Get("Update.Relative.JustNow");
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return Translations.Format("Update.Relative.Minutes", (int)elapsed.TotalMinutes);
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return Translations.Format("Update.Relative.Hours", (int)elapsed.TotalHours);
        }

        return Translations.Format("Update.Relative.Days", (int)elapsed.TotalDays);
    }
}
