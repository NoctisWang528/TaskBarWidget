using System.Globalization;

namespace TaskBarWidgets.UI;

// Built-in UI footprint counters: recording only, never changes behavior. The
// snapshot is cumulative on purpose so a script reads an observation window as
// a delta between two lines instead of depending on a reset.
internal static class FootprintMetrics
{
    private static readonly object Gate = new();
    private static long _opens, _firstFrames, _timeouts;
    private static double _openTotalMs, _openMaxMs, _firstFrameTotalMs, _firstFrameMaxMs;

    internal static void RecordOpen(double openMs)
    {
        lock (Gate)
        {
            _opens++;
            _openTotalMs += openMs;
            if (openMs > _openMaxMs) _openMaxMs = openMs;
        }
    }

    internal static void RecordFirstFrame(double firstFrameMs)
    {
        lock (Gate)
        {
            _firstFrames++;
            _firstFrameTotalMs += firstFrameMs;
            if (firstFrameMs > _firstFrameMaxMs) _firstFrameMaxMs = firstFrameMs;
        }
    }

    internal static void RecordFirstFrameTimeout()
    {
        lock (Gate) _timeouts++;
    }

    // One ASCII line with a stable key order and invariant one-decimal numbers
    // so the external parser never depends on the current culture.
    internal static string Snapshot(int imageCacheEntries, bool coverLoaded, int sourceIconEntries)
    {
        lock (Gate)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "METRICS opens={0} firstFrames={1} firstFrameAvgMs={2:0.0} firstFrameMaxMs={3:0.0} openAvgMs={4:0.0} openMaxMs={5:0.0} " +
                "timeouts={6} imageCache={7} coverLoaded={8} sourceIcons={9}",
                _opens, _firstFrames, Average(_firstFrameTotalMs, _firstFrames), _firstFrameMaxMs,
                Average(_openTotalMs, _opens), _openMaxMs, _timeouts, imageCacheEntries, coverLoaded ? 1 : 0, sourceIconEntries);
        }
    }

    private static double Average(double total, long count) => count == 0 ? 0 : total / count;
}
