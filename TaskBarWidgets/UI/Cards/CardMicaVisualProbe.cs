using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace TaskBarWidgets.UI.Cards;

// Desktop acceptance helper. The Mica material is composited by the desktop
// window manager outside the XAML visual tree, so no offscreen render can show
// it: this probe shows real card windows with the shipped backdrop on the
// primary monitor for a bounded time so the material can be inspected by eye or
// captured from the desktop. It never starts the product, reads user settings,
// connects to the media host or activates a window, and the ordinary offscreen
// template validation pass never calls it.
internal static class CardMicaVisualProbe
{
    internal static async Task RunAsync(string[] args)
    {
        CardLyricsView.ProfileName = "CardWebView2-visual-probe";
        var resultPath = Argument(args, "--result") ?? Path.Combine(AppContext.BaseDirectory, "mica-visual-check.json");
        var holdMilliseconds = int.TryParse(Argument(args, "--hold-ms"), out var parsedHold) ? Math.Clamp(parsedHold, 2000, 60000) : 9000;
        var samples = SampleFile(args) ?? new[]
        {
            new Sample("浅色 浓度45 通透度5（当前默认）", false, 0.45, 0.95, false),
            new Sample("浅色 浓度45 通透度90", false, 0.45, 0.10, false),
            new Sample("浅色 浓度0 通透度15", false, 0.00, 0.85, false),
            new Sample("深色 浓度45 通透度10（当前默认）", true, 0.45, 0.90, false),
            new Sample("深色 浓度15 通透度4", true, 0.15, 0.96, false),
            new Sample("歌词块（浅色 浓度45 通透度5）", false, 0.45, 0.95, true)
        };
        var cards = new List<CardWindow>();
        var windows = new List<object>();
        string? error = null;
        try
        {
            for (var index = 0; index < samples.Length; index++)
            {
                var sample = samples[index];
                var rect = new RectInt32(70 + index % 3 * 360, 70 + index / 3 * 160, 320, 128);
                if (sample.Lyrics)
                {
                    var (lyricsCard, background) = await CreateLyricsCardAsync(sample, rect);
                    cards.Add(lyricsCard);
                    windows.Add(new { label = sample.Label, dark = sample.Dark, tint = sample.TintOpacity, luminosity = sample.LuminosityOpacity, rect = Rect(rect.X, rect.Y, rect.Width, rect.Height), lyrics = true, painted = background });
                    continue;
                }
                var content = new StackPanel { Spacing = 6 };
                content.Children.Add(new TextBlock { Text = sample.Label, FontSize = 13, TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new TextBlock { Text = "Mica 材质验收样本", FontSize = 11, Opacity = .7 });
                var card = new CardWindow(sample.Label, content, 128, autoHeight: false, width: 320);
                card.Root.RequestedTheme = sample.Dark ? ElementTheme.Dark : ElementTheme.Light;
                card.SetMicaTuning(CardMicaTuning.FromValues(sample.TintOpacity, sample.LuminosityOpacity));
                Place(card, rect);
                cards.Add(card);
                windows.Add(new { label = sample.Label, dark = sample.Dark, tint = sample.TintOpacity, luminosity = sample.LuminosityOpacity, rect = Rect(rect.X, rect.Y, rect.Width, rect.Height), lyrics = false, painted = (object?)null });
            }
            Write(resultPath, new { success = true, phase = "shown", holdMilliseconds, windows });
            await Task.Delay(holdMilliseconds);
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            foreach (var card in cards)
            {
                try { card.Close(); } catch (COMException) { }
            }
            Write(resultPath, new { success = error is null, phase = "closed", error, windows });
            Environment.ExitCode = error is null ? 0 : 1;
        }
    }

    private static async Task<(CardWindow Card, object Painted)> CreateLyricsCardAsync(Sample sample, RectInt32 rect)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Media", "Web", "Lyrics"));
        var script = string.Join("\n", new[] { "bridge.js", "state.js", "presentation.js", "app.js", "browser.js" }
            .Select(file => File.ReadAllText(Path.Combine(directory, file))));
        var html = File.ReadAllText(Path.Combine(directory, "index.html"))
            .Replace("{{STYLE_CSS}}", File.ReadAllText(Path.Combine(directory, "style.css")))
            .Replace("{{APP_JS}}", script);
        var lyrics = new CardLyricsView { VerticalAlignment = VerticalAlignment.Top, Height = 64 };
        var card = new CardWindow("歌词块", lyrics, 128, autoHeight: false, width: 320);
        card.Root.RequestedTheme = sample.Dark ? ElementTheme.Dark : ElementTheme.Light;
        card.SetMicaTuning(CardMicaTuning.FromValues(sample.TintOpacity, sample.LuminosityOpacity));
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
        Place(card, rect);
        await Task.Delay(1200);
        return (card, await PaintedBackgroundsAsync(lyrics));
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
        try { File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    // Optional parameter grid: {"label":"…","dark":false,"tint":0.1,"luminosity":0.85,"lyrics":false}
    private static Sample[]? SampleFile(string[] args)
    {
        var path = Argument(args, "--samples-file");
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var samples = new List<Sample>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                samples.Add(new Sample(
                    item.GetProperty("label").GetString() ?? "样本",
                    item.TryGetProperty("dark", out var dark) && dark.GetBoolean(),
                    item.TryGetProperty("tint", out var tint) ? tint.GetDouble() : 0.0,
                    item.TryGetProperty("luminosity", out var luminosity) ? luminosity.GetDouble() : 0.85,
                    item.TryGetProperty("lyrics", out var lyrics) && lyrics.GetBoolean()));
            }
            return samples.Count > 0 ? samples.ToArray() : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private readonly record struct Sample(string Label, bool Dark, double TintOpacity, double LuminosityOpacity, bool Lyrics);
}
