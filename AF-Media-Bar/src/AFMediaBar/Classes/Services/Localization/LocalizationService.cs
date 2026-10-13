using AFMediaBar.Classes.Settings;
using AFMediaBar.Resources;
using System.Globalization;
using System.Windows;

namespace AFMediaBar.Classes.Services.Localization;

/// <summary>
/// 界面语言的唯一写入方：把设置里的选项解析成生效语言，重发 XAML 资源，并通知需要重新取文案的界面。
///
/// 三件事必须在同一个动作里完成，否则界面会停在两种语言的混合状态：
/// 1. 更新 <see cref="Translations"/> 的当前语言（代码取文案的口径）；
/// 2. 把该语言的每一条文案重新写入应用级资源（XAML 里 <c>{DynamicResource Loc.&lt;键&gt;}</c> 的口径）；
/// 3. 发布 <see cref="LanguageChanged"/>（托盘菜单文案、提示、搜索结果这类由代码在事件里重建的文本）。
///
/// 生命周期：组合根在设置加载之后、宿主启动之前调用 <see cref="Start"/>，因此托盘图标、任务栏媒体栏与设置页
/// 一定是用最终语言创建出来的，不需要先建一次再刷新。
/// The only writer of the interface language: it resolves the settings option into a language in effect, republishes the
/// XAML resources, and notifies the surfaces that have to fetch their text again.
///
/// All three steps have to happen in one action, otherwise the interface stops halfway between two languages:
/// 1. update the active language inside <see cref="Translations"/>, the entry code uses to read text;
/// 2. write every string of that language back into the application resources, the entry XAML uses through
///    <c>{DynamicResource Loc.&lt;key&gt;}</c>;
/// 3. publish <see cref="LanguageChanged"/> for the text that code rebuilds in an event handler, such as tray menu
///    titles, tooltips, and search results.
///
/// Lifetime: the composition root calls <see cref="Start"/> after the settings are loaded and before the host starts, so
/// the tray icon, the taskbar media bar, and the settings pages are created in the final language instead of being built
/// once and refreshed afterwards.
/// </summary>
public sealed class LocalizationService : IDisposable
{
    /// <summary>XAML 资源键的前缀：一条键为 <c>About.Title</c> 的文案在资源里的键是 <c>Loc.About.Title</c>。/ Prefix of the XAML resource key: the entry keyed <c>About.Title</c> lives in the resources as <c>Loc.About.Title</c>.</summary>
    public const string ResourceKeyPrefix = "Loc.";

    private InterfaceLanguage _setting = InterfaceLanguage.System;
    private LocalizationLanguage _language = LocalizationLanguage.SimplifiedChinese;
    private bool _applied;
    private bool _started;
    private bool _disposed;

    /// <summary>设置里保存的选项（可能是"跟随系统"）。/ The option stored in the settings, which may be "follow the system".</summary>
    public InterfaceLanguage Setting => _setting;

    /// <summary>当前生效的语言（永远是三个具体语言之一）。/ The language in effect, always one of the three concrete languages.</summary>
    public LocalizationLanguage CurrentLanguage => _language;

