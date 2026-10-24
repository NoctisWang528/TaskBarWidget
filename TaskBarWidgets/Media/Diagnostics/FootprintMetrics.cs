using System.Diagnostics;
using System.Globalization;
using TaskbarLyrics.Core.Utilities;

namespace TaskBarWidgets.Media.Diagnostics;

// Minimal built-in counters for the media helper. Every counter is cumulative
// for the process lifetime, like the native and UI footprint counters: Snapshot
// never resets anything, so an external script derives window deltas and slopes
// from two adjacent lines. Nothing here caches media objects, frames or payload.
internal static class FootprintMetrics
{
    internal const string Category = "METRICS";
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);
    private static readonly double TicksPerMillisecond = Stopwatch.Frequency / 1000.0;

    private static long _fft;
    private static long _frames;
    private static long _pipeWrites;
    private static long _pipeBytes;
    private static long _getStateCount;
    private static long _getStateTotalTicks;
    private static long _getStateMaxTicks;
    private static long _textBytes;
    private static long _coverBytes;
    private static long _iconBytes;

    // Fully qualified: WinForms is referenced by this project, so an unqualified
    // Timer would also match System.Windows.Forms.Timer.
    private static System.Threading.Timer? _timer;
    private static long _lastReportTick = Stopwatch.GetTimestamp();
    private static int _started;
    private static int _reporting;

    internal static void NoteFft() => Interlocked.Increment(ref _fft);
    internal static void NoteFrame() => Interlocked.Increment(ref _frames);

    internal static void NotePipeWrite(long bytes)
    {
        Interlocked.Increment(ref _pipeWrites);
        if (bytes > 0) Interlocked.Add(ref _pipeBytes, bytes);
    }

    internal static void NoteStateTiming(double operationMs)
    {
        if (!double.IsFinite(operationMs) || operationMs < 0) return;
        var ticks = (long)(operationMs * TicksPerMillisecond);
        Interlocked.Increment(ref _getStateCount);
        Interlocked.Add(ref _getStateTotalTicks, ticks);
        RaiseMax(ref _getStateMaxTicks, ticks);
    }

    internal static void NoteStatePayload(long textBytes, long coverBytes, long iconBytes)
    {
        if (textBytes > 0) Interlocked.Add(ref _textBytes, textBytes);
        if (coverBytes > 0) Interlocked.Add(ref _coverBytes, coverBytes);
        if (iconBytes > 0) Interlocked.Add(ref _iconBytes, iconBytes);
    }

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            Volatile.Write(ref _lastReportTick, Stopwatch.GetTimestamp());
            Volatile.Write(ref _timer, new System.Threading.Timer(Report, null, ReportInterval, ReportInterval));
        }
        catch
        {
            // A metrics timer must never prevent the media helper from starting.
            Interlocked.Exchange(ref _started, 0);
        }
    }

    internal static void Stop()
    {
        Interlocked.Exchange(ref _started, 0);
        var timer = Interlocked.Exchange(ref _timer, null);
        timer?.Dispose();
    }

    private static void Report(object? state)
    {
        if (Volatile.Read(ref _started) == 0) return;
        if (Interlocked.CompareExchange(ref _reporting, 1, 0) != 0) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - Interlocked.Exchange(ref _lastReportTick, now)) / (double)Stopwatch.Frequency;
            Log.Diagnostic(Category, Snapshot(elapsed));
        }
        catch
        {
            // Diagnostics must never affect the media helper.
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }

    // Returns one space separated metrics line (invariant culture). Every value
    // except seconds is cumulative since process start; seconds is the real time
    // elapsed since the previous report.
    internal static string Snapshot(double seconds)
    {
        var allocBytes = GC.GetTotalAllocatedBytes(precise: false);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var fft = Interlocked.Read(ref _fft);
        var frames = Interlocked.Read(ref _frames);
        var pipeWrites = Interlocked.Read(ref _pipeWrites);
        var pipeBytes = Interlocked.Read(ref _pipeBytes);
        var getStateCount = Interlocked.Read(ref _getStateCount);
        var getStateTotalMs = Interlocked.Read(ref _getStateTotalTicks) / TicksPerMillisecond;
        var getStateMaxMs = Interlocked.Read(ref _getStateMaxTicks) / TicksPerMillisecond;
        var textBytes = Interlocked.Read(ref _textBytes);
        var coverBytes = Interlocked.Read(ref _coverBytes);
        var iconBytes = Interlocked.Read(ref _iconBytes);
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"seconds={seconds:F1} allocBytes={allocBytes} gen0={gen0} gen1={gen1} gen2={gen2} fft={fft} frames={frames} pipeWrites={pipeWrites} pipeBytes={pipeBytes} getStateCount={getStateCount} getStateTotalMs={getStateTotalMs:F1} getStateMaxMs={getStateMaxMs:F1} getStateTextBytes={textBytes} getStateCoverBytes={coverBytes} getStateIconBytes={iconBytes}");
    }

    private static void RaiseMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current) return;
            current = previous;
        }
    }
}
