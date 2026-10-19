using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.UI.ViewManagement;

namespace TaskBarWidgets.UI.Cards;

// One renderer, latest complete frame only. Hidden cards never execute queued frames.
internal sealed class CardLyricsView : Grid, IDisposable
{
    private WebView2? _web;
    internal WebView2? Renderer => _web;
    private Task? _initializing;
    private string? _html;
    private JsonElement _pending;
    private long _revision = -1;
    private int _generation;
    private bool _active, _ready, _applying, _applyAgain, _disposed;
    private string _displayedTrack = "";
    private readonly Dictionary<string, string> _appliedStyles = [];
    private static readonly SemaphoreSlim EnvironmentGate = new(1, 1);
    private static CoreWebView2Environment? _sharedEnvironment;
    private static string? _sharedProfile;

    internal CardLyricsView()
    {
        IsHitTestVisible = false;
        Margin = new Thickness(0, 6, 0, 6);
        ActualThemeChanged += (_, _) => { _appliedStyles.Clear(); if (_active) _ = ApplyAsync(); };
    }
    internal void Activate()
    {
        _active = true; _revision = -1; ++_generation;
        if (!_ready && _initializing?.IsCompleted == true) ResetRenderer();
    }
    private void ResetRenderer()
    {
        _web?.Close(); Children.Clear(); _web = null; _initializing = null; _ready = false; _appliedStyles.Clear();
    }
    internal bool HasRenderer => _web is not null;
    internal void SetMemoryLevel(bool low)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try { core.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
    internal void ReleaseRenderer()
    {
        ResetRenderer();
        _html = null;
        _pending = default;
        _displayedTrack = "";
    }
    internal void HideLyrics()
    {
        _active = false; ++_generation; _pending = default;
        if (_web is not null) _web.Visibility = Visibility.Collapsed;
    }
    internal void Initialize(string html)
    {
        _html = html;
        if (_active && _pending.ValueKind == JsonValueKind.Object && _pending.GetProperty("available").GetBoolean()) EnsureRenderer();
    }
    internal void Receive(JsonElement frame)
    {
        if (!_active || _disposed || frame.ValueKind != JsonValueKind.Object) return;
        var revision = frame.GetProperty("revision").GetInt64();
        if (revision < _revision) return;
        _revision = revision; _pending = frame.Clone();
        var available = frame.GetProperty("available").GetBoolean();
        if (_web is not null && (!available || frame.GetProperty("trackId").GetString() != _displayedTrack)) _web.Visibility = Visibility.Collapsed;
        if (available) { EnsureRenderer(); _ = ApplyAsync(); }
    }
    private void EnsureRenderer()
    {
        if (_initializing is null && _html is not null) _initializing = InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        try
        {
            _web = new WebView2 { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent, Visibility = Visibility.Collapsed, IsTabStop = false };
            Children.Add(_web);
            var profile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskBarWidgets", "CardWebView2");
            var environment = await SharedEnvironmentAsync(profile);
            if (_disposed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_disposed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            var mediaRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "Media"));
            core.SetVirtualHostNameToFolderMapping("taskbarwidgets.local", mediaRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            var expectedDocument = true;
            core.NavigationStarting += (_, args) => {
                // Match the native host policy: this runtime exposes NavigateToString as data:.
                var hostDocument = expectedDocument && args.Uri.StartsWith("data:text/html;charset=utf-8;base64,", StringComparison.Ordinal) && args.Uri.Length <= 8 * 1024 * 1024;
                args.Cancel = args.IsUserInitiated || args.IsRedirected || !(hostDocument || args.Uri == "about:blank");
                if (!args.Cancel && hostDocument) expectedDocument = false;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) => {
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || (uri.Host != "taskbarwidgets.local" && uri.Scheme != "data" && uri.AbsoluteUri != "about:blank"))
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.NavigationCompleted += (sender, args) => {
                if (_disposed) return;
                _ready = args.IsSuccess;
                if (_ready && _active) _ = ApplyAsync();
            };
            core.ProcessFailed += (_, _) => {
                _ready = false;
                if (_web is not null) _web.Visibility = Visibility.Collapsed;
                CardLifecycleLog.Write("card-lyrics renderer-process-failed");
            };
            var html = _html!.Replace("<head>", "<head><script>window.taskbarCardHost=true;</script>")
                .Replace("</head>", "<style>.card-single-line #nextLine,.card-single-line #incomingLine,.card-single-line #incomingTranslationPair{visibility:hidden!important}html,body{pointer-events:none}.card-reduced-motion *{animation:none!important;transition:none!important}</style></head>")
                .Replace("</body>", "<script>" + CardScript + "</script></body>");
            core.NavigateToString(html);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ready = false;
            CardLifecycleLog.Write($"card-lyrics initialize-failed hr=0x{error.HResult:X8}");
        }
    }
    private static async Task<CoreWebView2Environment> SharedEnvironmentAsync(string profile)
    {
        if (_sharedEnvironment is not null && string.Equals(_sharedProfile, profile, StringComparison.OrdinalIgnoreCase)) return _sharedEnvironment;
        await EnvironmentGate.WaitAsync();
        try {
            if (_sharedEnvironment is null || !string.Equals(_sharedProfile, profile, StringComparison.OrdinalIgnoreCase)) {
                _sharedEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, new CoreWebView2EnvironmentOptions());
                _sharedProfile = profile;
            }
            return _sharedEnvironment;
        }
        finally { EnvironmentGate.Release(); }
    }
    private async Task ApplyAsync()
    {
        if (_applying) { _applyAgain = true; return; }
        if (_disposed || !_active || !_ready || _web?.CoreWebView2 is null || _pending.ValueKind != JsonValueKind.Object) return;
        _applying = true;
        _applyAgain = false;
        var generation = _generation;
        try
        {
            long applied;
            do
            {
                applied = _revision;
                var frame = _pending;
                if (!frame.GetProperty("available").GetBoolean()) { _web.Visibility = Visibility.Collapsed; return; }
                var options = JsonSerializer.Serialize(new { dark = ActualTheme == ElementTheme.Dark, reduced = !new UISettings().AnimationsEnabled });
                var scripts = new List<string>();
                foreach (var entry in frame.GetProperty("scripts").EnumerateArray())
                {
                    var script = entry.GetString() ?? "";
                    var slot = script.Contains("nativeAppearance", StringComparison.Ordinal) ? "nativeAppearance" :
                        script.Contains("\"type\":\"style\"", StringComparison.Ordinal) ? "style" : null;
                    if (slot is not null)
                    {
                        if (_appliedStyles.TryGetValue(slot, out var previous) && previous == script) continue;
                        _appliedStyles[slot] = script;
                    }
                    scripts.Add(script);
                }
                await _web.ExecuteScriptAsync("window.taskbarCardConfigure(" + options + ");" + string.Join(";\n", scripts));
                if (_disposed || !_active || generation != _generation) return;
                if (applied == _revision)
                {
                    _displayedTrack = frame.GetProperty("trackId").GetString() ?? "";
                    _web.Visibility = _pending.GetProperty("available").GetBoolean() ? Visibility.Visible : Visibility.Collapsed;
                }
            } while (applied != _revision);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _appliedStyles.Clear();
            if (_web is not null) _web.Visibility = Visibility.Collapsed;
            CardLifecycleLog.Write($"card-lyrics frame-failed hr=0x{error.HResult:X8}");
        }
        finally
        {
            _applying = false;
            if (_applyAgain && !_disposed && _active) { _applyAgain = false; _ = ApplyAsync(); }
        }
    }
    private const string CardScript = """
        (()=>{
          const receive=window.taskbarLyrics.receive.bind(window.taskbarLyrics);
          let options={dark:false,reduced:false};
          window.taskbarCardConfigure=o=>{options=o;document.documentElement.classList.toggle('card-reduced-motion',o.reduced);};
          window.taskbarLyrics.receive=m=>{
            if(m.type==='style'||m.type==='nativeAppearance'){
              const c=options.dark?'255,255,255':'20,20,20';
              m={...m,payload:{...m.payload,showCover:false,enableControlPanel:false,surfaceColor:'transparent',surfaceShadow:'none',
                primaryColor:`rgba(${c},.95)`,secondaryColor:`rgba(${c},.65)`,translationColor:`rgba(${c},.7)`,wordScanOverlayColor:options.dark?'#8ed8ff':'#005fb8'}};
            }
            if(m.type==='lyrics')m={...m,payload:{...m.payload,
              translationMode:m.payload.translationMode&&!document.documentElement.classList.contains('card-single-line'),
              animateTransition:!options.reduced}};
            receive(m);
          };
        })();
        """;
    public void Dispose()
    {
        _disposed = true; HideLyrics(); _web?.Close(); Children.Clear();
    }
}
