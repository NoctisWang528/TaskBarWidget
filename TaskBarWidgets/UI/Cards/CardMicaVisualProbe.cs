using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace TaskBarWidgets.UI.Cards;

// Desktop acceptance helper. The Mica material is composited by the desktop
// window manager outside the XAML visual tree, so no offscreen render can show
// it: this probe shows one real card with the shipped backdrop at a fixed
// screen rectangle for a bounded time so the material can be inspected by eye or
// captured from the desktop. It never starts the product, reads user settings,
// connects to the media host or activates a window, and the ordinary offscreen
// template validation pass never calls it.
internal static class CardMicaVisualProbe
{
    internal static async Task RunAsync(string[] args)
    {
        string? resultPath = null;
        CardWindow? card = null;
        CardLyricsView? lyrics = null;
        var windows = new List<object>();
        var totalCount = 0;
        string? error = null;
        try
        {
            // All generated files, including the optional WebView profile, stay
            // in this build's diagnostic directory, never in user configuration.
            var diagnosticRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ui-mica-check"));
            var requestedPath = Path.GetFullPath(Argument(args, "--result") ??
                Path.Combine(diagnosticRoot, $"manual-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}", "probe.json"));
            if (!requestedPath.StartsWith(diagnosticRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Mica results must be inside the build's ui-mica-check directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(requestedPath)!);
            resultPath = requestedPath;
            CardLyricsView.ProfileName = Path.Combine(Path.GetDirectoryName(resultPath)!, "webview-profile");
            var holdMilliseconds = Number(args, "--hold-ms", 9000, 2000, 60000);
            var stabilizeMilliseconds = Number(args, "--stabilize-ms", 1800, 1000, 10000);
            var samples = SampleFile(args) ?? DefaultSamples();
            totalCount = samples.Length;
            var index = Number(args, "--sample-index", 0, 0, samples.Length - 1);
            var sample = samples[index];
            var rect = new RectInt32(Number(args, "--x", 70, -32000, 32000), Number(args, "--y", 70, -32000, 32000),
                Number(args, "--width", 420, 320, 1600), Number(args, "--height", 300, 280, 1200));
            var tuning = sample.Mode == "custom"
                ? CardMicaTuning.FromValues(sample.TintOpacity, sample.LuminosityOpacity)
                : CardMicaTuning.Default;
            var vivid = sample.Mode != "system" && (sample.Mode != "default" || CardMicaTuning.DefaultVivid);
            var content = ComparisonContent();
            if (sample.Lyrics)
            {
                lyrics = new CardLyricsView { VerticalAlignment = VerticalAlignment.Top, Height = 64 };
                content.Children.Add(lyrics);
            }
            // Keep sample labels out of the window: every comparison has exactly
            // the same text and controls, including the 11/14/24-DIP text probes.
            card = new CardWindow("Mica visual diagnostic", content, rect.Height, autoHeight: false, width: rect.Width);
            card.Root.RequestedTheme = sample.Dark ? ElementTheme.Dark : ElementTheme.Light;
            card.Root.IsHitTestVisible = false;
            card.SetMicaAppearance(new CardMicaAppearance(vivid, tuning));
            if (lyrics is not null) InitializeLyrics(lyrics);
            Place(card, rect);
            await card.VisibleFrame.WaitAsync(TimeSpan.FromSeconds(5));

            var clock = Stopwatch.StartNew();
            while (!card.MicaBackdrop.Diagnostics.Connected && clock.ElapsedMilliseconds < 3000)
                await Task.Delay(100);
            var diagnostics = card.MicaBackdrop.Diagnostics;
            var stableSince = clock.ElapsedMilliseconds;
            while (clock.ElapsedMilliseconds - stableSince < stabilizeMilliseconds && clock.ElapsedMilliseconds < 15000)
            {
                await Task.Delay(100);
                var next = card.MicaBackdrop.Diagnostics;
                if (!Equals(next, diagnostics)) { diagnostics = next; stableSince = clock.ElapsedMilliseconds; }
            }
            var stabilized = clock.ElapsedMilliseconds - stableSince >= stabilizeMilliseconds;
            object? painted = lyrics is null ? null : await PaintedBackgroundsAsync(lyrics);
            // Read again after connection, stabilization and optional DOM work.
            var finalDiagnostics = card.MicaBackdrop.Diagnostics;
            stabilized &= Equals(diagnostics, finalDiagnostics);
            diagnostics = finalDiagnostics;
            var active = diagnostics.Connected && string.Equals(diagnostics.State, "Active", StringComparison.OrdinalIgnoreCase);
            var configurationMatches = diagnostics.Custom == vivid && diagnostics.Dark == sample.Dark && diagnostics.InputActive &&
                (!vivid || (diagnostics.TintOpacity is { } actualTint && diagnostics.LuminosityOpacity is { } actualLuminosity &&
                    Math.Abs(actualTint - tuning.TintOpacityFor(sample.Dark)) < .0005 &&
                    Math.Abs(actualLuminosity - tuning.LuminosityOpacityFor(sample.Dark)) < .0005));
            if (!GetWindowRect(card.Handle, out var bounds)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            windows.Add(new
            {
                label = sample.Label, mode = sample.Mode, dark = sample.Dark, vivid,
                tint = vivid ? (double?)tuning.TintOpacityFor(sample.Dark) : null,
                luminosity = vivid ? (double?)tuning.LuminosityOpacityFor(sample.Dark) : null,
                diagnostics, stabilized, configurationMatches, fallbackOrUnverified = !active,
                assessment = !active ? "FALLBACK OR UNVERIFIED: not a visual pass" :
                    !stabilized || !configurationMatches ? "UNSTABLE OR CONFIGURATION MISMATCH: not a visual pass" : "Active material; manual visual review required",
                rect = Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                requestedRect = Rect(rect.X, rect.Y, rect.Width, rect.Height),
                dpi = GetDpiForWindow(card.Handle), foreground = GetForegroundWindow() == card.Handle,
                actualTheme = card.Root.ActualTheme.ToString(), textSizes = new[] { 11, 14, 24 },
                lyrics = sample.Lyrics, painted
            });
            Write(resultPath, new { success = true, phase = "shown", sampleIndex = index, totalCount,
                holdMilliseconds, stabilizeMilliseconds, visualPass = false, readyAtUtc = DateTimeOffset.UtcNow, windows });
            var hold = Stopwatch.StartNew();
            while (hold.ElapsedMilliseconds < holdMilliseconds &&
                !(args.Contains("--wait-for-capture") && File.Exists(resultPath + ".captured")))
                await Task.Delay(100);
            if (args.Contains("--wait-for-capture") && !File.Exists(resultPath + ".captured"))
                throw new TimeoutException("No screenshot acknowledgement arrived before the hold deadline.");
            if (!Equals(diagnostics, card.MicaBackdrop.Diagnostics))
                throw new InvalidOperationException("Mica diagnostics changed between readiness and capture completion; retry this sample.");
            if (GetForegroundWindow() == card.Handle)
                throw new InvalidOperationException("The diagnostic window unexpectedly acquired foreground focus.");
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            try { lyrics?.Dispose(); } catch (Exception exception) { error ??= exception.ToString(); }
            try { card?.Close(); } catch (Exception exception) { error ??= exception.ToString(); }
            if (resultPath is not null)
                try { Write(resultPath, new { success = error is null, phase = "closed", error, totalCount, visualPass = false, windows }); }
                catch (Exception) { error ??= "Could not write the diagnostic result."; }
            Environment.ExitCode = error is null ? 0 : 1;
        }
    }

    private static StackPanel ComparisonContent()
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "Mica 材质 · Aa 0123", FontSize = 24 });
        content.Children.Add(new TextBlock { Text = "正文与控件 · Body text 0123456789", FontSize = 14 });
        content.Children.Add(new TextBlock { Text = "辅助文字 · Small text 0123456789", FontSize = 11, Opacity = .7 });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        controls.Children.Add(new Button { Content = "示例按钮", IsTabStop = false });
        controls.Children.Add(new ToggleSwitch { IsOn = true, OnContent = "已开启", IsTabStop = false });
        content.Children.Add(controls);
        content.Children.Add(new Slider { Minimum = 0, Maximum = 100, Value = 60, IsTabStop = false });
        return content;
    }

