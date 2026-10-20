using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TaskBarWidgets.UI.Cards;

// MicaBackdrop does not expose tint or opacity. A small controller wrapper keeps
// the platform material while making the wallpaper color more visible.
internal sealed class VividMicaBackdrop : SystemBackdrop
{
    private MicaController? _controller;
    private SystemBackdropConfiguration? _configuration;

    internal static MicaKind PreferredKind => MicaKind.BaseAlt;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!MicaController.IsSupported()) return;

        try
        {
            var controller = new MicaController();
            _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
            Configure(controller, _configuration, xamlRoot);
            if (!controller.AddSystemBackdropTarget(connectedTarget))
            {
                controller.Dispose();
                return;
            }
            controller.SetSystemBackdropConfiguration(_configuration);
            _controller = controller;
        }
        catch (Exception exception) when (exception is COMException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            CardLifecycleLog.Write($"mica-connect-failed hr=0x{exception.HResult:X8}");
            _controller?.Dispose();
            _controller = null;
            _configuration = null;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        var controller = _controller;
        _controller = null;
        _configuration = null;
        if (controller is not null)
        {
            try { controller.RemoveSystemBackdropTarget(disconnectedTarget); }
            catch (COMException) { }
            controller.Dispose();
        }
        base.OnTargetDisconnected(disconnectedTarget);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        var controller = _controller;
        var configuration = _configuration;
        if (controller is null || configuration is null) return;
        try
        {
            // GetDefaultSystemBackdropConfiguration rejects this callback's
            // transient target on some WinUI versions. The connected
            // configuration is already maintained with current theme state.
            Configure(controller, configuration, xamlRoot);
            controller.SetSystemBackdropConfiguration(configuration);
        }
        catch (Exception exception) when (exception is COMException or ObjectDisposedException or ArgumentException or InvalidOperationException)
        {
            CardLifecycleLog.Write($"mica-theme-update-failed hr=0x{exception.HResult:X8}");
        }
    }

    private static void Configure(MicaController controller, SystemBackdropConfiguration configuration, XamlRoot xamlRoot)
    {
        var dark = configuration.Theme switch
        {
            SystemBackdropTheme.Dark => true,
            SystemBackdropTheme.Light => false,
            _ => xamlRoot.Content is FrameworkElement { ActualTheme: ElementTheme.Dark }
        };

        controller.Kind = PreferredKind;
        controller.TintColor = dark ? Color.FromArgb(255, 12, 20, 34) : Color.FromArgb(255, 232, 241, 255);
        controller.TintOpacity = dark ? 0.20f : 0.22f;
        controller.LuminosityOpacity = dark ? 0.56f : 0.58f;
        controller.FallbackColor = dark ? Color.FromArgb(255, 22, 25, 31) : Color.FromArgb(255, 243, 245, 248);
    }
}
