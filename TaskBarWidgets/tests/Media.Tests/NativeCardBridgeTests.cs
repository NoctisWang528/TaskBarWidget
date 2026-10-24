using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class NativeCardBridgeTests
{
    [Fact]
    public async Task BlockedHistoryDoesNotHoldAudioOrCorruptConcurrentSnapshotAndErrorReplies()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var historyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var name = "TaskBarWidgets.Cards.test." + Guid.NewGuid().ToString("N");
        await using var bridge = new NativeCardBridge(name, _ => Task.FromResult<object>(new { connected = true }),
            async (request, _, token) =>
            {
                var action = request.GetProperty("action").GetString();
                if (action == "getTrafficHistory")
                {
                    historyStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                if (action == "getAudio") return new { revision = 1, outputs = Array.Empty<object>() };
                throw new ArgumentException("expected rejection");
            });
        bridge.Start();
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        async Task Send(string id, string action)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "command", action = "cardRequest", requestId = id,
                payload = new { action, payload = new { } } });
            await pipe.WriteAsync(bytes, timeout.Token);
            await pipe.WriteAsync(new byte[] { 10 }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }
        await Send("traffic1", "getTrafficHistory");
        await historyStarted.Task.WaitAsync(timeout.Token);
        await Send("audio1", "getAudio");
        await Send("misc1", "openSettings");
        for (var index = 0; index < 100; index++) bridge.PublishSnapshot();
        var received = new HashSet<string>();
        while (received.Count < 2)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            using var json = JsonDocument.Parse(line!);
            var root = json.RootElement;
            if (root.GetProperty("action").GetString() != "cardReply") continue;
            var id = root.GetProperty("requestId").GetString()!;
            received.Add(id);
            Assert.Equal(id == "audio1", root.GetProperty("success").GetBoolean());
            if (id == "misc1") Assert.Equal("expected rejection", root.GetProperty("error").GetString());
        }
        // Disposing the bridge cancels the blocked history instead of waiting for it.
        await bridge.DisposeAsync().AsTask().WaitAsync(timeout.Token);
    }

    [Fact]
    public void ArtworkFollowsSentMediaRevisionAndIsCompleteAfterReconnectOrSourceReappearance()
    {
        var artwork = new ArtworkDeltaState();
        static object Frame(long revision, string cover, bool source = true) => new Dictionary<string, object?>
        {
            ["action"] = "cardSnapshot", ["payload"] = new Dictionary<string, object?>
            {
                ["media"] = new Dictionary<string, object?>
                {
                    ["revision"] = revision, ["coverDataUrl"] = cover,
                    ["sources"] = source ? new[] { new Dictionary<string, object?> { ["id"] = "qq", ["iconDataUrl"] = "icon" } } : [],
                },
            },
        };
        JsonElement Filter(long revision, string cover, long generation = 1, bool source = true) =>
            JsonSerializer.SerializeToElement(NativeCardBridge.FilterArtwork(Frame(revision, cover, source), artwork, generation, true))
                .GetProperty("payload").GetProperty("media");
        Assert.Equal("A", Filter(1, "A").GetProperty("coverDataUrl").GetString());
        Assert.False(Filter(2, "A").TryGetProperty("coverDataUrl", out _));
        Filter(0, "B"); // The client discards this stale reply; artwork memory must do the same.
        Assert.Equal("B", Filter(3, "B").GetProperty("coverDataUrl").GetString());
        Filter(4, "B", source: false);
        Assert.Equal("icon", Filter(5, "B").GetProperty("sources")[0].GetProperty("iconDataUrl").GetString());
        var reconnected = Filter(1, "B", generation: 2);
        Assert.Equal("B", reconnected.GetProperty("coverDataUrl").GetString());
        Assert.Equal("icon", reconnected.GetProperty("sources")[0].GetProperty("iconDataUrl").GetString());
    }
}
