using AFMediaBar.Classes.Models.Updates;
using AFMediaBar.Classes.Services.Localization;
using AFMediaBar.Classes.Services.Updates;
using AFMediaBar.Classes.Settings;

namespace AFMediaBar.ViewModels.Windows
{
    /// <summary>
    /// 设置窗口的纯数据视图模型：窗口标题，以及"是否有可用更新"这一条导航提示。
    ///
    /// 这里只发布布尔与文本：导航项上的徽章是 WPF-UI 控件，视图模型不引用控件类型，由窗口自己按这里的取值
    /// 安装或移除徽章。
    ///
    /// 导航提示是按状态拼出来的句子，因此这里订阅界面语言并在切换后重算一次：只让 WPF 重读绑定会把同一句
    /// 旧语言再显示一遍。
    /// Pure-data view model for the settings window: the window title plus the single navigation hint telling whether
    /// an update is available.
    ///
    /// Only booleans and text are published: the badge on the navigation item is a WPF-UI control, and the view model
    /// never references control types, so the window installs or removes the badge according to these values.
    ///
    /// The notice is a sentence composed from the update state, so this view model subscribes to the interface language
    /// and recomputes it after a switch: letting WPF re-read the bindings alone would show the same sentence in the old
    /// language again.
    /// </summary>
    public partial class SettingsWindowViewModel : ObservableObject
    {
        private readonly UpdateService _updateService;
        private readonly LocalizationService _localization;

        /// <summary>窗口与侧栏标识块上的应用名称：它是品牌名，因此三种语言下都写作同一个写法，不登记文案。/ Application name on the window and in the pane identity block: a brand name, written the same way in all three languages and therefore not registered as text.</summary>
        [ObservableProperty]
        private string _applicationTitle = "AFMediaBar";

        [ObservableProperty]
        private bool _hasUpdateAvailable;

        [ObservableProperty]
        private string _updateNoticeText = string.Empty;

        /// <summary>导航项上的短提示；为空时窗口隐藏该行。/ Short notice shown on the navigation item; the window hides the row when it is empty.</summary>
        [ObservableProperty]
        private string _updateNoticeTitle = string.Empty;

        /// <summary>
        /// 创建设置窗口视图模型并订阅更新状态。窗口是 Transient、视图模型是 Singleton，因此由窗口负责退订。
        /// Creates the settings-window view model and subscribes to update state. The window is transient while the
        /// view model is a singleton, so unsubscribing is the window's responsibility.
        /// </summary>
        /// <param name="updateService">更新下载器协调器。/ Update downloader coordinator.</param>
        /// <param name="localization">界面语言：导航提示的文案在切换语言后必须重算。/ Interface language: the navigation notice has to be recomputed after a language switch.</param>
        public SettingsWindowViewModel(UpdateService updateService, LocalizationService localization)
        {
            _updateService = updateService;
            _localization = localization;

            // 视图模型与语言服务都是单例，因此这次订阅与进程同寿命，不需要退订路径（窗口只在关闭时退订更新状态）。
            // Both the view model and the language service are singletons, so this subscription lives as long as the
            // process and needs no release path; the window only releases the update subscription when it closes.
            _localization.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            // 先按当前状态把提示重算成新语言，再让 WPF 重读全部绑定——顺序反过来的话，重读拿到的仍是旧字符串。
            // Recompute the notice from the current state in the new language first and only then make WPF re-read
            // every binding; the other order would re-read the old string.
            ApplyState(_updateService.CurrentState);
            OnPropertyChanged(string.Empty);
        }

        /// <summary>开始订阅更新状态；窗口显示时调用，可重复调用。/ Starts listening to update state, called when the window is shown and safe to call repeatedly.</summary>
        public void Subscribe() => _updateService.UpdateStateChanged += ApplyState;

        /// <summary>停止订阅更新状态；窗口关闭时调用，可重复调用。/ Stops listening to update state, called when the window closes and safe to call repeatedly.</summary>
        public void Unsubscribe() => _updateService.UpdateStateChanged -= ApplyState;

        /// <summary>按当前状态刷新提示文本，供窗口显示时立即取一次值。/ Refreshes the notice from the current state so the window can read it as soon as it is shown.</summary>
        public void Refresh() => ApplyState(_updateService.CurrentState);

        private void ApplyState(UpdateState state)
        {
            HasUpdateAvailable = state.Phase is UpdatePhase.Available
                or UpdatePhase.Downloading
                or UpdatePhase.Verifying
                or UpdatePhase.Ready
                or UpdatePhase.ManualOnly;

            UpdateNoticeText = UpdatePresentationPolicy.ResolveStatusText(
                state,
                SettingsManager.Current.Update.LastCheckUtc,
                DateTimeOffset.Now);
            UpdateNoticeTitle = UpdatePresentationPolicy.ResolveNavigationNoticeText(state);
        }
    }
}
