using AFMediaBar.Classes.Settings;
using AFMediaBar.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AFMediaBar.ViewModels.Pages;

/// <summary>
/// 歌词页「第二行顺序」列表里的一项：一种第二行来源与它在界面上的显示名。
/// One row of the Lyrics page's second-line order list: a second-line source and the name shown for it.
///
/// 显示名按当前语言解析后写进来，因此语言变化时由页面视图模型重建整个列表，而不是在这里监听语言事件（与来源列表同一处理）。
/// The display name is resolved in the active language and written in, so a language change rebuilds the whole list inside the page view model
/// instead of this item listening for language events, the same handling as the source list.
/// </summary>
public partial class LyricsSecondaryLineSettingItem : ObservableObject
{
    /// <summary>
    /// 创建一个第二行顺序列表项。
    /// Creates one second-line order entry.
    /// </summary>
    /// <param name="mode">第二行来源。/ Second-line source.</param>
    public LyricsSecondaryLineSettingItem(LyricsSecondaryLineMode mode)
    {
        Mode = mode;
        _displayName = Translations.Get(ResolveLabelKey(mode));
    }

    /// <summary>这一项对应的第二行来源。/ The second-line source this row stands for.</summary>
    public LyricsSecondaryLineMode Mode { get; }

    /// <summary>当前语言下的显示名。/ The display name in the active language.</summary>
    [ObservableProperty]
    private string _displayName;

    /// <summary>来源对应的文案键。/ The text key of a source.</summary>
    /// <param name="mode">第二行来源。/ Second-line source.</param>
    public static string ResolveLabelKey(LyricsSecondaryLineMode mode) => mode switch
    {
        LyricsSecondaryLineMode.Translation => "Lyrics.SecondLine.Translation",
        LyricsSecondaryLineMode.Romanization => "Lyrics.SecondLine.Romanization",
        _ => "Lyrics.SecondLine.NextLine"
    };
}
