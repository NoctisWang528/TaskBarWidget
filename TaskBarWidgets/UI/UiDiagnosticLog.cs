using System.Threading.Channels;

namespace TaskBarWidgets.UI;

// Best-effort diagnostics must not turn a slow disk into a frozen card. A single
// bounded writer owns rotation; timestamps describe the event, not the disk write.
internal static class UiDiagnosticLog
{
    private static readonly Channel<(string File, string Line)> Pending = Channel.CreateBounded<(string, string)>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    static UiDiagnosticLog() => _ = Task.Run(async () =>
    {
        await foreach (var entry in Pending.Reader.ReadAllAsync())
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, entry.File);
                if (File.Exists(path) && new FileInfo(path).Length >= 128 * 1024) File.Move(path, path + ".1", true);
                await File.AppendAllTextAsync(path, entry.Line).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    });

    internal static void Write(string file, string message) => Pending.Writer.TryWrite((file,
        $"{DateTimeOffset.Now:o} pid={Environment.ProcessId} {message[..Math.Min(message.Length, 512)]}\n"));
}

// Footprint counters share the bounded writer above: no extra thread, and a
// slow disk still cannot stall the UI thread.
internal static class CardMetricsLog
{
    internal static void Write(string message) => UiDiagnosticLog.Write("ui-metrics.log", message);
}
