using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace TaskBarWidgets.UI.Cards;

internal static class CardLyricsProbe
{
    // Structured fixtures exercise the real native template in a nonactivating
    // offscreen CardWindow. No media/native host, player, HTML, or script runs.
    internal static async Task<object> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var elapsed = Stopwatch.StartNew();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ui-lyrics-check"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var checks = new List<object>();
        var content = new Grid { Height = 220 };
        var lyrics = new CardLyricsView { Height = 96, VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(lyrics);
        var card = new CardWindow("Card lyrics component probe", content, 280, autoHeight: false, width: 384);
        card.Root.RequestedTheme = ElementTheme.Dark;
        card.Window.AppWindow.IsShownInSwitchers = false;
        UiTemplateProbe.PreventProbeActivation(card.Window);
        card.Window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, 384, 280));
        var activated = false;
        card.Window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) activated = true;
        };
        CardLyricsTextLine[]? reusedLines = null;
        var reusedLayoutBuilds = 0;
        try
        {
            card.Show(animate: false);
            await card.VisibleFrame.WaitAsync(deadline.Token);
            await SettleAsync(card.Root, deadline.Token);
            if (!lyrics.IsLoaded || lyrics.ActualWidth <= 0 || lyrics.ActualHeight <= 0)
                throw new InvalidOperationException("Native lyrics host did not realize a nonzero layout.");

            lyrics.Activate();
            lyrics.Receive(Frame(1, "probe-a", "第一行歌词", "第二行歌词", .42, 0,
                isPlaying: true, wordScanProgress: .35));
            await SettleAsync(card.Root, deadline.Token);
            AssertRealized(lyrics, "ordinary");
            reusedLines = lyrics.Lines.ToArray();
            reusedLayoutBuilds = lyrics.LayoutBuilds;
            if (!lyrics.ContentVisible || lyrics.SingleLine)
                throw new InvalidOperationException("The 96-DIP lyric host did not keep the compact two-line layout.");
            var primary = lyrics.PrimaryLine ?? throw new InvalidOperationException("No primary lyric line was realized.");
            if (primary.Text != "第一行歌词" || lyrics.SecondaryLine?.Text != "第二行歌词")
                throw new InvalidOperationException("The ordinary native lyric fixture did not apply current/next text.");
            if (lyrics.SecondaryLine.Opacity is < .66 or > .69)
                throw new InvalidOperationException("The next-line progress opacity was not applied after layout.");
            AssertScanLayout(primary);
            checks.Add(new
            {
                kind = "ordinary",
                width = lyrics.ActualWidth,
                height = lyrics.ActualHeight,
                rowHeight = lyrics.RowHeight,
                singleLine = lyrics.SingleLine,
                primary = primary.Text,
                next = lyrics.SecondaryLine?.Text
            });
            images.Add(new { kind = "ordinaryDark", path = await CaptureAsync(card.Root, directory, "lyrics-ordinary-dark.png") });

            lyrics.SetCardFallbackFont(new FontFamily("Segoe UI"));
            lyrics.Receive(Frame(1, "probe-a", "第一行歌词", "第二行歌词", .42, 0,
                isPlaying: true, wordScanProgress: .35));
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.PrimaryLine?.BaseText.FontFamily.Source != "Segoe UI")
                throw new InvalidOperationException("The card-level font fallback was not consumed by the next lyric frame.");
            checks.Add(new { kind = "cardFontFallback", family = lyrics.PrimaryLine.BaseText.FontFamily.Source });

            lyrics.Height = 62;
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.SingleLine || lyrics.PrimaryLine is not { ActualWidth: > 0, ActualHeight: > 0 } ||
                lyrics.SecondaryLine?.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The media-card lyric height did not keep two nonempty compact rows.");
            checks.Add(new { kind = "mediaHeight", height = lyrics.ActualHeight, rowHeight = lyrics.RowHeight,
                singleLine = lyrics.SingleLine });
            lyrics.Height = 96;
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);

            // Progress-only updates must reuse the same four line objects and all
            // derived TextBlocks; only the scan clip and scroll offset change.
            double previousClip = -1;
            var progressMeasureCount = lyrics.PrimaryLine!.MeasureCount;
            foreach (var (revision, progress) in new[] { (2L, .1), (3L, .3), (4L, .65), (5L, .95) })
            {
                lyrics.Receive(Frame(revision, "probe-a", "第一行歌词", "第二行歌词", .42, 0,
                    isPlaying: false, wordScanProgress: progress));
                await SettleAsync(card.Root, deadline.Token);
                var line = lyrics.PrimaryLine!;
                if (line.ScanClipWidth <= previousClip)
                    throw new InvalidOperationException("The native scan clip did not advance with authoritative progress.");
                previousClip = line.ScanClipWidth;
            }
            if (!ReusedSameElements(lyrics, reusedLines) || lyrics.LayoutBuilds != reusedLayoutBuilds ||
                lyrics.PrimaryLine!.MeasureCount != progressMeasureCount)
                throw new InvalidOperationException("Progress-only frames rebuilt or remeasured the lyric visual tree.");
            checks.Add(new { kind = "wordScan", finalClip = previousClip, reusedVisualTree = true,
                layoutBuilds = reusedLayoutBuilds, remeasured = false });

            lyrics.Receive(Frame(6, "probe-a", "第一行歌词", "第二行歌词", .42, 0,
                isPlaying: false, wordScanProgress: .62));
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.DisplayedWordScan != .62 || lyrics.ScanSmoothing)
                throw new InvalidOperationException("Pause did not freeze the authoritative word-scan position.");
            checks.Add(new { kind = "pauseFrozen", progress = lyrics.DisplayedWordScan });

            lyrics.Receive(Frame(7, "probe-a", "第一行歌词", "第二行歌词", .42, 0,
                isPlaying: true, wordScanProgress: .82));
            await SettleAsync(card.Root, deadline.Token);
            lyrics.Receive(Frame(8, "probe-a", "第一行歌词", "第二行歌词", .43, 0,
                isPlaying: true, wordScanProgress: .9));
            await Task.Delay(70, deadline.Token);
            if (lyrics.DisplayedWordScan is not { } smoothing || smoothing < .82 || smoothing > .9)
                throw new InvalidOperationException("Resume did not smooth toward the new authoritative scan progress.");
            await UntilAsync(() => lyrics.DisplayedWordScan == .9, deadline, "Word-scan smoothing never converged.");
            checks.Add(new { kind = "resume", smoothedTo = .9 });

            lyrics.Receive(Frame(9, "probe-a", "Seek 校正后的当前行", "Seek 后的下一行", .08, 7,
                isPlaying: true, wordScanProgress: .18));
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.TransitionActive || lyrics.PrimaryLine?.Text != "Seek 校正后的当前行" ||
                lyrics.DisplayedWordScan != .18)
                throw new InvalidOperationException("Seek did not cancel interpolation and apply the authoritative frame immediately.");
            checks.Add(new { kind = "seek", transition = false, progress = lyrics.DisplayedWordScan });

            lyrics.Receive(Frame(10, "probe-a", "Seek 后的下一行", "再下一行", .12, 8,
                isPlaying: true, wordScanProgress: .25));
            await SettleAsync(card.Root, deadline.Token);
            if (!lyrics.TransitionActive)
                throw new InvalidOperationException("A sequential lyric line change did not start the native roll.");
            var rollingTops = lyrics.Lines
                .Where(line => line.Visibility == Visibility.Visible)
                .Select(Canvas.GetTop)
                .ToArray();
            if (rollingTops.Length != 3 || rollingTops.Distinct().Count() != 3 ||
                Math.Abs(lyrics.TransitionDistance - lyrics.RowPitch) > .01)
                throw new InvalidOperationException("The native single-line roll reused a row or had an invalid distance.");
            var singleRollDistance = lyrics.TransitionDistance;
            lyrics.Receive(Frame(10, "probe-a", "Seek 后的下一行", "再下一行", .12, 8,
                isPlaying: true, wordScanProgress: .6));
            await Task.Delay(100, deadline.Token);
            var promoting = lyrics.PrimaryLine ??
                throw new InvalidOperationException("The promoting line disappeared during a roll.");
            if (promoting.ContentWidth <= 0 ||
                promoting.ScanClipWidth < promoting.ContentWidth * .55)
                throw new InvalidOperationException("Progress-only frames during a roll did not update the promoting line scan.");
            images.Add(new { kind = "lineRoll", path = await CaptureAsync(card.Root, directory, "lyrics-line-roll-dark.png") });
            await UntilAsync(() => !lyrics.TransitionActive, deadline, "Sequential lyric roll did not complete.");
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.PrimaryLine?.Text != "Seek 后的下一行" || lyrics.SecondaryLine?.Text != "再下一行")
                throw new InvalidOperationException("The completed line roll lost the promoted current or incoming next text.");
            checks.Add(new { kind = "lineRoll", promoted = lyrics.PrimaryLine?.Text, next = lyrics.SecondaryLine?.Text,
                rows = rollingTops.Length, distance = singleRollDistance });

            lyrics.Receive(Frame(11, "probe-b", "切歌后的当前行", "切歌后的下一行", .2, 0,
                isPlaying: true, wordScanProgress: null));
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.TransitionActive || lyrics.PrimaryLine?.Text != "切歌后的当前行")
                throw new InvalidOperationException("Track switch reused the old roll or text.");
            checks.Add(new { kind = "trackSwitch", immediate = true });

            lyrics.Receive(Frame(12, "probe-b", "Translation current", "Translation next", .2, 0,
                isPlaying: true, translationMode: true, currentTranslation: "译文当前行",
                nextTranslation: "译文下一行", wordScanProgress: null));
            await SettleAsync(card.Root, deadline.Token);
            var translationLine = lyrics.SecondaryLine ??
                throw new InvalidOperationException("Translation mode did not realize a second line.");
            if (translationLine.Text != "译文当前行" ||
                translationLine.BaseText.Foreground is not SolidColorBrush translationBrush ||
                translationBrush.Color != lyrics.TranslationColor)
                throw new InvalidOperationException("Translation mode did not use the translation foreground token.");
            checks.Add(new { kind = "translation", secondary = translationLine.Text });
            images.Add(new { kind = "translationDark", path = await CaptureAsync(card.Root, directory, "lyrics-translation-dark.png") });

            lyrics.Receive(Frame(13, "probe-b", "Translation next", "Translation after", .1, 1,
                isPlaying: true, translationMode: true, currentTranslation: "译文下一行",
                nextTranslation: "译文再下一行", wordScanProgress: null));
            await SettleAsync(card.Root, deadline.Token);
            if (!lyrics.TransitionActive || lyrics.Lines.Count(line => line.Visibility == Visibility.Visible) != 4 ||
                Math.Abs(lyrics.TransitionDistance - (lyrics.RowPitch * 2)) > .01)
                throw new InvalidOperationException("Translation mode did not start a two-row pair roll.");
            var translationRollDistance = lyrics.TransitionDistance;
            await UntilAsync(() => !lyrics.TransitionActive, deadline, "Translation pair roll did not complete.");
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.PrimaryLine?.Text != "Translation next" || lyrics.SecondaryLine?.Text != "译文下一行")
                throw new InvalidOperationException("Translation pair roll did not commit the next original/translation pair.");
            checks.Add(new { kind = "translationRoll", rows = 4, distance = translationRollDistance,
                primary = lyrics.PrimaryLine?.Text, translation = lyrics.SecondaryLine?.Text });

            lyrics.Height = 28;
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);
            if (!lyrics.SingleLine || lyrics.SecondaryLine?.Visibility != Visibility.Collapsed ||
                lyrics.PrimaryLine is not { ActualWidth: > 0, ActualHeight: > 0 })
                throw new InvalidOperationException(
                    $"Narrow lyrics did not collapse to one nonempty line. single={lyrics.SingleLine} " +
                    $"host={lyrics.ActualWidth}x{lyrics.ActualHeight} row={lyrics.RowHeight:0.##} " +
                    $"secondary={lyrics.SecondaryLine?.Visibility} " +
                    $"primary={lyrics.PrimaryLine?.ActualWidth}x{lyrics.PrimaryLine?.ActualHeight} " +
                    $"text={lyrics.PrimaryLine?.BaseText.ActualWidth}x{lyrics.PrimaryLine?.BaseText.ActualHeight}");
            checks.Add(new { kind = "narrow", width = lyrics.ActualWidth, height = lyrics.ActualHeight, singleLine = true });
            images.Add(new { kind = "narrow", path = await CaptureAsync(card.Root, directory, "lyrics-narrow-dark.png") });

            lyrics.Height = 96;
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);
            var longText = "这是一行很长的歌词，用于验证卡片内不会重建文本布局，并且扫描推进时能够水平跟随而不越界。" +
                string.Concat(Enumerable.Repeat("连续文本 Long lyric phrase 0123456789。", 5));
            lyrics.Receive(Frame(14, "probe-b", longText, "长文本的下一行", .5, 1,
                isPlaying: false, wordScanProgress: .7));
            await SettleAsync(card.Root, deadline.Token);
            primary = lyrics.PrimaryLine!;
            if (!primary.HasHorizontalOverflow || primary.HorizontalOffset <= 0 ||
                Math.Abs(primary.BaseText.ActualWidth - primary.ScanText.ActualWidth) > .5)
                throw new InvalidOperationException("Long lyric text did not use identical base/scan layout with bounded horizontal follow.");
            checks.Add(new
            {
                kind = "longText",
                contentWidth = primary.ContentWidth,
                viewportWidth = lyrics.ActualWidth,
                offset = primary.HorizontalOffset,
                identicalWidth = Math.Abs(primary.BaseText.ActualWidth - primary.ScanText.ActualWidth) <= .5
            });
            images.Add(new { kind = "longTextDark", path = await CaptureAsync(card.Root, directory, "lyrics-long-text-dark.png") });

            card.Root.RequestedTheme = ElementTheme.Light;
            await Task.Delay(140, deadline.Token);
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.PrimaryColor.R != 20 || lyrics.PrimaryColor.G != 20 || lyrics.PrimaryColor.B != 20)
                throw new InvalidOperationException("Light theme did not switch the native lyric foreground.");
            checks.Add(new { kind = "theme", theme = lyrics.EffectiveTheme.ToString(), primary = lyrics.PrimaryColor.ToString() });
            images.Add(new { kind = "longTextLight", path = await CaptureAsync(card.Root, directory, "lyrics-long-text-light.png") });

            lyrics.Receive(Frame(15, "probe-b", "居中歌词", "下一行", .4, 1,
                isPlaying: false, textAlignment: "Center"));
            await SettleAsync(card.Root, deadline.Token);
            primary = lyrics.PrimaryLine!;
            var expectedCenter = (lyrics.ActualWidth - primary.ContentWidth) / 2;
            if (Canvas.GetLeft(primary.BaseText) <= 0 ||
                Math.Abs(Canvas.GetLeft(primary.BaseText) - expectedCenter) > 1 ||
                Math.Abs(Canvas.GetLeft(primary.BaseText) - Canvas.GetLeft(primary.ScanText)) > .01)
                throw new InvalidOperationException("Center alignment did not keep base/scan in the same centered layout.");
            checks.Add(new { kind = "alignment", alignment = "Center",
                left = Canvas.GetLeft(primary.BaseText), width = primary.BaseText.ActualWidth });

            lyrics.ProbeHighContrast = true;
            lyrics.RefreshAppearance();
            await SettleAsync(card.Root, deadline.Token);
            var expected = new UISettings().GetColorValue(UIColorType.Foreground);
            if (lyrics.PrimaryColor.A != 255 || lyrics.PrimaryColor.R != expected.R ||
                lyrics.PrimaryColor.G != expected.G || lyrics.PrimaryColor.B != expected.B ||
                lyrics.TextShadowEnabled || lyrics.SecondaryLine?.Opacity != 1 ||
                lyrics.SecondaryColor != lyrics.PrimaryColor)
                throw new InvalidOperationException("High contrast did not use the opaque system foreground without text shadow.");
            checks.Add(new { kind = "highContrast", primary = lyrics.PrimaryColor.ToString(), shadow = lyrics.TextShadowEnabled });
            images.Add(new { kind = "highContrast", path = await CaptureAsync(card.Root, directory, "lyrics-high-contrast.png") });
            lyrics.ProbeHighContrast = null;
            lyrics.RefreshAppearance();

            lyrics.ProbeReducedMotion = true;
            lyrics.Receive(Frame(16, "probe-b", "下一行", "Reduced motion next", .1, 2,
                isPlaying: false, wordScanProgress: null));
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.TransitionActive || lyrics.PrimaryLine?.Text != "下一行")
                throw new InvalidOperationException("Reduced motion did not replace the lyric roll with an immediate update.");
            checks.Add(new { kind = "reducedMotion", immediate = true });
            lyrics.ProbeReducedMotion = null;

            // Keep track, line, role, and placeholder fixed while the effective
            // primary font changes with style size and available height.
            lyrics.Height = 96;
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);
            lyrics.Activate();
            lyrics.Receive(Frame(20, "dynamic-font", "Dynamic primary line", "Dynamic next line", .3, 4,
                isPlaying: false, wordScanProgress: .5, fontSize: 18));
            await SettleAsync(card.Root, deadline.Token);
            primary = lyrics.PrimaryLine!;
            var dynamicLines = lyrics.Lines.ToArray();
            var dynamicBuilds = lyrics.LayoutBuilds;
            var primarySize18 = AssertFontTriplet(primary);
            var primaryWidth18 = primary.ContentWidth;
            var primaryMeasure18 = primary.MeasureCount;
            AssertScanMeasurement(primary, "initial primary font");
            if (Math.Abs(primarySize18 - 18) > .05)
                throw new InvalidOperationException("The initial fixed primary role did not use the requested 18-DIP font.");

            lyrics.Receive(Frame(21, "dynamic-font", "Dynamic primary line", "Dynamic next line", .3, 4,
                isPlaying: false, wordScanProgress: .5, fontSize: 26));
            await SettleAsync(card.Root, deadline.Token);
            primary = lyrics.PrimaryLine!;
            var primarySize26 = AssertFontTriplet(primary);
            AssertScanMeasurement(primary, "larger primary font");
            var primaryWidth26 = primary.ContentWidth;
            var primaryMeasure26 = primary.MeasureCount;
            if (Math.Abs(primarySize26 - 26) > .05 || primarySize26 <= primarySize18 + 1 ||
                primaryWidth26 <= primaryWidth18 + 1 ||
                primaryMeasure26 <= primaryMeasure18 ||
                !ReusedSameElements(lyrics, dynamicLines) || lyrics.LayoutBuilds != dynamicBuilds)
                throw new InvalidOperationException("Dynamic primary font style did not update font, measurement, and scan clip in the reused tree.");
            images.Add(new { kind = "dynamicPrimaryFont",
                path = await CaptureAsync(card.Root, directory, "lyrics-dynamic-primary-font.png") });

            lyrics.Height = 24;
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);
            primary = lyrics.PrimaryLine!;
            var primarySizeHeight = AssertFontTriplet(primary);
            AssertScanMeasurement(primary, "reduced primary height");
            if (primarySizeHeight >= primarySize26 - 1 || primary.ContentWidth >= primaryWidth26 - .5)
                throw new InvalidOperationException(
                    "Available-height font reduction did not remeasure the primary text. " +
                    $"size26={primarySize26:0.###} sizeHeight={primarySizeHeight:0.###} " +
                    $"width26={primaryWidth26:0.###} widthHeight={primary.ContentWidth:0.###} " +
                    $"host={lyrics.ActualHeight:0.###} row={lyrics.RowHeight:0.###}");
            if (primary.MeasureCount <= primaryMeasure26)
                throw new InvalidOperationException("Available-height font reduction did not invalidate lyric measurement.");
            checks.Add(new
            {
                kind = "primaryFontResize",
                role = "Primary",
                placeholder = false,
                size18 = primarySize18,
                size26 = primarySize26,
                sizeAtReducedHeight = primarySizeHeight,
                width18 = primaryWidth18,
                width26 = primaryWidth26,
                widthAtReducedHeight = primary.ContentWidth,
                measuredOnFontChange = primaryMeasure26 > primaryMeasure18,
                measuredOnHeightChange = primary.MeasureCount > primaryMeasure26,
                reusedVisualTree = ReusedSameElements(lyrics, dynamicLines) && lyrics.LayoutBuilds == dynamicBuilds
            });
            lyrics.Height = 96;
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);

            // Fixed translation role/placeholder with only its effective font
            // size changing. Its measured text width must change as well.
            lyrics.Receive(Frame(22, "dynamic-font", "Primary fixed", "Next fixed", .3, 4,
                isPlaying: false, translationMode: true, currentTranslation: "Translation fixed text",
                wordScanProgress: null, fontSize: 18));
            await SettleAsync(card.Root, deadline.Token);
            var secondary = lyrics.SecondaryLine ??
                throw new InvalidOperationException("Dynamic translation scenario did not realize its secondary line.");
            if (secondary.Text != "Translation fixed text")
                throw new InvalidOperationException("Dynamic translation scenario lost its fixed translation role/placeholder state.");
            var translationSize18 = AssertFontTriplet(secondary);
            var translationWidth18 = secondary.ContentWidth;
            var translationMeasure18 = secondary.MeasureCount;

            lyrics.Receive(Frame(23, "dynamic-font", "Primary fixed", "Next fixed", .3, 4,
                isPlaying: false, translationMode: true, currentTranslation: "Translation fixed text",
                wordScanProgress: null, fontSize: 26));
            await SettleAsync(card.Root, deadline.Token);
            secondary = lyrics.SecondaryLine!;
            var translationSize26 = AssertFontTriplet(secondary);
            if (translationSize26 <= translationSize18 + .5 ||
                secondary.ContentWidth <= translationWidth18 + 1 ||
                secondary.MeasureCount <= translationMeasure18 ||
                !ReusedSameElements(lyrics, dynamicLines) || lyrics.LayoutBuilds != dynamicBuilds)
                throw new InvalidOperationException("Dynamic translation font did not update font and measured width in the reused tree.");
            checks.Add(new
            {
                kind = "translationFontResize",
                role = "Translation",
                placeholder = false,
                size18 = translationSize18,
                size26 = translationSize26,
                width18 = translationWidth18,
                width26 = secondary.ContentWidth,
                measuredOnFontChange = secondary.MeasureCount > translationMeasure18,
                reusedVisualTree = ReusedSameElements(lyrics, dynamicLines) && lyrics.LayoutBuilds == dynamicBuilds
            });

            lyrics.Activate();
            lyrics.Receive(Frame(50, "probe-resource", "Low", "Old", .1, 0));
            if (lyrics.Revision != 50)
                throw new InvalidOperationException("Resource-probe revision gate rejected a valid frame.");
            lyrics.Activate();
            lyrics.Receive(Frame(1, "probe-resource", "Restarted", "New", .1, 0));
            if (lyrics.Revision != 1)
                throw new InvalidOperationException("A backend restart with a lower revision was rejected after resubscribe.");
            if (CardLyricsFrameParser.TryParse(JsonSerializer.SerializeToElement(new
                {
                    revision = 2,
                    trackId = "probe-resource",
                    available = true,
                    scripts = Array.Empty<string>()
                }), out _))
                throw new InvalidOperationException("A legacy script frame was accepted by the structured frame parser.");
            checks.Add(new { kind = "revisionRestart", acceptedLowerRevision = true, rejectedLegacyScripts = true });

            lyrics.HideLyrics();
            var hiddenRevision = lyrics.Revision;
            lyrics.Receive(Frame(99, "probe-hidden", "Hidden frame must be ignored", "Hidden next", .1, 0,
                isPlaying: true, wordScanProgress: .2));
            if (lyrics.ContentVisible || lyrics.Revision != hiddenRevision ||
                lyrics.TransitionActive || lyrics.AnimationTimerActive)
                throw new InvalidOperationException("Hidden lyrics consumed a frame or remained visible.");
            lyrics.Height = 120;
            card.Root.UpdateLayout();
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.ContentVisible)
                throw new InvalidOperationException("A hidden lyric host was resurrected by a layout pass.");
            lyrics.Height = 96;
            card.Root.UpdateLayout();
            lyrics.Activate();
            lyrics.Receive(Frame(17, "probe-b", "Reopen snapshot current", "Reopen snapshot next", .1, 2,
                isPlaying: true, wordScanProgress: null));
            await SettleAsync(card.Root, deadline.Token);
            if (!lyrics.ContentVisible || lyrics.TransitionActive ||
                lyrics.PrimaryLine?.Text != "Reopen snapshot current" || lyrics.LayoutBuilds != reusedLayoutBuilds)
                throw new InvalidOperationException("Reopen did not apply the latest snapshot immediately with reused visuals.");
            checks.Add(new { kind = "hideReopen", transition = false, layoutBuilds = lyrics.LayoutBuilds });

            lyrics.Receive(Frame(18, "probe-b", "", "", 0, -1, available: false,
                isPlaying: false, wordScanProgress: null, scene: "message"));
            await SettleAsync(card.Root, deadline.Token);
            if (lyrics.ContentVisible)
                throw new InvalidOperationException("Unavailable lyrics left the native lyric content visible.");
            checks.Add(new { kind = "unavailable", collapsed = true });

            var position = card.Window.AppWindow.Position;
            var size = card.Window.AppWindow.Size;
            if (activated || position.X + size.Width >= -20000 || position.Y + size.Height >= -20000)
                throw new InvalidOperationException("The lyrics probe activated or left its offscreen region.");
            return new
            {
                checks,
                images,
                durationMs = elapsed.ElapsedMilliseconds,
                probeActivated = activated,
                structuredFixtures = true,
                webView2Used = false,
                htmlParsed = false,
                actualPlayerTimingVerified = false,
                realHighContrastToggleVerified = false
            };
        }
        finally
        {
            lyrics.ProbeHighContrast = null;
            lyrics.ProbeReducedMotion = null;
            lyrics.Dispose();
            card.Close();
        }
    }

    internal static JsonElement Frame(
        long revision,
        string trackId,
        string current,
        string next,
        double progress = .4,
        int currentLineIndex = 0,
        bool available = true,
        bool isPlaying = false,
        double? wordScanProgress = null,
        bool translationMode = false,
        string currentTranslation = "",
        string nextTranslation = "",
        bool animateTransition = true,
        string scene = "lyrics",
        bool isPureMusic = false,
        double fontSize = 18,
        string textAlignment = "Left",
        object? nativeAppearance = null)
    {
        return JsonSerializer.SerializeToElement(new
        {
            renderVersion = 2,
            revision,
            trackId,
            available,
            isPlaying,
            lyrics = new
            {
                current,
                next,
                progress,
                currentLineIndex,
                trackId,
                isPureMusic,
                isPlaying,
                currentTranslation,
                nextTranslation,
                translationMode,
                animateTransition,
                wordScanProgress,
                scene
            },
            style = new
            {
                fontFamily = "Segoe UI Variable Text",
                layoutScalePercent = 100,
                fontSize,
                fontWeight = 500,
                showCover = false,
                enableControlPanel = false,
                surfaceColor = "transparent",
                surfaceShadow = "none",
                primaryColor = "rgba(255, 255, 255, .95)",
                secondaryColor = "rgba(255, 255, 255, .65)",
                translationColor = "rgba(255, 255, 255, .7)",
                wordScanOverlayColor = "#8ed8ff",
                textShadow = "0 1px 2px rgba(0, 0, 0, .36)",
                textAlignment,
                viewportDescenderBuffer = 2,
                primaryOffsetY = 1,
                secondaryOffsetY = 2
            },
            nativeAppearance = nativeAppearance ?? new Dictionary<string, object?>()
        }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static void AssertRealized(CardLyricsView lyrics, string kind)
    {
        if (!lyrics.ContentVisible || lyrics.PrimaryLine is not { } line ||
            line.BaseText.ActualWidth <= 0 || line.BaseText.ActualHeight <= 0 ||
            line.ScanText.ActualWidth <= 0 || line.ScanText.ActualHeight <= 0)
            throw new InvalidOperationException($"The {kind} lyrics fixture did not realize nonzero text.");
    }

    private static void AssertScanLayout(CardLyricsTextLine line)
    {
        if (!line.IsScanning || line.ScanText.Visibility != Visibility.Visible ||
            line.ScanClipWidth <= 0 || line.BaseText.FontSize != line.ScanText.FontSize ||
            Math.Abs(line.BaseText.ActualWidth - line.ScanText.ActualWidth) > .5 ||
            AutomationProperties.GetAccessibilityView(line.ScanText) != AccessibilityView.Raw)
            throw new InvalidOperationException("Base and scan lyric copies did not share identical layout and a visible scan clip.");
    }

    private static double AssertFontTriplet(CardLyricsTextLine line)
    {
        var size = line.BaseText.FontSize;
        if (!(size > 0) ||
            Math.Abs(line.ScanText.FontSize - size) > .01 ||
            Math.Abs(line.ShadowText.FontSize - size) > .01)
            throw new InvalidOperationException("Shadow, base, and scan text did not share the effective font size.");
        return size;
    }

    private static void AssertScanMeasurement(CardLyricsTextLine line, string scenario)
    {
        if (line.ContentWidth <= 0 ||
            Math.Abs(line.ScanClipWidth - (line.ContentWidth * .5)) > 1)
            throw new InvalidOperationException($"Scan measurement did not follow {scenario}.");
    }

    private static bool ReusedSameElements(CardLyricsView lyrics, IReadOnlyList<CardLyricsTextLine>? lines)
    {
        if (lines is null || lyrics.Lines.Count != lines.Count) return false;
        for (var index = 0; index < lines.Count; index++)
            if (!ReferenceEquals(lines[index], lyrics.Lines[index])) return false;
        return true;
    }

    private static async Task<string> CaptureAsync(FrameworkElement element, string directory, string name)
    {
        var path = Path.GetFullPath(Path.Combine(directory, name));
        await UiLayoutProbe.CaptureAsync(element, path);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException("The lyric layout capture was empty.");
        return path;
    }

    private static async Task SettleAsync(FrameworkElement root, CancellationToken token)
    {
        root.UpdateLayout();
        await Task.Delay(40, token);
        root.UpdateLayout();
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationTokenSource deadline, string failure)
    {
        while (!condition())
        {
            if (deadline.IsCancellationRequested) throw new InvalidOperationException(failure);
            try { await Task.Delay(25, deadline.Token); }
            catch (OperationCanceledException) { throw new InvalidOperationException(failure); }
        }
    }
}
