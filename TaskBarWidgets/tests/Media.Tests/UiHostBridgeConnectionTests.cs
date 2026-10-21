using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

// Exercises the real bridge, pipe server and request dispatcher against a test
// client. The executor stands in for Media.App's dispatcher handler; the card
// visibility sink mirrors App.ApplyCardLyricsVisibility reading the bridge state.
public sealed class UiHostBridgeConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task QueuedSubscriptionFromRetiredConnectionCannotReopenCardOrOutrankReconnect()
    {
        await using var harness = new BridgeHarness();
        using var timeout = new CancellationTokenSource(Timeout);
        await using var oldClient = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);

        await oldClient.SendAsync(Request("block-1", "block"), timeout.Token);
        await harness.Started.WaitAsync(timeout.Token);
        // The old connection's visible=true subscription is queued behind the
        // blocked request, then the connection is closed before it can run.
        await oldClient.SendAsync(Request("old-subscribe", "subscribeCardLyrics", new { visible = true, session = 12 }), timeout.Token);
        await oldClient.DisposeAsync();

        // Connecting the replacement only succeeds after the server retired the old
        // connection, so this is the disconnect barrier without a sleep.
        await using var newClient = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        Assert.False(harness.Bridge.CardLyricsVisible);

        harness.Release();
        await harness.Finished.WaitAsync(timeout.Token);
        // A new request on the same ordered lane proves the stale item was handled.
        await newClient.SendAsync(Request("new-state", "getState"), timeout.Token);
        var stateReply = await newClient.ReadUiResultAsync("new-state", timeout.Token);
        Assert.True(stateReply.GetProperty("success").GetBoolean());
        Assert.False(harness.Bridge.CardLyricsVisible);
        Assert.DoesNotContain("subscribeCardLyrics", harness.Executions);

        // The replacement starts at session 1; the retired connection's session 12
        // must not be able to suppress it.
        await newClient.SendAsync(Request("new-subscribe", "subscribeCardLyrics", new { visible = true, session = 1 }), timeout.Token);
        var reply = await newClient.ReadUiResultAsync("new-subscribe", timeout.Token);
        Assert.True(reply.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Object, reply.GetProperty("payload").GetProperty("snapshot").ValueKind);
        Assert.True(harness.Bridge.CardLyricsVisible);
    }

    [Fact]
    public async Task ReplyProducedForRetiredConnectionIsDropped()
    {
        await using var harness = new BridgeHarness();
        using var timeout = new CancellationTokenSource(Timeout);
        await using var oldClient = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);

        await oldClient.SendAsync(Request("slow-1", "block"), timeout.Token);
        await harness.Started.WaitAsync(timeout.Token);
        await oldClient.DisposeAsync();

        await using var newClient = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        // The in-flight operation completes only after a replacement is connected.
        harness.Release();
        await harness.Finished.WaitAsync(timeout.Token);

        await newClient.SendAsync(Request("new-1", "getState"), timeout.Token);
        var reply = await newClient.ReadUiResultAsync("new-1", timeout.Token);
        Assert.True(reply.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("slow-1", newClient.ObservedResults);
        Assert.Equal(["new-1"], newClient.ObservedResults);
    }

    [Fact]
    public async Task LateCleanupFromRetiredConnectionCannotCloseReplacementSubscription()
    {
        await using var harness = new BridgeHarness();
        using var timeout = new CancellationTokenSource(Timeout);
        await using var oldClient = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        await oldClient.SendAsync(Request("old-subscribe", "subscribeCardLyrics", new { visible = true, session = 1 }), timeout.Token);
        var oldReply = await oldClient.ReadUiResultAsync("old-subscribe", timeout.Token);
        Assert.Equal(JsonValueKind.Object, oldReply.GetProperty("payload").GetProperty("snapshot").ValueKind);
        Assert.True(harness.Bridge.CardLyricsVisible);
        var oldGeneration = harness.Bridge.ConnectionGeneration;
        var signalsBeforeDisconnect = harness.ReleaseSignals;
        await oldClient.DisposeAsync();
        await harness.Released.WaitAsync(timeout.Token);
        Assert.True(harness.ReleaseSignals > signalsBeforeDisconnect);

        await using var newClient = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        await newClient.SendAsync(Request("new-subscribe", "subscribeCardLyrics", new { visible = true, session = 1 }), timeout.Token);
        var newReply = await newClient.ReadUiResultAsync("new-subscribe", timeout.Token);
        Assert.Equal(JsonValueKind.Object, newReply.GetProperty("payload").GetProperty("snapshot").ValueKind);
        Assert.True(harness.Bridge.CardLyricsVisible);
        var signals = harness.ReleaseSignals;

        // Both delayed callbacks belong to the retired connection / process epoch.
        harness.Bridge.EndConnection(oldGeneration);
        harness.Bridge.ReleaseCardLyricsForProcess(-1);
        Assert.True(harness.Bridge.CardLyricsVisible);
        Assert.Equal(signals, harness.ReleaseSignals);

        await newClient.SendAsync(Request("after", "getState"), timeout.Token);
        Assert.True((await newClient.ReadUiResultAsync("after", timeout.Token)).GetProperty("success").GetBoolean());
        Assert.True(harness.Bridge.CardLyricsVisible);
    }

    [Fact]
    public async Task SessionOrderingStillAppliesWithinOneConnection()
    {
        await using var harness = new BridgeHarness();
        using var timeout = new CancellationTokenSource(Timeout);
        await using var client = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);

        await SubscribeAsync(client, harness, "s2", visible: true, session: 2, timeout.Token);
        Assert.True(harness.Bridge.CardLyricsVisible);
        var stale = await SubscribeAsync(client, harness, "s1", visible: true, session: 1, timeout.Token);
        Assert.Equal(JsonValueKind.Null, stale.GetProperty("payload").GetProperty("snapshot").ValueKind);
        Assert.True(harness.Bridge.CardLyricsVisible);

        var hidden = await SubscribeAsync(client, harness, "s3", visible: false, session: 3, timeout.Token);
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("payload").GetProperty("snapshot").ValueKind);
        Assert.False(harness.Bridge.CardLyricsVisible);

        await SubscribeAsync(client, harness, "s4", visible: true, session: 4, timeout.Token);
        Assert.True(harness.Bridge.CardLyricsVisible);
    }

    [Fact]
    public async Task NoConnectionAndNoSubscriptionLeavesCardHidden()
    {
        await using var harness = new BridgeHarness();
        Assert.False(harness.Bridge.CardLyricsVisible);

        using var timeout = new CancellationTokenSource(Timeout);
        var client = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        await SubscribeAsync(client, harness, "s1", visible: true, session: 1, timeout.Token);
        Assert.True(harness.Bridge.CardLyricsVisible);
        var signalsBeforeDisconnect = harness.ReleaseSignals;
        await client.DisposeAsync();
        await harness.Released.WaitAsync(timeout.Token);
        Assert.True(harness.ReleaseSignals > signalsBeforeDisconnect);

        await using var next = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        Assert.False(harness.Bridge.CardLyricsVisible);
    }

    [Fact]
    public async Task RapidTogglingAndReconnectPreserveVisibility()
    {
        await using var harness = new BridgeHarness();
        using var timeout = new CancellationTokenSource(Timeout);
        var client = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        for (var session = 1L; session <= 6; ++session)
        {
            var visible = session % 2 == 1;
            await SubscribeAsync(client, harness, $"toggle-{session}", visible, session, timeout.Token);
            Assert.Equal(visible, harness.Bridge.CardLyricsVisible);
        }
        await client.DisposeAsync();

        await using var reconnected = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        Assert.False(harness.Bridge.CardLyricsVisible);
        await SubscribeAsync(reconnected, harness, "reconnect-1", visible: true, session: 1, timeout.Token);
        Assert.True(harness.Bridge.CardLyricsVisible);
        await SubscribeAsync(reconnected, harness, "reconnect-2", visible: false, session: 2, timeout.Token);
        Assert.False(harness.Bridge.CardLyricsVisible);
    }

    [Fact]
    public async Task DisposeWithBlockedAndQueuedRequestsCompletesWithoutDeadlock()
    {
        await using var harness = new BridgeHarness();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await TestClient.ConnectAsync(harness.PipeName, timeout.Token);
        await client.SendAsync(Request("block-1", "block"), timeout.Token);
        await harness.Started.WaitAsync(timeout.Token);
        for (var index = 0; index < 24; ++index)
            await client.SendAsync(Request($"queued-{index}", "getState"), timeout.Token);

        await harness.Bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        harness.Release();
    }

    [Fact]
    public async Task DisposeWithoutClientCompletes()
    {
        var harness = new BridgeHarness();
        await harness.Bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await harness.DisposeAsync();
    }

    private static async Task<JsonElement> SubscribeAsync(TestClient client, BridgeHarness harness, string requestId,
        bool visible, long session, CancellationToken token)
    {
        await client.SendAsync(Request(requestId, "subscribeCardLyrics", new { visible, session }), token);
        var reply = await client.ReadUiResultAsync(requestId, token);
        Assert.True(reply.GetProperty("success").GetBoolean());
        Assert.Equal(visible, harness.Bridge.CardLyricsVisible);
        return reply;
    }

    private static object Request(string requestId, string action, object? payload = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["type"] = "command",
            ["action"] = action,
            ["requestId"] = requestId
        };
        if (payload is not null) message["payload"] = payload;
        return message;
    }

    private sealed class BridgeHarness : IAsyncDisposable
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _releaseSignals;
        public string PipeName { get; } = "TaskBarWidgets.Tests.UI." + Guid.NewGuid().ToString("N");
        public UiHostBridge Bridge { get; }
        public Task Started => _started.Task;
        public Task Finished => _finished.Task;
        public ConcurrentQueue<string> Executions { get; } = new();
        public int ReleaseSignals => Volatile.Read(ref _releaseSignals);
        public Task Released => _released.Task;

        public BridgeHarness()
        {
            UiHostBridge? bridge = null;
            bridge = new UiHostBridge(async (request, generation, token) =>
            {
                var action = request.GetProperty("action").GetString() ?? "";
                Executions.Enqueue(action);
                switch (action)
                {
                    case "block":
                        _started.TrySetResult();
                        try { await _release.Task.WaitAsync(token); }
                        finally { _finished.TrySetResult(); }
                        return new { released = true };
                    case "subscribeCardLyrics":
                        var payload = request.GetProperty("payload");
                        var visible = payload.GetProperty("visible").GetBoolean();
                        var session = payload.GetProperty("session").GetInt64();
                        if (!bridge!.TrySubscribeCardLyrics(visible, session, generation))
                            return new { snapshot = (object?)null };
                        return new { snapshot = visible ? (object?)new { available = true } : null };
                    default:
                        return new { ok = true };
                }
            }, startUiProcess: false, pipeName: PipeName);
            Bridge = bridge;
            Bridge.CardLyricsReleased += () =>
            {
                Interlocked.Increment(ref _releaseSignals);
                _released.TrySetResult();
            };
        }

        public void Release() => _release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Release();
            await Bridge.DisposeAsync();
        }
    }

    private sealed class TestClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        public ConcurrentQueue<string> ObservedResults { get; } = new();

        private TestClient(NamedPipeClientStream pipe, StreamReader reader) { _pipe = pipe; _reader = reader; }

        public static async Task<TestClient> ConnectAsync(string name, CancellationToken token)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(token);
                var reader = new StreamReader(pipe, leaveOpen: true);
                // The bridge's handshake is two uiReady commands: one initialize
                // frame and one filtered display snapshot, not the raw media protocol.
                var initialize = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException("Missing initialize frame.");
                using (var document = JsonDocument.Parse(initialize))
                {
                    Assert.Equal("command", document.RootElement.GetProperty("type").GetString());
                    Assert.Equal("uiReady", document.RootElement.GetProperty("action").GetString());
                }
                var snapshot = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException("Missing snapshot frame.");
                using (var document = JsonDocument.Parse(snapshot))
                {
                    Assert.Equal("command", document.RootElement.GetProperty("type").GetString());
                    Assert.Equal("uiReady", document.RootElement.GetProperty("action").GetString());
                }
                return new TestClient(pipe, reader);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        public async Task SendAsync(object message, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await _pipe.WriteAsync(bytes, token);
            await _pipe.WriteAsync(new byte[] { 10 }, token);
            await _pipe.FlushAsync(token);
        }

        public async Task<JsonElement> ReadUiResultAsync(string requestId, CancellationToken token)
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(token);
                if (line is null) throw new EndOfStreamException("Media pipe closed before the reply arrived.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("action", out var action) && action.GetString() == "uiResult" &&
                    root.TryGetProperty("requestId", out var id) && id.GetString() is { } value)
                {
                    ObservedResults.Enqueue(value);
                    if (value == requestId) return root;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _pipe.DisposeAsync();
        }
    }
}
