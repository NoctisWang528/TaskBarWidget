// Tracks the short presentation hold when a selected browser session disappears.
// The media coordinator owns the timer; this state never changes backend source selection.
using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Services;

internal sealed class BrowserMissingPresentationState
{
    internal static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(500);

    private int _generation;

    internal bool IsHidden { get; private set; }

    internal int Begin() => ++_generation;

    internal void CancelPending() => _generation++;

    internal bool TryHide(int generation, bool selectedBrowserStillMissing)
    {
        if (generation != _generation || !selectedBrowserStillMissing)
            return false;

        IsHidden = true;
        return true;
    }

    internal void Complete()
    {
        _generation++;
        IsHidden = false;
    }

    internal MediaSnapshot Project(MediaSnapshot snapshot) => IsHidden
        ? MediaSnapshot.Disconnected
        : snapshot;
}
