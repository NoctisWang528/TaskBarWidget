using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using TaskbarLyrics.Core.Utilities;

namespace TaskBarWidgets.Media.Integration;

// Complete snapshots coalesce; replies use a bounded reliable queue. Only the
// writer touches the pipe or advances artwork memory, after taking the snapshot.
internal sealed class NativeCardBridge : IAsyncDisposable
{
    internal const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly string _name;
    private readonly Func<CancellationToken, Task<object>> _snapshot;
    private readonly Func<JsonElement, long, CancellationToken, Task<object?>> _execute;
    private readonly CancellationTokenSource _stop = new();
    private readonly UiRequestDispatcher _requests;
    private readonly Channel<Outbound> _outgoing = Channel.CreateBounded<Outbound>(32);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly object _gate = new();
    private CancellationToken _connectionToken;
    private long _generation, _lyricsSession;
    private bool _lyricsVisible;
    private int _snapshotPending, _disposed;
    private volatile bool _connected, _artworkDelta;
    private Task? _run;
    private readonly record struct Outbound(object Payload, long Generation);
    internal event Action? VisibilityChanged;

    internal NativeCardBridge(string name, Func<CancellationToken, Task<object>> snapshot,
        Func<JsonElement, long, CancellationToken, Task<object?>> execute)
    {
        _name = name; _snapshot = snapshot; _execute = execute;
        _requests = new UiRequestDispatcher(ExecuteAsync, isCurrent: IsConnectionCurrent);
    }

    internal long ConnectionGeneration => Interlocked.Read(ref _generation);
    internal bool IsConnectionCurrent(long generation) => _connected && generation != 0 && ConnectionGeneration == generation;
    internal bool Connected => _connected;
    internal bool CardLyricsVisible { get { lock (_gate) return _lyricsVisible; } }
    internal void Start() => _run = RunAsync(_stop.Token);

    internal bool TrySubscribeCardLyrics(bool visible, long session, long generation)
    {
        lock (_gate)
        {
            if (!IsConnectionCurrent(generation) || session < _lyricsSession) return false;
            _lyricsSession = session;
            _lyricsVisible = visible;
        }
        VisibilityChanged?.Invoke();
        return true;
    }

