using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace TaskBarWidgets.Media.Integration;

internal sealed record UiRequestResult(JsonElement Request, object? Value, Exception? Error);
internal sealed record UiRequestTiming(string Action, string Lane, double QueueMilliseconds, double OperationMilliseconds, string Outcome);

// Reading the pipe only admits work. A slow settings/history request cannot hold
// the ordered audio lane, and each lane retains backpressure through its reply.
internal sealed class UiRequestDispatcher : IAsyncDisposable
{
    private readonly Func<JsonElement, CancellationToken, Task<object?>> _execute;
    private readonly Action<UiRequestTiming>? _measure;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Work> _audio;
    private readonly Channel<Work> _other;
    private readonly Task[] _workers;
    private int _disposed;
    private sealed record Work(JsonElement Request, Func<UiRequestResult, CancellationToken, Task> Complete, long Queued);

    internal UiRequestDispatcher(Func<JsonElement, CancellationToken, Task<object?>> execute,
        Action<UiRequestTiming>? measure = null, int capacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _execute = execute; _measure = measure;
        _audio = CreateLane(capacity); _other = CreateLane(capacity);
        _workers = [Task.Run(() => RunLaneAsync(_audio, "audio")), Task.Run(() => RunLaneAsync(_other, "other"))];
    }
    private static Channel<Work> CreateLane(int capacity) => Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity) {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });
    internal bool TryPost(JsonElement request, Func<UiRequestResult, CancellationToken, Task> complete)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        var lane = IsAudio(Action(request)) ? _audio : _other;
        return lane.Writer.TryWrite(new Work(request.Clone(), complete, Stopwatch.GetTimestamp()));
    }
    private async Task RunLaneAsync(Channel<Work> lane, string name)
    {
        try
        {
            await foreach (var work in lane.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await ExecuteAsync(work, name).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            while (lane.Reader.TryRead(out var work))
                await CompleteAsync(work, new UiRequestResult(work.Request, null, new OperationCanceledException(_stop.Token))).ConfigureAwait(false);
        }
    }
    private async Task ExecuteAsync(Work work, string lane)
    {
        var started = Stopwatch.GetTimestamp();
        object? value = null; Exception? error = null;
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            var operation = _execute(work.Request, _stop.Token);
            try { value = await operation.WaitAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // The root also receives this token. If an existing API cannot
                // cancel immediately, still observe its eventual failure safely.
                _ = operation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
        catch (Exception exception) { error = exception; }
        var timing = new UiRequestTiming(SafeAction(Action(work.Request)), lane,
            Stopwatch.GetElapsedTime(work.Queued, started).TotalMilliseconds,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, error?.GetType().Name ?? "ok");
        _measure?.Invoke(timing);
        await CompleteAsync(work, new UiRequestResult(work.Request, value, error)).ConfigureAwait(false);
    }
    private async Task CompleteAsync(Work work, UiRequestResult result)
    {
        try { await work.Complete(result, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) {
            _measure?.Invoke(new UiRequestTiming(SafeAction(Action(work.Request)), "reply", 0, 0, exception.GetType().Name));
        }
    }
    private static string Action(JsonElement request) => request.ValueKind == JsonValueKind.Object &&
        request.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String ? action.GetString() ?? "unknown" : "unknown";
    private static bool IsAudio(string action) => action is "getAudio" or "selectDevice" or "setVolume" or "setMute";
    private static bool IsWeather(string action) => action == "weatherCommand";
    private static string SafeAction(string action) => IsAudio(action) || IsWeather(action) || action is "getState" or "getNativeSettings" or "applyNativeSettings" or "getTrafficHistory" or "getMediaSettings" or "applyMediaSettings" or "getFonts" or "settingsCommand" or "mediaCommand" or "openPanel" ? action : "other";
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _audio.Writer.TryComplete(); _other.Writer.TryComplete(); _stop.Cancel();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _stop.Dispose();
    }
}
