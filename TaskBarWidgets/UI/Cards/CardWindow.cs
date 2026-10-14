using System.Numerics;
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
    private bool _initialized;
    internal Window Window { get; }
    internal Grid Root { get; }
    internal nint Handle { get; }
    internal double Height { get; }

    internal CardWindow(string title, FrameworkElement content, double height)
    {
        Height = height;
        Root = new Grid();
        var surface = new Grid { Width = CardGroup.CardWidth, Height = height, Padding = new Thickness(16) };
        surface.Children.Add(content);
        Root.Children.Add(new Viewbox { Child = surface, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        Window = new Window { Title = "TaskBarWidgets · " + title, Content = Root, SystemBackdrop = new MicaBackdrop() };
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

    internal void Show()
    {
        ++_motionGeneration;
        // First activation initializes the WinUI XAML tree. This only runs for the
        // user's explicit panel-open request; hidden construction never activates.
        if (!_initialized) { _initialized = true; Window.Activate(); }
        else Window.AppWindow.Show(false);
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Offset));
        visual.Opacity = 1;
        visual.Offset = Vector3.Zero;
        if (new UISettings().AnimationsEnabled) Animate(visual, true);
    }

    internal async void Hide()
    {
        var generation = ++_motionGeneration;
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        if (new UISettings().AnimationsEnabled)
        {
            Animate(visual, false);
            await Task.Delay(150);
        }
        if (generation != _motionGeneration) return;
        Window.AppWindow.Hide();
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Offset));
        visual.Opacity = 1;
        visual.Offset = Vector3.Zero;
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
        _motionGeneration++;
        Window.Close();
    }
}
