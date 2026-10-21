using System.Collections.Concurrent;
using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class UiRequestDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BlockedTrafficHistoryDoesNotDelayAudio()
    {
        var historyStarted = Signal(); var releaseHistory = Signal();
        await using var dispatcher = new UiRequestDispatcher(async (request, _, token) => {
            if (request.GetProperty("action").GetString() == "getTrafficHistory") {
                historyStarted.TrySetResult(); await releaseHistory.Task.WaitAsync(token); return "history";
            }
            return "audio";
        });
        var history = Post(dispatcher, "getTrafficHistory");
        await historyStarted.Task.WaitAsync(Timeout);
        var audio = await Post(dispatcher, "getAudio").WaitAsync(Timeout);
        Assert.Null(audio.Error); Assert.Equal("audio", audio.Value);
        Assert.False(history.IsCompleted);
        releaseHistory.TrySetResult();
        Assert.Equal("history", (await history.WaitAsync(Timeout)).Value);
    }

    [Fact]
    public async Task AudioMutationsAndReadbackStayInArrivalOrder()
    {
        var started = Signal(); var release = Signal();
        var events = new ConcurrentQueue<string>();
        var volume = 10d; var muted = false;
        await using var dispatcher = new UiRequestDispatcher(async (request, _, token) => {
            var action = request.GetProperty("action").GetString();
            if (action == "setVolume") {
                events.Enqueue("volume:start"); started.TrySetResult(); await release.Task.WaitAsync(token);
                volume = 42; events.Enqueue("volume:complete"); return null;
            }
            if (action == "setMute") { muted = true; events.Enqueue("mute"); return null; }
            if (action == "getAudio") { events.Enqueue("readback"); return (volume, muted); }
            return null;
        });
        var setVolume = Post(dispatcher, "setVolume"); await started.Task.WaitAsync(Timeout);
        var setMute = Post(dispatcher, "setMute"); var readback = Post(dispatcher, "getAudio");
        await Post(dispatcher, "getState").WaitAsync(Timeout); // The other lane continues while audio waits.
        Assert.False(setMute.IsCompleted); Assert.False(readback.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(setVolume, setMute, readback).WaitAsync(Timeout);
        Assert.Equal(new[] { "volume:start", "volume:complete", "mute", "readback" }, events);
        Assert.Equal((42d, true), Assert.IsType<(double, bool)>((await readback).Value));
    }

    [Fact]
    public async Task FullQueueRejectsAndShutdownCancelsQueuedWorkWithoutExecutingIt()
    {
        var started = Signal(); var release = Signal();
        var executed = new ConcurrentQueue<string>();
        await using var dispatcher = new UiRequestDispatcher(async (request, _, _) => {
            executed.Enqueue(request.GetProperty("action").GetString()!);
            started.TrySetResult(); await release.Task; return null; // Deliberately non-cooperative legacy operation.
        }, capacity: 1);
        var running = Post(dispatcher, "getTrafficHistory"); await started.Task.WaitAsync(Timeout);
        var queued = Post(dispatcher, "getState");
        Assert.False(dispatcher.TryPost(Request("settingsCommand"), 1, (_, _) => throw new InvalidOperationException("Rejected work cannot run.")));
        await dispatcher.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.IsAssignableFrom<OperationCanceledException>((await running.WaitAsync(Timeout)).Error);
        Assert.IsAssignableFrom<OperationCanceledException>((await queued.WaitAsync(Timeout)).Error);
        Assert.Equal(new[] { "getTrafficHistory" }, executed);
        Assert.False(dispatcher.TryPost(Request("getAudio"), 1, (_, _) => Task.CompletedTask));
        release.TrySetResult();
    }

    [Fact]
    public async Task QueuedWorkFromRetiredGenerationIsNotExecutedButStillCompletes()
    {
        var started = Signal(); var release = Signal();
        var executed = new ConcurrentQueue<string>();
        var current = 1L;
        await using var dispatcher = new UiRequestDispatcher(async (request, generation, _) => {
            executed.Enqueue(request.GetProperty("action").GetString()!);
            started.TrySetResult();
            await release.Task;
            return generation;
        }, isCurrent: generation => generation == Volatile.Read(ref current));
        var running = Post(dispatcher, "getTrafficHistory", 1);
        await started.Task.WaitAsync(Timeout);
        var queued = Post(dispatcher, "getState", 1);
        Volatile.Write(ref current, 2);
        release.TrySetResult();
        var result = await queued.WaitAsync(Timeout);
        Assert.Equal(1, result.ConnectionGeneration);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);
        Assert.Equal(new[] { "getTrafficHistory" }, executed);
        Assert.Equal(1L, Assert.IsType<long>((await running.WaitAsync(Timeout)).Value));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonElement Request(string action) => JsonSerializer.SerializeToElement(new { action });
    private static Task<UiRequestResult> Post(UiRequestDispatcher dispatcher, string action, long generation = 1)
    {
        var result = new TaskCompletionSource<UiRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(dispatcher.TryPost(Request(action), generation, (value, _) => { result.TrySetResult(value); return Task.CompletedTask; }));
        return result.Task;
    }
}
