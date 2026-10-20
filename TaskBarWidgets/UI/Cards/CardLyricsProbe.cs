using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace TaskBarWidgets.UI.Cards;

internal static class CardLyricsProbe
{
    // Real shipped HTML/JS, synthetic complete frames, and a nonactivating,
    // offscreen HWND. No native/media host, settings or player is started.
    internal static async Task<object> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var elapsed = Stopwatch.StartNew();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Media", "Web", "Lyrics"));
        var script = string.Join("\n", new[] { "bridge.js", "state.js", "presentation.js", "app.js", "browser.js" }
            .Select(file => File.ReadAllText(Path.Combine(directory, file))));
        var html = File.ReadAllText(Path.Combine(directory, "index.html"))
            .Replace("{{STYLE_CSS}}", File.ReadAllText(Path.Combine(directory, "style.css")))
            .Replace("{{APP_JS}}", script);
        var lyrics = new CardLyricsView { Height = 28, VerticalAlignment = VerticalAlignment.Top, RequestedTheme = ElementTheme.Dark };
        var root = new Grid(); root.Children.Add(lyrics);
        var host = new Window { Content = root, Title = "Card lyrics component probe" };
        var activated = false;
        host.Activated += (_, args) => { if (args.WindowActivationState != WindowActivationState.Deactivated) activated = true; };
        var checks = new List<object>();
        try
        {
            UiTemplateProbe.PreventProbeActivation(host);
            host.AppWindow.IsShownInSwitchers = false;
            host.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, 480, 240));
            host.AppWindow.Show(false);
            await Task.Delay(60, deadline.Token);
            root.UpdateLayout();
            if (!lyrics.IsLoaded || lyrics.ActualWidth <= 0 || lyrics.ActualHeight <= 0)
                throw new InvalidOperationException("Lyrics probe host did not realize its content.");
            lyrics.Activate(); lyrics.Initialize(html);
            lyrics.Receive(Frame(1, "probe-a", "Synthetic lyric alpha", "Synthetic next alpha"));
            var renderer = lyrics.Renderer ?? throw new InvalidOperationException("Lyrics renderer was not created.");
            if (!renderer.Resources.TryGetValue("BrushForThemeBackgroundColor", out var bridgeBrush) ||
                bridgeBrush is not Microsoft.UI.Xaml.Media.SolidColorBrush { Color.A: 0 })
                throw new InvalidOperationException("The lyrics renderer kept an opaque system visual bridge background.");
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            renderer.NavigationCompleted += (_, args) => {
                if (args.IsSuccess) navigation.TrySetResult(true);
                else navigation.TrySetException(new InvalidOperationException("Lyrics HTML navigation failed: " + args.WebErrorStatus));
            };
            await navigation.Task.WaitAsync(deadline.Token);
            checks.Add(new { kind = "navigation", success = true, realShippedBundle = true });

            async Task<JsonElement> ReadAsync()
            {
                async Task<string> ExecuteAsync() => await renderer.ExecuteScriptAsync(ReadState);
                using var state = JsonDocument.Parse(await ExecuteAsync().WaitAsync(deadline.Token));
                return state.RootElement.Clone();
            }
            async Task<JsonElement> UntilAsync(Func<JsonElement, bool> matches, string failure)
            {
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var state = await ReadAsync();
                    if (state.ValueKind == JsonValueKind.Object && matches(state)) return state;
                    try { await Task.Delay(40, deadline.Token); }
                    catch (OperationCanceledException) { throw new InvalidOperationException(failure); }
                }
            }
            bool HasText(JsonElement state, string expected) => state.GetProperty("current").GetString() == expected &&
                state.GetProperty("currentWidth").GetDouble() > 0 && state.GetProperty("currentHeight").GetDouble() > 0;
            var narrow = await UntilAsync(state => HasText(state, "Synthetic lyric alpha") && state.GetProperty("single").GetBoolean() &&
                state.GetProperty("nextVisibility").GetString() == "hidden" && renderer.Visibility == Visibility.Visible &&
                state.GetProperty("bodyBackground").GetString() == "rgba(0, 0, 0, 0)" &&
                state.GetProperty("surfaceBackground").GetString() == "rgba(0, 0, 0, 0)",
                "Narrow lyrics did not display a nonempty single line.");
            checks.Add(new { kind = "narrow", state = narrow });

            lyrics.Height = 92; root.UpdateLayout();
            var tall = await UntilAsync(state => HasText(state, "Synthetic lyric alpha") && !state.GetProperty("single").GetBoolean() &&
                state.GetProperty("nextVisibility").GetString() == "visible" && state.GetProperty("next").GetString() == "Synthetic next alpha",
                "Taller lyrics did not restore both synchronized lines.");
            checks.Add(new { kind = "tall", state = tall });

            lyrics.RequestedTheme = ElementTheme.Light;
            var light = await UntilAsync(state => HasText(state, "Synthetic lyric alpha") &&
                state.GetProperty("primary").GetString()!.Replace(" ", "").Contains("20,20,20"),
                "A theme-only change did not recolor the existing lyric frame.");
            var darkPrimary = narrow.GetProperty("primary").GetString()!;
            if (!darkPrimary.Replace(" ", "").Contains("255,255,255") || darkPrimary == light.GetProperty("primary").GetString())
                throw new InvalidOperationException("Dark and light lyric themes did not resolve distinct readable colors.");
            checks.Add(new { kind = "theme", darkPrimary, light = light.GetProperty("primary").GetString() });

            lyrics.HideLyrics();
            lyrics.Receive(Frame(999, "ignored-hidden", "Hidden frame must be dropped", "Hidden next"));
            await Task.Delay(60, deadline.Token);
            var hidden = await ReadAsync();
            if (renderer.Visibility != Visibility.Collapsed || !HasText(hidden, "Synthetic lyric alpha") || hidden.GetProperty("revision").GetInt32() != 1)
                throw new InvalidOperationException("A hidden lyric frame executed or made the renderer visible.");
            lyrics.Activate();
            lyrics.Receive(Frame(2, "probe-a", "Synthetic lyric alpha", "Synthetic next alpha"));
            await UntilAsync(state => state.GetProperty("revision").GetInt32() == 2 && renderer.Visibility == Visibility.Visible,
                "Reopening did not apply its current snapshot.");
            checks.Add(new { kind = "hiddenFrame", ignored = true });

            // Keep ExecuteScriptAsync in flight across hide/show. This catches a
            // lost new-generation frame when the previous apply owns the pump.
            lyrics.Receive(Frame(3, "probe-a", "Old frame must not survive", "Old next", delay: true));
            lyrics.HideLyrics();
            if (renderer.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Hide did not immediately collapse lyrics.");
            lyrics.Activate();
            lyrics.Receive(Frame(4, "probe-b", "Synthetic lyric beta", "Synthetic next beta"));
            var reopened = await UntilAsync(state => HasText(state, "Synthetic lyric beta") && state.GetProperty("revision").GetInt32() == 4 &&
                renderer.Visibility == Visibility.Visible, "Reopening retained an old track or lost the new-generation lyric frame.");
            checks.Add(new { kind = "hideReopenDuringApply", state = reopened, hiddenFrameDropped = true });

            lyrics.Receive(JsonSerializer.SerializeToElement(new { revision = 5, trackId = "", available = false, scripts = Array.Empty<string>() }));
            if (renderer.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Unavailable lyrics remained visible.");
            var position = host.AppWindow.Position;
            var size = host.AppWindow.Size;
            if (activated || position.X + size.Width >= -20000 || position.Y + size.Height >= -20000)
                throw new InvalidOperationException("Lyrics probe activated or left the offscreen region.");
            checks.Add(new { kind = "unavailable", collapsed = true });
            return new { checks, durationMs = elapsed.ElapsedMilliseconds, probeActivated = activated, syntheticFrames = true,
                actualPlayerTimingVerified = false, wordScanAnimationVisuallyVerified = false };
        }
        finally { lyrics.Dispose(); host.Close(); }
    }

    private static JsonElement Frame(int revision, string trackId, string current, string next, bool delay = false)
    {
        string Message(string type, object payload) => "window.taskbarLyrics.receive(" + JsonSerializer.Serialize(new { version = 1, type, payload }) + ")";
        var scripts = new[] {
            delay ? "(()=>{const start=performance.now();while(performance.now()-start<120){}})()" : "void 0",
            Message("style", new { fontFamily = "Segoe UI", fontSize = 18, layoutScalePercent = 100, showCover = false, enableControlPanel = false }),
            Message("nativeAppearance", new { fontSize = 18, layoutScalePercent = 100 }),
            Message("lyrics", new { current, next, progress = .4, currentLineIndex = 0, trackId, isPureMusic = false, isPlaying = false,
                currentTranslation = "", nextTranslation = "", translationMode = false, animateTransition = false, scene = "lyrics" }),
            "window.__taskbarProbeRevision=" + revision
        };
        return JsonSerializer.SerializeToElement(new { revision, trackId, available = true, scripts });
    }

    private const string ReadState = """
        (()=>{
          const current=document.getElementById('currentLineText'),next=document.getElementById('nextLine');
          if(!current||!next)return null;
          const bounds=current.getBoundingClientRect();
          return {current:current.textContent,next:document.getElementById('nextLineText').textContent,
            single:document.documentElement.classList.contains('card-single-line'),nextVisibility:getComputedStyle(next).visibility,
            primary:getComputedStyle(document.documentElement).getPropertyValue('--primary').trim(),
            bodyBackground:getComputedStyle(document.body).backgroundColor,
            surfaceBackground:getComputedStyle(document.getElementById('layout')).backgroundColor,
            currentWidth:bounds.width,currentHeight:bounds.height,revision:window.__taskbarProbeRevision||0,
            viewportWidth:innerWidth,viewportHeight:innerHeight};
        })()
        """;
}
