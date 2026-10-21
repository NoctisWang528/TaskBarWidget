using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.UI.Settings;
using Windows.UI.ViewManagement;

namespace TaskBarWidgets.UI.Cards;

internal sealed class CardWindow
{
    private int _motionGeneration;
    private bool _closed;
    private bool _prewarmed;
    private TaskCompletionSource<bool>? _visibleFrame;
    internal Window Window { get; }
    internal Grid Root { get; }
    internal nint Handle { get; }
    internal double Height { get; private set; }
    internal double Width { get; }
    internal FrameworkElement Content => _content;
    // Thin themed wash shared with the settings sections.
    internal Brush? SurfaceBackground => _surface.Background;
    internal VividMicaBackdrop MicaBackdrop => _micaBackdrop;
    internal CardMicaTuning MicaTuning => _micaBackdrop.Tuning;
    // False keeps the plain system Mica material instead of the tuned vivid one.
    internal bool UsesVividMica => _vividMica;
    // True once the window has rendered a frame while hidden, so the first
    // click skips window creation and XAML realization.
    internal bool Prewarmed => _prewarmed;
    // Completes once the fade has started on a real composition frame for the
    // most recent Show; used by timing and the offscreen template probe.
    internal Task VisibleFrame => _visibleFrame?.Task ?? Task.CompletedTask;
    private readonly FrameworkElement _content;
    private readonly Grid _surface;
    private readonly VividMicaBackdrop _micaBackdrop;
    private bool _vividMica = true;
    private readonly bool _autoHeight;

