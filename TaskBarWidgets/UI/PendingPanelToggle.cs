using System.Text.Json;

namespace TaskBarWidgets.UI;

// One outstanding UI callback, latest click payload. Once consumed, a new click
// is a new toggle, preserving ordinary click-to-open / click-to-close behavior.
internal sealed class PendingPanelToggle
{
    private readonly object _gate = new();
    private (JsonElement Message, DateTimeOffset ReceivedAt)? _pending;

    internal bool Post(JsonElement message, DateTimeOffset receivedAt)
    {
        lock (_gate)
        {
            var schedule = !_pending.HasValue;
            _pending = (message, _pending?.ReceivedAt ?? receivedAt);
            return schedule;
        }
    }

    internal (JsonElement Message, DateTimeOffset ReceivedAt)? Take()
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }
}
