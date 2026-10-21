using Microsoft.UI.Composition.SystemBackdrops;

namespace TaskBarWidgets.UI.Cards;

internal sealed partial class VividMicaBackdrop
{
    // Exercise the controller's real policy transitions on an isolated offscreen
    // window. No OS transparency, theme or power settings are changed.
    internal async Task<bool> ProbePolicyRecoveryAsync()
    {
        var controller = _controller ?? throw new InvalidOperationException("Probe Mica is disconnected.");
        var configuration = _configuration!;
        var original = configuration.IsInputActive;
        var initiallyActive = controller.State == SystemBackdropState.Active;
        var appearance = _appearance;
        try
        {
            configuration.IsInputActive = false;
            controller.SetSystemBackdropConfiguration(configuration);
            await Task.Delay(250);
            if (initiallyActive && controller.State == SystemBackdropState.Active)
                throw new InvalidOperationException("Inactive Mica did not enter fallback.");
            if (_appearance != appearance)
                throw new InvalidOperationException("Fallback replaced custom Mica settings.");
        }
        finally
        {
            configuration.IsInputActive = original;
            controller.SetSystemBackdropConfiguration(configuration);
        }
        await Task.Delay(250);
        if (initiallyActive && controller.State != SystemBackdropState.Active)
            throw new InvalidOperationException("Mica did not recover from inactive policy.");
        if (_appearance != appearance)
            throw new InvalidOperationException("Recovery replaced custom Mica settings.");
        return initiallyActive;
    }
}
