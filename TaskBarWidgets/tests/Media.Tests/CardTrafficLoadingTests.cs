using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class CardTrafficLoadingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstConnectionReceivesLoadedTrafficWithoutReopening(bool cancelInitialRead)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var initialRead = new CancellationTokenSource();
        var nativeRequests = Channel.CreateUnbounded<JsonElement>();
        using var native = new NativeSettingsClient((message, token) =>
            nativeRequests.Writer.WriteAsync(JsonSerializer.SerializeToElement(message), token).AsTask());
        var builder = new CardSnapshotBuilder();
        var gate = new object();
        // Project the real cache, as Build does, without constructing unrelated
        // media/audio services. This tests transport/cache, not App's timer.
        var cache = typeof(CardSnapshotBuilder).GetField("_lastTraffic", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(cache);
        var name = "TaskBarWidgets.Cards.traffic-test." + Guid.NewGuid().ToString("N");
        NativeCardBridge? publisher = null;
        var reads = 0;
        await using var bridge = new NativeCardBridge(name,
            _ =>
            {
                lock (gate)
                    return Task.FromResult<object>(new Dictionary<string, object?> { ["traffic"] = cache!.GetValue(builder) });
            },
            async (request, _, token) =>
            {
                Assert.Equal("getTrafficHistory", request.GetProperty("action").GetString());
                using var read = CancellationTokenSource.CreateLinkedTokenSource(token,
                    Interlocked.Increment(ref reads) == 1 ? initialRead.Token : CancellationToken.None);
                var history = await native.RequestAsync("getTrafficHistory", cancellationToken: read.Token);
                object traffic;
                lock (gate) traffic = builder.SetTraffic(history);
                publisher!.PublishSnapshot();
                return traffic;
            });
        publisher = bridge;
        bridge.Start();
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);

        async Task<JsonElement> ReadFrame()
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            using var json = JsonDocument.Parse(line!);
            return json.RootElement.Clone();
        }

        async Task SendHistoryRequest(string id)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, type = "command", action = "cardRequest", requestId = id,
                payload = new { action = "getTrafficHistory", payload = new { } }
            });
            await pipe.WriteAsync(bytes, timeout.Token);
            await pipe.WriteAsync(new byte[] { 10 }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }

        static JsonElement Reply(JsonElement request) => JsonSerializer.SerializeToElement(new
        {
            version = 1, type = "command", action = "trafficHistory",
            requestId = request.GetProperty("requestId").GetString(), success = true,
            payload = new
            {
                today = "2026-10-26", scopeLabel = "test adapters",
                days = new[] { new { date = "2026-10-25", totalKiB = 2048 }, new { date = "2026-10-26", totalKiB = 4096 } }
            }
        });

        Assert.Equal("cardReady", (await ReadFrame()).GetProperty("action").GetString());
        var empty = await ReadFrame();
        Assert.Equal("cardSnapshot", empty.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("payload").GetProperty("traffic").ValueKind);
        var generation = bridge.ConnectionGeneration;
        Assert.True(generation > 0);
        string? canceledId = null;

        if (cancelInitialRead)
        {
            await SendHistoryRequest("traffic-canceled");
            var canceled = await nativeRequests.Reader.ReadAsync(timeout.Token);
            Assert.Equal("getTrafficHistory", canceled.GetProperty("action").GetString());
            canceledId = canceled.GetProperty("requestId").GetString();
            initialRead.Cancel();
            var failure = await ReadFrame();
            Assert.Equal("cardReply", failure.GetProperty("action").GetString());
            Assert.Equal("traffic-canceled", failure.GetProperty("requestId").GetString());
            Assert.False(failure.GetProperty("success").GetBoolean());
            Assert.False(native.Receive(Reply(canceled))); // A late reply cannot resurrect the canceled read.
            Assert.Equal(0, builder.TrafficRevision);
            bridge.PublishSnapshot();
            var stillEmpty = await ReadFrame();
            Assert.Equal("cardSnapshot", stillEmpty.GetProperty("action").GetString());
            Assert.Equal(JsonValueKind.Null, stillEmpty.GetProperty("payload").GetProperty("traffic").ValueKind);
        }

        await SendHistoryRequest("traffic-loaded");
        var pending = await nativeRequests.Reader.ReadAsync(timeout.Token);
        Assert.Equal("getTrafficHistory", pending.GetProperty("action").GetString());
        Assert.NotEqual(canceledId, pending.GetProperty("requestId").GetString());
        Assert.True(native.Receive(Reply(pending)));

        JsonElement? loaded = null, direct = null;
        while (loaded is null || direct is null)
        {
            var frame = await ReadFrame();
            switch (frame.GetProperty("action").GetString())
            {
                case "cardSnapshot":
                    loaded = frame.GetProperty("payload").GetProperty("traffic");
                    break;
                case "cardReply":
                    Assert.Equal("traffic-loaded", frame.GetProperty("requestId").GetString());
                    Assert.True(frame.GetProperty("success").GetBoolean());
                    direct = frame.GetProperty("payload");
                    break;
                default:
                    Assert.Fail("Unexpected handshake/event while the original card connection is still open.");
                    break;
            }
        }

        foreach (var traffic in new[] { loaded.Value, direct.Value })
        {
            Assert.True(traffic.GetProperty("available").GetBoolean());
            Assert.Equal(1, traffic.GetProperty("revision").GetInt64());
            Assert.Equal("2026-10-26", traffic.GetProperty("today").GetString());
            Assert.Equal("test adapters", traffic.GetProperty("scopeLabel").GetString());
            Assert.Equal(6144d, traffic.GetProperty("totalKiB").GetDouble());
            var days = traffic.GetProperty("days").EnumerateArray().ToArray();
            Assert.Equal(84, days.Length);
            Assert.Equal(84, days.Select(day => day.GetProperty("date").GetString()).Distinct().Count());
            Assert.Equal(2, days.Count(day => day.GetProperty("known").GetBoolean()));
            Assert.Equal(6, days.Count(day => day.GetProperty("isFuture").GetBoolean()));
            Assert.Equal(4096d, Assert.Single(days, day => day.GetProperty("isToday").GetBoolean()).GetProperty("totalKiB").GetDouble());
        }
        Assert.Equal(1, builder.TrafficRevision);
        Assert.Equal(cancelInitialRead ? 2 : 1, reads);
        Assert.Equal(generation, bridge.ConnectionGeneration);
        Assert.True(pipe.IsConnected);
    }
}
