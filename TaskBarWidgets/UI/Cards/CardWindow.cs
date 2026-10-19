using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace TaskBarWidgets.UI.Cards;

internal sealed class CardWindow
{
    private int _motionGeneration;
    private bool _closed;
    internal Window Window { get; }
    internal Grid Root { get; }
    internal nint Handle { get; }
    internal double Height { get; private set; }
    internal double Width { get; }
    internal FrameworkElement Content => _content;
    private readonly FrameworkElement _content;
    private readonly Grid _surface;
    private readonly bool _autoHeight;

    internal CardWindow(string title, FrameworkElement content, double height, bool autoHeight = false, double width = CardGroup.CardWidth)
    {
        Height = height; Width = width;
        _content = content; _autoHeight = autoHeight;
        Root = new Grid();
        var surface = new Grid { Width = width, Height = height, Padding = new Thickness(16) };
        _surface = surface;
        surface.Children.Add(content);
        Root.Children.Add(new Viewbox { Child = surface, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        Window = new Window { Title = "TaskBarWidgets · " + title, Content = Root, SystemBackdrop = new MicaBackdrop() };
        Window.Closed += (_, _) => { _closed = true; _motionGeneration++; };
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

    internal void Show()
    {
        if (_closed) return;
        ++_motionGeneration;
        // The shipped template probe exercises this non-activating initialization.
        // The group activates only its final media card after all three are shown.
        Window.AppWindow.Show(false);
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Offset));
        visual.Opacity = 1;
        visual.Offset = Vector3.Zero;
        if (new UISettings().AnimationsEnabled) Animate(visual, true);
    }

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