    private static void InitializeLyrics(CardLyricsView lyrics)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Media", "Web", "Lyrics"));
        var script = string.Join("\n", new[] { "bridge.js", "state.js", "presentation.js", "app.js", "browser.js" }
            .Select(file => File.ReadAllText(Path.Combine(directory, file))));
        var html = File.ReadAllText(Path.Combine(directory, "index.html"))
            .Replace("{{STYLE_CSS}}", File.ReadAllText(Path.Combine(directory, "style.css")))
            .Replace("{{APP_JS}}", script);
        lyrics.Activate();
        lyrics.Initialize(html);
        lyrics.Receive(JsonSerializer.SerializeToElement(new
        {
            revision = 1,
            trackId = "visual-probe",
            available = true,
            scripts = new[]
            {
                "window.taskbarLyrics.receive({version:1,type:'style',payload:{fontFamily:'Segoe UI',fontSize:15,layoutScalePercent:100,showCover:false,enableControlPanel:false}})",
                "window.taskbarLyrics.receive({version:1,type:'nativeAppearance',payload:{fontSize:15,layoutScalePercent:100}})",
                "window.taskbarLyrics.receive({version:1,type:'lyrics',payload:{current:'歌词块背景应当透明',next:'第二行歌词',progress:.4,currentLineIndex:0,trackId:'visual-probe',isPureMusic:false,isPlaying:false,currentTranslation:'',nextTranslation:'',translationMode:false,animateTransition:false,scene:'lyrics'}})"
            }
        }));
    }

    // Reports every element that still paints a background, so a visible block
    // inside a card can be attributed to the DOM or to the renderer surface.
    private static async Task<object> PaintedBackgroundsAsync(CardLyricsView lyrics)
    {
        if (lyrics.Renderer?.CoreWebView2 is not { } core) return new { available = false };
        const string script = """
            (()=>{
              const rows=[];
              for(const element of document.querySelectorAll('html,body,*')){
                const style=getComputedStyle(element);
                if(style.backgroundColor==='rgba(0, 0, 0, 0)'||style.backgroundColor==='transparent')continue;
                const rect=element.getBoundingClientRect();
                if(rect.width<=0||rect.height<=0)continue;
                rows.push({tag:element.tagName.toLowerCase(),id:element.id,cls:element.className&&element.className.toString?element.className.toString():'',
                  background:style.backgroundColor,x:Math.round(rect.x),y:Math.round(rect.y),width:Math.round(rect.width),height:Math.round(rect.height)});
              }
              return {body:getComputedStyle(document.body).backgroundColor,html:getComputedStyle(document.documentElement).backgroundColor,painted:rows.slice(0,12)};
            })()
            """;
        try
        {
            using var document = JsonDocument.Parse(await core.ExecuteScriptAsync(script));
            return document.RootElement.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<JsonElement>(document.RootElement.GetString()!)
                : document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new { available = false, error = exception.Message };
        }
    }

    private static void Place(CardWindow card, RectInt32 rect)
    {
        card.Window.AppWindow.IsShownInSwitchers = false;
        card.Window.AppWindow.MoveAndResize(rect);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(card.Window);
        // Stay above the user's windows without ever taking focus.
        SetWindowLongPtr(handle, -20, (nint)(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L | 0x00000008L));
        card.Show(animate: false);
    }

    private static object Rect(int x, int y, int width, int height) => new { x, y, width, height };

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Write(string path, object payload)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    private static int Number(string[] args, string name, int fallback, int minimum, int maximum)
    {
        var text = Argument(args, name);
        if (text is null) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        return value;
    }

    private static Sample[] DefaultSamples() => new[] { false, true }.SelectMany(dark =>
    {
        var theme = dark ? "深色" : "浅色";
        var tuning = CardMicaTuning.Default;
        return new[]
        {
            new Sample($"{theme} · system", dark, 0, 0, false, "system"),
            new Sample(FormattableString.Invariant($"{theme} · current default (tint {tuning.TintOpacityFor(dark):0.##}, luminosity {tuning.LuminosityOpacityFor(dark):0.##})"), dark, 0, 0, false, "default"),
            new Sample($"{theme} · tint 0 / luminosity 0.85", dark, 0, .85, false, "custom"),
            new Sample($"{theme} · tint 0 / luminosity 0.65", dark, 0, .65, false, "custom"),
            new Sample($"{theme} · tint 0 / luminosity 0.45", dark, 0, .45, false, "custom")
        };
    }).ToArray();

    // Legacy parameter grids remain custom by default; mode may also be system/default.
    private static Sample[]? SampleFile(string[] args)
    {
        var path = Argument(args, "--samples-file");
        if (path is null) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var samples = new List<Sample>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var mode = item.TryGetProperty("mode", out var modeElement) ? modeElement.GetString() : "custom";
            if (mode is not ("custom" or "system" or "default")) throw new ArgumentException("Unknown Mica sample mode.");
            samples.Add(new Sample(
                item.GetProperty("label").GetString() ?? "样本",
                item.TryGetProperty("dark", out var dark) && dark.GetBoolean(),
                item.TryGetProperty("tint", out var tint) ? tint.GetDouble() : 0.0,
                item.TryGetProperty("luminosity", out var luminosity) ? luminosity.GetDouble() : 0.85,
                item.TryGetProperty("lyrics", out var lyrics) && lyrics.GetBoolean(), mode));
        }
        return samples.Count > 0 ? samples.ToArray() : throw new ArgumentException("Sample file is empty.");
    }

    private readonly record struct Sample(string Label, bool Dark, double TintOpacity, double LuminosityOpacity, bool Lyrics, string Mode);
}
