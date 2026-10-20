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
    private readonly bool _alwaysInputActive;
    private CardMicaTuning _tuning;
    private bool _dark;

    internal VividMicaBackdrop(CardMicaTuning? tuning = null, bool alwaysInputActive = false)
    {
        _alwaysInputActive = alwaysInputActive;
        _tuning = tuning is { } value
            ? value.Normalized()
            : CardMicaTuning.Default;
    }

    internal static MicaKind KindFor(bool dark) => dark ? MicaKind.BaseAlt : MicaKind.Base;
    internal static bool IsSupported
    {
        get
        {
            try { return MicaController.IsSupported(); }
        catch (Exception exception) when (exception is COMException or NotSupportedException or InvalidOperationException or ArgumentException)
            { return false; }
        }
    }
    internal CardMicaTuning Tuning => _tuning;
    internal bool ForcesInputActive => _alwaysInputActive;
    internal bool HasInputActiveConfiguration => _configuration?.IsInputActive == true;

    internal void UpdateTuning(CardMicaTuning tuning)
    {
        _tuning = tuning.Normalized();
        var controller = _controller;
        if (controller is null) return;
        try { ApplyTuning(controller); }
        catch (Exception exception) when (exception is COMException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            CardLifecycleLog.Write($"mica-tuning-failed hr=0x{exception.HResult:X8}");
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!IsSupported) return;

        var controller = (MicaController?)null;
        try
        {
            controller = new MicaController();
            _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
            Configure(controller, _configuration, xamlRoot);
            if (!controller.AddSystemBackdropTarget(connectedTarget))
            {
                controller.Dispose();
                controller = null;
                _configuration = null;
                return;
            }
            controller.SetSystemBackdropConfiguration(_configuration);
            _controller = controller;
            controller = null;
        }
        catch (Exception exception) when (exception is COMException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            CardLifecycleLog.Write($"mica-connect-failed hr=0x{exception.HResult:X8}");
            controller?.Dispose();
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

    private void Configure(MicaController controller, SystemBackdropConfiguration configuration, XamlRoot xamlRoot)
    {
        _dark = configuration.Theme switch
        {
            SystemBackdropTheme.Dark => true,
            SystemBackdropTheme.Light => false,
            _ => xamlRoot.Content is FrameworkElement { ActualTheme: ElementTheme.Dark }
        };

        if (_alwaysInputActive) configuration.IsInputActive = true;
        controller.Kind = KindFor(_dark);
        // MicaTabless keeps the system material unobstructed. A neutral tint
        // avoids the old blue wash while the user-tuned opacity controls how
        // strongly the material sits over the wallpaper.
        controller.TintColor = _dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        ApplyTuning(controller);
        controller.FallbackColor = _dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
    }

    private void ApplyTuning(MicaController controller)
    {
        controller.TintOpacity = (float)_tuning.TintOpacityFor(_dark);
        controller.LuminosityOpacity = (float)_tuning.LuminosityOpacityFor(_dark);
    }
}