    internal void PublishSnapshot()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_connected) return;
        Interlocked.Exchange(ref _snapshotPending, 1);
        _wake.Writer.TryWrite(true);
    }

    private async Task<object?> ExecuteAsync(JsonElement request, long generation, CancellationToken token)
    {
        CancellationToken connectionToken;
        lock (_gate)
        {
            if (!IsConnectionCurrent(generation)) throw new OperationCanceledException("卡片连接已失效。");
            connectionToken = _connectionToken;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connectionToken);
        linked.CancelAfter(TimeSpan.FromSeconds(90));
        return await _execute(request, generation, linked.Token).ConfigureAwait(false);
    }

    private async Task QueueAsync(long generation, object reply, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        await _outgoing.Writer.WriteAsync(new Outbound(reply, generation), linked.Token).ConfigureAwait(false);
        _wake.Writer.TryWrite(true);
    }

    private Task CompleteAsync(UiRequestResult result, CancellationToken token)
    {
        if (!IsConnectionCurrent(result.ConnectionGeneration)) return Task.CompletedTask;
        var requestId = result.Request.GetProperty("requestId").GetString();
        var reply = Message("cardReply", result.Value, requestId, result.Error is null, result.Error?.Message);
        return QueueAsync(result.ConnectionGeneration, reply, token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                long generation;
                lock (_gate)
                {
                    generation = Interlocked.Increment(ref _generation);
                    _connectionToken = connection.Token;
                    _lyricsSession = 0; _lyricsVisible = false;
                    _artworkDelta = false; _connected = true;
                }
                try
                {
                    while (_outgoing.Reader.TryRead(out _)) { }
                    Interlocked.Exchange(ref _snapshotPending, 1);
                    var reader = ReadAsync(pipe, generation, connection.Token);
                    var writer = WriteAsync(pipe, generation, connection.Token);
                    await Task.WhenAny(reader, writer).ConfigureAwait(false);
                    connection.Cancel();
                    try { await Task.WhenAll(reader, writer).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
                }
                finally
                {
                    lock (_gate)
                    {
                        _connected = false; _lyricsVisible = false;
                        Interlocked.Increment(ref _generation);
                    }
                    connection.Cancel();
                    VisibilityChanged?.Invoke();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Log.Warn($"Card pipe disconnected: {error.GetType().Name}");
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAsync(Stream pipe, long generation, CancellationToken token)
    {
        var artwork = new ArtworkDeltaState();
        await WriteFrameAsync(pipe, new { version = 1, type = "command", action = "cardReady", renderVersion = 2 }, token).ConfigureAwait(false);
        while (!token.IsCancellationRequested)
        {
            // Snapshot first: a reply produced while building it still follows it,
            // so older cached audio can never follow the newer mutation readback.
            if (Interlocked.Exchange(ref _snapshotPending, 0) != 0)
            {
                var snapshot = await _snapshot(token).ConfigureAwait(false);
                await WriteFrameAsync(pipe, FilterArtwork(Message("cardSnapshot", snapshot),
                    artwork, generation, _artworkDelta), token).ConfigureAwait(false);
            }
            while (_outgoing.Reader.TryRead(out var reply))
            {
                if (reply.Generation != generation) continue;
                await WriteFrameAsync(pipe, FilterArtwork(reply.Payload, artwork, generation, _artworkDelta), token).ConfigureAwait(false);
            }
            if (Volatile.Read(ref _snapshotPending) != 0) continue;
            await _wake.Reader.ReadAsync(token).ConfigureAwait(false);
        }
    }

    // Opt-in only: old parsers clear omitted images. Keep memory at the actual
    // writer, never at publication time where coalescing can discard a full image.
    private static Dictionary<string, object?> Message(string action, object? payload = null,
        string? requestId = null, bool? success = null, string? error = null) => new()
    {
        ["version"] = 1, ["type"] = "command", ["action"] = action,
        ["payload"] = payload, ["requestId"] = requestId, ["success"] = success, ["error"] = error,
    };

    internal static object FilterArtwork(object frame, ArtworkDeltaState artwork, long generation, bool requested)
    {
        // Copy dictionaries shallowly: unchanged base64 strings are never copied
        // or serialized merely to discover that they can be omitted.
        if (frame is not Dictionary<string, object?> envelope ||
            envelope.GetValueOrDefault("payload") is not Dictionary<string, object?> payload) return frame;
        var nested = payload.GetValueOrDefault("media") as Dictionary<string, object?>;
        var media = nested ?? (payload.ContainsKey("sources") ? payload : null);
        if (media is null) return frame;
        var delta = artwork.Begin(generation, requested);
        // A queued reply can be older than a freshly built snapshot. The native
        // revision guard will discard it; it must not poison our artwork memory.
        if (!artwork.AdvanceRevision(Convert.ToInt64(media.GetValueOrDefault("revision")))) return frame;
        var filtered = new Dictionary<string, object?>(media);
        var cover = media.GetValueOrDefault("coverDataUrl") as string ?? "";
        if (!artwork.CoverChanged(cover, delta)) filtered.Remove("coverDataUrl");
        artwork.CommitCover(cover);
        if (media.GetValueOrDefault("sources") is IEnumerable<Dictionary<string, object?>> sources)
        {
            var items = sources.ToArray();
            artwork.RetainIcons(items.Select(source => (string)source["id"]!));
            filtered["sources"] = items.Select(source =>
            {
                var id = (string)source["id"]!;
                var icon = source.GetValueOrDefault("iconDataUrl") as string ?? "";
                if (artwork.IconChanged(id, icon, delta)) return source;
                var copy = new Dictionary<string, object?>(source);
                copy.Remove("iconDataUrl");
                return copy;
            }).ToArray();
        }
        var root = new Dictionary<string, object?>(envelope);
        if (nested is null) root["payload"] = filtered;
        else
        {
            var state = new Dictionary<string, object?>(payload) { ["media"] = filtered };
            root["payload"] = state;
        }
        return root;
    }

    private static async Task WriteFrameAsync(Stream pipe, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Card frame exceeds protocol limit.");
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { 10 }, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task ReadAsync(Stream pipe, long generation, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var length = await pipe.ReadAsync(buffer, token).ConfigureAwait(false);
            if (length == 0) return;
            for (var index = 0; index < length; ++index)
            {
                if (buffer[index] != 10)
                {
                    if (message.Length >= MaximumMessageBytes) throw new InvalidDataException("Card command exceeds protocol limit.");
                    message.WriteByte(buffer[index]);
                    continue;
                }
                if (message.Length == 0) continue;
                using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                message.SetLength(0);
                var command = json.RootElement;
                if (!MediaPipeServer.IsCommand(command) || command.GetProperty("action").GetString() != "cardRequest" ||
                    !command.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
                    id.GetString() is not { Length: > 0 and <= 128 } requestId ||
                    !command.TryGetProperty("payload", out var body) || body.ValueKind != JsonValueKind.Object) continue;
                var request = System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();
                request["requestId"] = requestId;
                var admitted = JsonSerializer.SerializeToElement(request);
                var arguments = body.TryGetProperty("payload", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : body;
                if (arguments.TryGetProperty("artworkDelta", out var flag) && flag.ValueKind == JsonValueKind.True) _artworkDelta = true;
                if (!_requests.TryPost(admitted, generation, CompleteAsync))
                    await QueueAsync(generation, new { version = 1, type = "command", action = "cardReply", requestId,
                        success = false, error = "请求队列已满，请稍后重试。" }, token).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        await _requests.DisposeAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try { await _run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _outgoing.Writer.TryComplete(); _wake.Writer.TryComplete();
        _stop.Dispose();
    }
}
