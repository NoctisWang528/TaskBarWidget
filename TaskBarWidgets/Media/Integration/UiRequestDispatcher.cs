using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace TaskBarWidgets.Media.Integration;

internal sealed record UiRequestResult(JsonElement Request, long ConnectionGeneration, object? Value, Exception? Error);
internal sealed record UiRequestTiming(string Action, string Lane, double QueueMilliseconds, double OperationMilliseconds, string Outcome);

// Reading the pipe only admits work. A slow settings/history request cannot hold
// the ordered audio lane, and each lane retains backpressure through its reply.
internal sealed class UiRequestDispatcher : IAsyncDisposable
{
    private readonly Func<JsonElement, long, CancellationToken, Task<object?>> _execute;
    private readonly Action<UiRequestTiming>? _measure;
    private readonly Func<long, bool>? _isCurrent;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Work> _audio;
    private readonly Channel<Work> _other;
    private readonly Channel<Work> _control;
    private readonly Task[] _workers;
    private int _disposed;
    private sealed record Work(JsonElement Request, long Generation, Func<UiRequestResult, CancellationToken, Task> Complete, long Queued);

    internal UiRequestDispatcher(Func<JsonElement, long, CancellationToken, Task<object?>> execute,
        Action<UiRequestTiming>? measure = null, int capacity = 16, Func<long, bool>? isCurrent = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _execute = execute; _measure = measure; _isCurrent = isCurrent;
        _audio = CreateLane(capacity); _other = CreateLane(capacity); _control = CreateLane(capacity);
        _workers = [Task.Run(() => RunLaneAsync(_audio, "audio")), Task.Run(() => RunLaneAsync(_other, "other")),
            Task.Run(() => RunLaneAsync(_control, "control"))];
    }
    private static Channel<Work> CreateLane(int capacity) => Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity) {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });
    // The generation is captured here, on the pipe reader, so every queued item
    // remembers which connection admitted it.
    internal bool TryPost(JsonElement request, long generation, Func<UiRequestResult, CancellationToken, Task> complete)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        var action = Action(request);
        var lane = IsAudio(action) ? _audio : IsControl(action) ? _control : _other;
        return lane.Writer.TryWrite(new Work(request.Clone(), generation, complete, Stopwatch.GetTimestamp()));
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
                await CompleteAsync(work, new UiRequestResult(work.Request, work.Generation, null, new OperationCanceledException(_stop.Token))).ConfigureAwait(false);
        }
    }
    private async Task ExecuteAsync(Work work, string lane)
    {
        var started = Stopwatch.GetTimestamp();
        object? value = null; Exception? error = null;
        var outcome = "ok";
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            // A request that outlived its connection must not mutate state or answer
            // the replacement client. The check uses the generation captured at
            // admission; it never re-tags the request with the current connection.
            if (_isCurrent is not null && !_isCurrent(work.Generation))
            {
                error = new OperationCanceledException("界面连接已失效。");
                outcome = "stale";
            }
            else
            {
                var operation = _execute(work.Request, work.Generation, _stop.Token);
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
        }
        catch (Exception exception) { error = exception; outcome = exception.GetType().Name; }
        var timing = new UiRequestTiming(SafeAction(Action(work.Request)), lane,
            Stopwatch.GetElapsedTime(work.Queued, started).TotalMilliseconds,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome);
        _measure?.Invoke(timing);
        await CompleteAsync(work, new UiRequestResult(work.Request, work.Generation, value, error)).ConfigureAwait(false);
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
    private static bool IsControl(string action) => action is "cardSnapshot" or "subscribeCardLyrics" or "mediaCommand" or
        "getUpdateState" or "reportUiReady" or "openSettings" or "hideCards" or "previewCardMica" or "previewCardTheme";
    private static bool IsWeather(string action) => action == "weatherCommand";
    private static string SafeAction(string action) => IsAudio(action) || IsWeather(action) || action is "getState" or "getNativeSettings" or "applyNativeSettings" or "getTrafficHistory" or "getMediaSettings" or "applyMediaSettings" or "getFonts" or "settingsCommand" or "mediaCommand" or "openPanel" or "getDataStatus" or "storageCommand" ? action : "other";
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _audio.Writer.TryComplete(); _other.Writer.TryComplete(); _control.Writer.TryComplete(); _stop.Cancel();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _stop.Dispose();
    }
}
