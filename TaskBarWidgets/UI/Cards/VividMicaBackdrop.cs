using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TaskBarWidgets.UI.Cards;

// One controller per window for both system defaults and custom Mica. Changing
// settings never swaps a visible window's SystemBackdrop or approximates defaults.
internal sealed partial class VividMicaBackdrop : SystemBackdrop
{
    private MicaController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private DispatcherQueue? _dispatcher;
    private readonly bool _alwaysInputActive;
    private CardMicaAppearance _appearance;
    private bool _dark;
    private bool _applying;
    private SystemBackdropState? _lastState;
    private bool _connectionFailed;

    internal VividMicaBackdrop(CardMicaTuning? tuning = null, bool alwaysInputActive = false)
    {
        _alwaysInputActive = alwaysInputActive;
        _appearance = new(true, (tuning ?? CardMicaTuning.Default).Normalized());
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
    internal CardMicaTuning Tuning => _appearance.Tuning;
    internal bool Custom => _appearance.Vivid;
    internal bool NeedsSolidFallback => _connectionFailed || !IsSupported;
    internal bool ForcesInputActive => _alwaysInputActive;
    internal bool HasInputActiveConfiguration => _configuration?.IsInputActive == true;
    internal event Action? StatusChanged;
    internal MicaDiagnostics Diagnostics => _controller is { } controller
        ? new(true, Custom, controller.State.ToString(), controller.Kind.ToString(),
            controller.TintOpacity, controller.LuminosityOpacity, HasInputActiveConfiguration, _dark)
        : new(false, Custom, "Unavailable", null, null, null, false, _dark);

    internal void UpdateTuning(CardMicaTuning tuning) => UpdateAppearance(_appearance with { Tuning = tuning });

    internal void UpdateAppearance(CardMicaAppearance appearance)
    {
        appearance = appearance with { Tuning = appearance.Tuning.Normalized() };
        if (_appearance.Vivid == appearance.Vivid && _appearance.Tuning.Matches(appearance.Tuning)) return;
        _appearance = appearance;
        var controller = _controller;
        if (controller is null) return;
        try { ApplyAppearance(controller); }
        catch (Exception exception) when (exception is COMException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            CardLifecycleLog.Write($"mica-tuning-failed hr=0x{exception.HResult:X8}");
        }
        StatusChanged?.Invoke();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _connectionFailed = false;
        if (!IsSupported) { _connectionFailed = true; StatusChanged?.Invoke(); return; }
        MicaController? controller = null;
        try
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            controller = new MicaController();
            _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
            if (!controller.AddSystemBackdropTarget(connectedTarget))
            {
                controller.Dispose();
                _configuration = null;
                _connectionFailed = true;
                StatusChanged?.Invoke();
                return;
            }
            Configure(controller, _configuration, xamlRoot);
            _controller = controller;
            _lastState = controller.State;
            controller.StateChanged += OnControllerStateChanged;
            controller = null;
            StatusChanged?.Invoke();
        }
        catch (Exception exception) when (exception is COMException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            CardLifecycleLog.Write($"mica-connect-failed hr=0x{exception.HResult:X8}");
            controller?.Dispose();
            ReleaseController();
            _connectionFailed = true;
            StatusChanged?.Invoke();
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        ReleaseController();
        base.OnTargetDisconnected(disconnectedTarget);
        StatusChanged?.Invoke();
    }

    private void ReleaseController()
    {
        var controller = _controller;
        _controller = null;
        _configuration = null;
        _dispatcher = null;
        _lastState = null;
        if (controller is null) return;
        controller.StateChanged -= OnControllerStateChanged;
        try { controller.RemoveAllSystemBackdropTargets(); }
        catch (COMException) { }
        controller.Dispose();
    }

    private void OnControllerStateChanged(ISystemBackdropControllerWithTargets sender, object args)
    {
        if (_controller is not { } controller || !sender.Equals(controller)) return;
        // Policy notifications can arrive off-thread or after disconnection.
        _dispatcher?.TryEnqueue(() =>
        {
            if (!ReferenceEquals(controller, _controller)) return;
            try
            {
                var state = controller.State;
                var recovered = _lastState != SystemBackdropState.Active && state == SystemBackdropState.Active;
                _lastState = state;
                if (recovered && !_applying) ApplyAppearance(controller);
            }
            catch (Exception exception) when (exception is COMException or ObjectDisposedException or InvalidOperationException or ArgumentException)
            {
                CardLifecycleLog.Write($"mica-policy-update-failed hr=0x{exception.HResult:X8}");
                return;
            }
            StatusChanged?.Invoke();
        });
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        if (_controller is not { } controller || _configuration is not { } configuration) return;
        try
        {
            // The connected configuration is maintained by XAML; the callback's
            // transient target cannot safely be queried on all WinUI versions.
            Configure(controller, configuration, xamlRoot);
            StatusChanged?.Invoke();
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
        controller.SetSystemBackdropConfiguration(configuration);
        ApplyAppearance(controller);
    }

    private void ApplyAppearance(MicaController controller)
    {
        if (_applying) return;
        _applying = true;
        try
        {
            if (!Custom)
            {
                controller.Kind = MicaKind.Base;
                controller.ResetProperties();
                return;
            }
            controller.Kind = KindFor(_dark);
            controller.TintColor = _dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
            controller.TintOpacity = (float)Tuning.TintOpacityFor(_dark);
            controller.LuminosityOpacity = (float)Tuning.LuminosityOpacityFor(_dark);
            controller.FallbackColor = _dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        }
        finally { _applying = false; }
    }
}

internal readonly record struct MicaDiagnostics(bool Connected, bool Custom, string State,
    string? Kind, float? TintOpacity, float? LuminosityOpacity, bool InputActive, bool Dark);