    internal CardWindow(string title, FrameworkElement content, double height, bool autoHeight = false, double width = CardGroup.CardWidth)
    {
        Height = height; Width = width;
        _content = content; _autoHeight = autoHeight;
        Root = new Grid();
        // Keep the content wash thin so the window Mica remains visible.
        var surface = new Grid { Width = width, Height = height, Padding = new Thickness(16) };
        UiThemeStyles.Apply(surface, "CardWindowSurface");
        _surface = surface;
        surface.Children.Add(content);
        Root.Children.Add(new Viewbox { Child = surface, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        _micaBackdrop = new VividMicaBackdrop(alwaysInputActive: true);
        _micaBackdrop.StatusChanged += UpdateMicaFallback;
        Window = new Window { Title = "TaskBarWidgets · " + title, Content = Root, SystemBackdrop = _micaBackdrop };
        UpdateMicaFallback();
        Window.Closed += (_, _) => { _closed = true; _motionGeneration++; _micaBackdrop.StatusChanged -= UpdateMicaFallback; };
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        Window.AppWindow.IsShownInSwitchers = false;
        if (Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
    }

    internal void MeasureHeight()
    {
        if (!_autoHeight || _closed) return;
        _content.Measure(new Windows.Foundation.Size(Width - 32, double.PositiveInfinity));
        Height = Math.Ceiling(_content.DesiredSize.Height + 32);
        _surface.Height = Height;
    }

    internal void SetMinimumWidth(double width)
    {
        if (_closed) return;
        Root.MinWidth = width;
        _surface.Width = Math.Max(Width, width);
    }

    internal void SetMicaTuning(CardMicaTuning tuning) => _micaBackdrop.UpdateTuning(tuning);

    private void UpdateMicaFallback()
    {
        if (_closed) return;
        if (_micaBackdrop.NeedsSolidFallback) UiThemeStyles.Apply(Root, "MicaFallbackRoot");
        else Root.ClearValue(FrameworkElement.StyleProperty);
    }

    internal void SetMicaAppearance(CardMicaAppearance appearance)
    {
        if (_closed) return;
        _vividMica = appearance.Vivid;
        _micaBackdrop.UpdateAppearance(appearance);
    }

    internal void SetVividMica(bool vivid) => SetMicaAppearance(new(vivid, MicaTuning));

    internal void Show(bool animate = true)
    {
        if (_closed) return;
        var generation = ++_motionGeneration;
        var visibleFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _visibleFrame = visibleFrame;
        var visual = RootVisual;
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Offset));
        visual.Opacity = 0;
        visual.Offset = Vector3.Zero;
        // The shipped template probe exercises this non-activating initialization.
        // The group activates only its final card after all are shown.
        Window.AppWindow.Show(false);
        // The group repositions and resizes the window right before showing, so
        // wait one composition frame before the fade reveals any pixels.
        _ = CompleteShowAsync(generation, visual, visibleFrame, animate);
    }

    // Render one invisible frame at the current position, then hide again.
    // Called during startup prewarm so the first user click carries no cold cost.
    internal async Task PrewarmAsync()
    {
        if (_closed || _prewarmed) return;
        var generation = ++_motionGeneration;
        var visual = RootVisual;
        var wasVisible = IsWindowVisible(Handle);
        visual.Opacity = 0;
        visual.Offset = Vector3.Zero;
        Window.AppWindow.Show(false);
        await WaitForFrameAsync();
        if (_closed) return;
        if (generation != _motionGeneration)
        {
            // Something else owns the window now; never hide it behind its back.
            if (!wasVisible) Window.AppWindow.Hide();
            return;
        }
        _prewarmed = true;
        Window.AppWindow.Hide();
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Offset));
        visual.Opacity = 1;
        visual.Offset = Vector3.Zero;
        CardLifecycleLog.Write($"prewarm-first-frame hwnd=0x{Handle:X}");
    }

    private async Task CompleteShowAsync(int generation, Visual visual, TaskCompletionSource<bool> visibleFrame, bool animate)
    {
        await WaitForFrameAsync();
        // The compositor animates from the value already committed on the
        // hidden frame, so the first visible pixels are the real card surface.
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Apply()
        {
            if (_closed || generation != _motionGeneration) { completed.TrySetResult(false); return; }
            try
            {
                if (animate && new UISettings().AnimationsEnabled) Animate(visual, true);
                else { visual.Opacity = 1; visual.Offset = Vector3.Zero; }
            }
            catch (Exception exception) when (exception is COMException or ObjectDisposedException)
            {
                // A compositor failure must never leave the card stuck invisible.
                CardLifecycleLog.Write($"show-animate-error hwnd=0x{Handle:X} hr=0x{exception.HResult:X8}");
                try { visual.Opacity = 1; visual.Offset = Vector3.Zero; }
                catch (Exception fallback) when (fallback is COMException or ObjectDisposedException) { }
            }
            completed.TrySetResult(true);
        }
        // The visual must be updated on the UI thread; a frame callback can
        // arrive on the compositor thread.
        if (Root.DispatcherQueue.HasThreadAccess) Apply();
        else Root.DispatcherQueue.TryEnqueue(Apply);
        if (await completed.Task) visibleFrame.TrySetResult(true);
    }

    // One composition frame, bounded so a stalled compositor cannot leave a
    // card permanently invisible.
    private static async Task WaitForFrameAsync()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, object args) { gate.TrySetResult(true); }
        CompositionTarget.Rendering += OnRendering;
        try { await gate.Task.WaitAsync(TimeSpan.FromMilliseconds(250)); }
        catch (TimeoutException) { }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    private Visual RootVisual => ElementCompositionPreview.GetElementVisual(Root);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);

    internal async Task HideAsync(bool? animate = null)
    {
        if (_closed) return;
        var generation = ++_motionGeneration;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(Root);
            // The offscreen probe can force this branch without changing the OS
            // animation preference. Production callers always use the system value.
            if (animate ?? new UISettings().AnimationsEnabled)
            {
                Animate(visual, false);
                await Task.Delay(150);
            }
            if (_closed || generation != _motionGeneration) return;
            Window.AppWindow.Hide();
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation(nameof(Visual.Offset));
            visual.Opacity = 1;
            visual.Offset = Vector3.Zero;
        }
        catch (Exception exception) when (exception is COMException or ObjectDisposedException)
        {
            CardLifecycleLog.Write($"hide-error hwnd=0x{Handle:X} closed={_closed} hr=0x{exception.HResult:X8}");
            if (_closed || generation != _motionGeneration) return;
            try { Window.AppWindow.Hide(); }
            catch (Exception fallback) when (fallback is COMException or ObjectDisposedException)
            { CardLifecycleLog.Write($"hide-fallback-error hwnd=0x{Handle:X} hr=0x{fallback.HResult:X8}"); }
        }
    }

    private static void Animate(Visual visual, bool opening)
    {
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = TimeSpan.FromMilliseconds(150);
        opacity.InsertKeyFrame(0, opening ? 0 : 1);
        opacity.InsertKeyFrame(1, opening ? 1 : 0);
        visual.StartAnimation(nameof(Visual.Opacity), opacity);
        var offset = visual.Compositor.CreateVector3KeyFrameAnimation();
        offset.Duration = opacity.Duration;
        offset.InsertKeyFrame(0, new Vector3(0, opening ? 6 : 0, 0));
        offset.InsertKeyFrame(1, new Vector3(0, opening ? 0 : 6, 0));
        visual.StartAnimation(nameof(Visual.Offset), offset);
    }

    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        _motionGeneration++;
        if (_surface.Children.Contains(_content)) _surface.Children.Remove(_content);
        Root.Children.Clear();
        Window.Close();
    }
}