    /// <summary>生效语言发生变化时发布；只在语言真的变了时触发，重复应用同一个选项不会打扰订阅者。/ Published when the language in effect changes; it fires only on a real change, so reapplying the same option does not disturb subscribers.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// 应用设置里的语言并订阅后续变化。重复调用是幂等的：宿主重启或测试重复调用不会产生第二次订阅。
    /// Applies the language from the settings and subscribes to later changes. Repeated calls are idempotent, so a host
    /// restart or a repeated call from a test never creates a second subscription.
    /// </summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        SettingsManager.SettingsChanged += OnSettingsChanged;
        Apply(SettingsManager.Current.InterfaceLanguage);
    }

    /// <summary>
    /// 应用一个语言选项：解析、重发资源、通知订阅者。
    ///
    /// 它同时被设置变更和"恢复默认设置"覆盖：<see cref="SettingsManager.SettingsChanged"/> 在重置时只带范围而
    /// 不带属性名，因此这里按当前设置重新解析，而不是只认某一个属性名。
    /// Applies one language option: resolve, republish, notify.
    ///
    /// It also covers "restore defaults": <see cref="SettingsManager.SettingsChanged"/> carries only a scope and no
    /// property name on a reset, so this method re-resolves from the current settings instead of watching one property
    /// name.
    /// </summary>
    /// <param name="setting">要应用的选项。/ The option to apply.</param>
    public void Apply(InterfaceLanguage setting)
    {
        if (_disposed)
        {
            return;
        }

        _setting = Enum.IsDefined(setting) ? setting : InterfaceLanguage.System;
        var resolved = InterfaceLanguagePolicy.Resolve(_setting, CultureInfo.CurrentUICulture);
        var changed = _applied && resolved != _language;
        _language = resolved;
        _applied = true;

        // 顺序是刻意的：先写语言，再重发 XAML 资源，最后通知订阅者。订阅者（分组标签条一类）要读的是已经换过的
        // 动态资源值，先通知就会让它们把旧语言再快照一遍。
        // The order is deliberate: write the language, republish the XAML resources, and only then notify subscribers. A
        // subscriber such as the group strip reads the already changed dynamic-resource values, and notifying first would make
        // it snapshot the old language once more.
        Translations.SetActiveLanguage(resolved);
        ApplyCulture(resolved);
        PublishResources();

        if (changed)
        {
            Translations.RaiseLanguageChanged();
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 把生效语言的每一条文案写入应用级资源。XAML 引用的是资源键，因此这一步就是"界面换语言"本身；
    /// 直接改应用级字典而不是替换合并字典，是因为 WPF 在字典条目变化时会重新解析所有动态资源引用。
    /// Writes every string of the language in effect into the application resources. XAML references resource keys, so this
    /// step *is* the language switch; it writes into the application-level dictionary rather than replacing a merged one
    /// because WPF re-resolves every dynamic resource reference when a dictionary entry changes.
    /// </summary>
    private static void PublishResources()
    {
        // 设计期与单元测试里没有 Application，此时只更新 Translations（代码口径），不触碰 WPF。
        // There is no Application during design time or in unit tests, so only Translations is updated then and WPF is
        // left alone.
        if (Application.Current is not { } application)
        {
            return;
        }

        // 资源字典只能在 UI 线程改动。当前所有写入方都在 UI 线程（组合根启动、设置页选择、重置入口），
        // 但设置变更事件的对端不是一个受控枚举，因此这里显式跳到 Dispatcher，而不是假定调用线程。
        // The resource dictionary may only be changed on the UI thread. Every writer today runs on it (composition-root
        // startup, the settings-page choice, the reset entries), but the far side of the settings-changed event is not an
        // enumerable set of callers, so this hops to the dispatcher explicitly instead of assuming the calling thread.
        var dispatcher = application.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(PublishResources);
            }

            return;
        }

        foreach (var key in Translations.Keys)
        {
            application.Resources[ResourceKeyPrefix + key] = Translations.Get(key);
        }
    }

    /// <summary>
    /// 让进程的 UI 区域性跟随界面语言。文字度量与框架自带文案都读它，因此不设置时会出现"界面是繁体、文字按
    /// 简体度量"的错位（媒体文字的跑马灯宽度正是这么算出来的）。
    /// Aligns the process UI culture with the interface language. Text measurement and the framework's own wording read
    /// it, so skipping this step leaves the interface in one language while text is measured in another — which is exactly
    /// how the media bar's marquee width is calculated.
    /// </summary>
    private static void ApplyCulture(LocalizationLanguage language)
    {
        var culture = CultureInfo.GetCultureInfo(InterfaceLanguagePolicy.ToCultureName(language));
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        // 语言之外的设置变化同样落进这里：重新解析的代价只是一次字符串比较，而漏掉重置路径的代价是语言永远不跟着
        // "恢复默认设置"回来。
        // Settings changes other than the language land here too: re-resolving costs one string comparison, while missing
        // the reset path would mean the language never follows "restore defaults" back.
        Apply(SettingsManager.Current.InterfaceLanguage);
    }

    /// <summary>取消订阅设置变更。宿主释放时调用；释放之后不再发布任何资源或事件。/ Unsubscribes from settings changes. The host calls it on disposal, after which no resource or event is published any more.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SettingsManager.SettingsChanged -= OnSettingsChanged;
    }
}
