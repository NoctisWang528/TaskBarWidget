using TaskbarLyrics.App;

namespace TaskBarWidgets.Media.Integration;

// The taskbar and the lyric card are independent surfaces. The taskbar may show
// the spectrum while the card still renders lyrics, so fast lyric scheduling and
// the wire scene are derived per surface instead of once for both.
internal static class MediaPresentationActivity
{
    internal static bool RequiresFastLyrics(LyricsPresentationScene scene) =>
        scene is LyricsPresentationScene.Lyrics or LyricsPresentationScene.Searching;

    internal static bool ShouldAdvancePresentation(
        bool taskbarVisible,
        LyricsPresentationScene taskbarScene,
        bool cardVisible,
        LyricsPresentationScene cardScene) =>
        (taskbarVisible && RequiresFastLyrics(taskbarScene)) ||
        (cardVisible && RequiresFastLyrics(cardScene));
}
