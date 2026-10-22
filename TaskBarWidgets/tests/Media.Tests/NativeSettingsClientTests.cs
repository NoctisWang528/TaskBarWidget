using System.Text.Json;
using TaskBarWidgets.Media.Integration;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class NativeSettingsClientTests
{
    [Fact]
    public async Task ClosingPanelCancelsHistoryRequestWithoutReportingTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new NativeSettingsClient((_, _) => Task.CompletedTask);
        var request = client.RequestAsync("getTrafficHistory", cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task CorrelatesOutOfOrderRepliesAndReturnsDetachedPayload()
    {
        var requests = new List<JsonElement>();
        using var client = new NativeSettingsClient((message, _) =>
        {
            requests.Add(JsonSerializer.SerializeToElement(message));
            return Task.CompletedTask;
        });
        var first = client.RequestAsync("getNativeSettings");
        var second = client.RequestAsync("getNativeSettings");
        Assert.Equal(2, requests.Count);
        using (var reply = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            requestId = requests[1].GetProperty("requestId").GetString(), success = true, payload = new { revision = 2 }
        }))) Assert.True(client.Receive(reply.RootElement));
        Assert.Equal(2, (await second).GetProperty("revision").GetInt32());
        Assert.False(first.IsCompleted);
        client.Receive(JsonSerializer.SerializeToElement(new
        {
            requestId = requests[0].GetProperty("requestId").GetString(), success = true, payload = new { revision = 1 }
        }));
        Assert.Equal(1, (await first).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task TimeoutCancelsBlockedQueueAndRejectsLateReply()
    {
        string? id = null;
        using var client = new NativeSettingsClient(async (message, token) =>
        {
            id = JsonSerializer.SerializeToElement(message).GetProperty("requestId").GetString();
            await Task.Delay(Timeout.Infinite, token);
        }, TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("getNativeSettings"));
        Assert.False(client.Receive(JsonSerializer.SerializeToElement(new { requestId = id, success = true, payload = new { } })));
    }

    [Fact]
    public async Task NativeValidationFailureDoesNotLookLikeSuccessfulSave()
    {
        object? sent = null;
        using var client = new NativeSettingsClient((message, _) => { sent = message; return Task.CompletedTask; });
        var request = client.RequestAsync("applyNativeSettings", JsonSerializer.SerializeToElement(new { font_size = -1 }));
        var id = JsonSerializer.SerializeToElement(sent).GetProperty("requestId").GetString();
        client.Receive(JsonSerializer.SerializeToElement(new { requestId = id, success = false, error = "字号超出范围", payload = new { fields = new[] { new { key = "font_size", value = 12 } } } }));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Equal("字号超出范围", exception.Message);
        var readback = Assert.IsType<JsonElement>(exception.Data["NativeSettingsReadback"]);
        Assert.Equal(12, readback.GetProperty("fields")[0].GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task ExpiredRejectionIsRetriedOnceWithoutFailingTheSave()
    {
        var sent = new List<(string Id, long ExpiresAt)>();
        using var client = new NativeSettingsClient((message, _) =>
        {
            var element = JsonSerializer.SerializeToElement(message);
            sent.Add((element.GetProperty("requestId").GetString()!,
                element.GetProperty("expiresAt").GetInt64()));
            return Task.CompletedTask;
        });
        var request = client.RequestAsync("applyNativeSettings", JsonSerializer.SerializeToElement(new { taskbar_transparent = true }));
        client.Receive(JsonSerializer.SerializeToElement(new
        {
            requestId = sent[0].Id, success = false, error = "Native settings request expired; retry the operation."
        }));
        // The retry runs on the awaiting continuation, so let it observe the rejection first.
        for (var attempt = 0; attempt < 200 && sent.Count < 2; attempt++) await Task.Delay(10);
        Assert.Equal(2, sent.Count);
        Assert.NotEqual(sent[0].Id, sent[1].Id);
        client.Receive(JsonSerializer.SerializeToElement(new
        {
            requestId = sent[1].Id, success = true, payload = new { fields = new[] { new { key = "taskbar.transparent", value = true } } }
        }));
        var payload = await request;
        Assert.True(payload.GetProperty("fields")[0].GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task RequestLifetimeOutlivesTheResponseWait()
    {
        var sent = new List<JsonElement>();
        using var client = new NativeSettingsClient((message, _) =>
        {
            sent.Add(JsonSerializer.SerializeToElement(message));
            return Task.CompletedTask;
        });
        _ = client.RequestAsync("getNativeSettings");
        await Task.Delay(50);
        var expiresAt = sent[0].GetProperty("expiresAt").GetInt64();
        var remaining = expiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(remaining > 60_000, $"A queued request must stay valid well past the 30s response wait (got {remaining} ms).");
    }

    [Fact]
    public async Task DisposalCancelsOutstandingRequests()
    {
        using var client = new NativeSettingsClient((_, _) => Task.CompletedTask);
        var request = client.RequestAsync("getNativeSettings");
        client.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }
}
