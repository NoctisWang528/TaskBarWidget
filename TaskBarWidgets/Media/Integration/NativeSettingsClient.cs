using System.Collections.Concurrent;
using System.Text.Json;

namespace TaskBarWidgets.Media.Integration;

internal sealed class NativeSettingsClient : IDisposable
{
    private readonly Func<object, CancellationToken, Task> _send;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _lifetime;
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    internal NativeSettingsClient(Func<object, CancellationToken, Task> send, TimeSpan? timeout = null)
    {
        _send = send;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        // The native host validates expiresAt when it dequeues a request, after the previous one
        // finished. Advertise a lifetime several times longer than the wait below so a queued
        // settings write is never discarded before it is attempted; the caller still stops waiting
        // at _timeout, and an expired rejection is retried once below.
        _lifetime = TimeSpan.FromTicks(Math.Max(_timeout.Ticks * 3, TimeSpan.FromSeconds(90).Ticks));
    }

    internal async Task<JsonElement> RequestAsync(string action, JsonElement? values = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            return await RequestOnceAsync(action, values, cancellationToken);
        }
        catch (InvalidOperationException error) when (IsExpiredRejection(error))
        {
            // Settings writes are absolute assignments, so re-sending once after the native host
            // dropped an expired request keeps a stalled queue from surfacing as a failed save.
            return await RequestOnceAsync(action, values, cancellationToken);
        }
    }

    private static bool IsExpiredRejection(InvalidOperationException error) =>
        error.Message.Contains("expired", StringComparison.OrdinalIgnoreCase);

    private async Task<JsonElement> RequestOnceAsync(string action, JsonElement? values, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        timeout.CancelAfter(_timeout);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _send(new { version = 1, type = "command", action, requestId = id,
                expiresAt = DateTimeOffset.UtcNow.Add(_lifetime).ToUnixTimeMilliseconds(), payload = values }, timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("主程序未及时响应，请稍后重试；当前页面不会将未确认的修改标记为已保存。");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    internal bool Receive(JsonElement message)
    {
        if (!message.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
            !_pending.TryGetValue(id.GetString()!, out var completion)) return false;
        if (message.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True &&
            message.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            completion.TrySetResult(payload.Clone());
        else
        {
            var error = message.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() : "主程序拒绝了此设置请求。";
            var rejection = new InvalidOperationException(error);
            if (message.TryGetProperty("payload", out var actual) && actual.ValueKind == JsonValueKind.Object)
                rejection.Data["NativeSettingsReadback"] = actual.Clone();
            completion.TrySetException(rejection);
        }
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _stop.Dispose();
    }
}
