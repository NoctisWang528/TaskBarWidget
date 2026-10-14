using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace TaskBarWidgets.UI;

public sealed class UiConnection : IDisposable
{
    public const int MaximumMessageBytes = 4 * 1024 * 1024;
    private const int MaximumPendingRequests = 64;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _requestSlots = new(MaximumPendingRequests, MaximumPendingRequests);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private Task? _receiveTask;
    private int _started;
    private int _disposed;

    public UiConnection(string pipeName) => _pipeName = pipeName;
    public bool IsConnected => Volatile.Read(ref _pipe)?.IsConnected == true;
    public event Action<JsonElement>? EventReceived;
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0) _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public async Task<JsonElement> RequestAsync(string action, object? payload = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("An action is required.", nameof(action));
        if (!await _requestSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new IOException("Too many pending UI commands.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(action == "settingsCommand" ? 90 : 15));
            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "command", action, requestId = id, payload });
            if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("UI command exceeds 4 MiB.");
            await _writer.WaitAsync(timeout.Token).ConfigureAwait(false);
            NamedPipeClientStream? pipe = null;
            var writing = false;
            try
            {
                pipe = Volatile.Read(ref _pipe);
                if (pipe?.IsConnected != true) throw new IOException("The media backend is disconnected.");
                _pending[id] = completion;
                writing = true;
                await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                // A cancelled/failed write may leave a partial JSON frame; reconnect before sending again.
                if (writing) pipe?.Dispose();
                throw;
            }
            finally { _writer.Release(); }
            try { return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            finally { _pending.TryRemove(id, out _); }
        }
        finally { _requestSlots.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(2000, _lifetime.Token).ConfigureAwait(false);
                Volatile.Write(ref _pipe, pipe);
                ConnectionChanged?.Invoke(true);
                await ReadMessagesAsync(pipe, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
            finally
            {
                Volatile.Write(ref _pipe, null);
                foreach (var request in _pending)
                    if (_pending.TryRemove(request.Key, out var completion)) completion.TrySetException(new IOException("The media backend disconnected."));
                ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(500, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReadMessagesAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    if (message.Length >= MaximumMessageBytes) throw new InvalidDataException("UI event exceeds 4 MiB.");
                    message.WriteByte(buffer[index]);
                    continue;
                }
                if (message.Length == 0) continue;
                using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                message.SetLength(0);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out var number) || number != 1 || !root.TryGetProperty("action", out var action) ||
                    action.ValueKind != JsonValueKind.String) continue;
                if (action.GetString() == "uiResult" && root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    if (!_pending.TryRemove(id.GetString()!, out var completion)) continue;
                    if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                        completion.TrySetResult(root.TryGetProperty("payload", out var payload) ? payload.Clone() : JsonSerializer.SerializeToElement<object?>(null));
                    else
                        completion.TrySetException(new InvalidOperationException(root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "UI command failed."));
                }
                else EventReceived?.Invoke(root.Clone());
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        Volatile.Read(ref _pipe)?.Dispose();
        // The receive loop owns the stream and completes outstanding requests on exit.
    }
}
