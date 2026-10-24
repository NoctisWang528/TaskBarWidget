using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using TaskbarLyrics.Core.Utilities;

namespace TaskBarWidgets.Media.Integration;

// Server side of the card-only pipe: TaskBarWidgets.Cards.<nativePid>.
//
// The native card front-end is the client; Media stays the authority for media,
// audio, weather, history, settings and update state. The protocol has exactly
// three messages:
//   cardSnapshot  a complete, replaceable state (each part carries its revision)
//   cardRequest   requestId + payload { action, ... }
//   cardReply     requestId + success/error/payload
// Snapshots coalesce because replacing one never loses state; requests and
// replies use a bounded reliable queue so ordering survives.
internal sealed class NativeCardBridge : IAsyncDisposable
{
    internal const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly string _name;
    private readonly Func<object> _snapshot;
    private readonly Func<JsonElement, long, CancellationToken, Task<object?>> _execute;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Outbound> _outgoing = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(32)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private long _generation;
    private Task? _run;
    private int _disposed;
    private volatile bool _connected;

    // A snapshot may be replaced at any time; a reply belongs to the connection
    // that admitted its request, exactly like the UI channel.
    private readonly record struct Outbound(object Payload, long Generation, bool Snapshot);

    internal NativeCardBridge(string name, Func<object> snapshot,
        Func<JsonElement, long, CancellationToken, Task<object?>> execute)
    {
        _name = name;
        _snapshot = snapshot;
        _execute = execute;
    }

    internal long ConnectionGeneration => Interlocked.Read(ref _generation);
    internal bool IsConnectionCurrent(long generation) => generation != 0 && ConnectionGeneration == generation;
    internal bool Connected => _connected;

    internal void Start() => _run = RunAsync(_stop.Token);

    // Called whenever a part of the snapshot changed. The latest snapshot wins.
    internal void PublishSnapshot()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Write(new Outbound(new object(), 0, true));
    }

    private void Write(Outbound outbound)
    {
        if (_outgoing.Writer.TryWrite(outbound)) _wake.Writer.TryWrite(true);
    }

    internal Task SendReplyAsync(long generation, object reply, CancellationToken cancellationToken) =>
        _outgoing.Writer.WriteAsync(new Outbound(reply, generation, false), cancellationToken).AsTask();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var generation = Interlocked.Increment(ref _generation);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _connected = true;
                // Nothing queued for an older connection may leak into this one.
                while (_outgoing.Reader.TryRead(out _)) { }
                try
                {
                    // Full state first, then deltas. A renderer reset reconnects.
                    await WriteAsync(pipe, new { version = 1, type = "command", action = "cardReady",
                        renderVersion = 2 }, connection.Token).ConfigureAwait(false);
                    await WriteAsync(pipe, Envelope(_snapshot()), connection.Token).ConfigureAwait(false);
                    var reader = ReadAsync(pipe, generation, connection.Token);
                    var writer = WriteAsync(pipe, generation, connection.Token);
                    await Task.WhenAny(reader, writer).ConfigureAwait(false);
                    connection.Cancel();
                    try { await Task.WhenAll(reader, writer).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException
                        or JsonException or InvalidDataException) { }
                }
                finally
                {
                    _connected = false;
                    Interlocked.CompareExchange(ref _generation, generation + 1, generation);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or InvalidDataException)
            {
                Log.Warn($"Card pipe disconnected: {exception.Message}");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static object Envelope(object snapshot) =>
        new { version = 1, type = "command", action = "cardSnapshot", payload = snapshot };

    private async Task WriteAsync(Stream pipe, long generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_outgoing.Reader.TryRead(out var outbound))
            {
                if (outbound.Generation != 0 && outbound.Generation != generation) continue;
                var payload = outbound.Snapshot ? Envelope(_snapshot()) : outbound.Payload;
                await WriteAsync(pipe, payload, cancellationToken).ConfigureAwait(false);
            }
            await _wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(Stream pipe, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Card frame exceeds protocol limit.");
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { 10 }, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadAsync(Stream pipe, long generation, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var length = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (length == 0) return;
            for (var index = 0; index < length; ++index)
            {
                if (buffer[index] == 10)
                {
                    var bytes = message.ToArray();
                    message.SetLength(0);
                    try
                    {
                        using var json = JsonDocument.Parse(bytes);
                        await HandleAsync(pipe, json.RootElement.Clone(), generation, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException
                        or ArgumentException or System.Runtime.InteropServices.COMException)
                    {
                        Log.Warn($"Rejected card command: {exception.Message}");
                    }
                }
                else
                {
                    if (message.Length >= MaximumMessageBytes) throw new InvalidDataException("Card command exceeds protocol limit.");
                    message.WriteByte(buffer[index]);
                }
            }
        }
    }

    private async Task HandleAsync(Stream pipe, JsonElement command, long generation, CancellationToken cancellationToken)
    {
        if (command.ValueKind != JsonValueKind.Object ||
            !command.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) || number != 1 ||
            !command.TryGetProperty("type", out var type) || type.GetString() != "command" ||
            !command.TryGetProperty("action", out var action) || action.GetString() != "cardRequest") return;

        var requestId = command.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? "" : "";
        if (requestId.Length is 0 or > 128) return;
        var payload = command.TryGetProperty("payload", out var body) && body.ValueKind == JsonValueKind.Object
            ? body : default;

        object? result = null;
        string? error = null;
        try
        {
            // The generation was captured at admission; executing for a retired
            // connection would mutate state for a client that is already gone.
            if (!IsConnectionCurrent(generation)) throw new OperationCanceledException("卡片连接已失效。");
            result = await _execute(payload, generation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("卡片请求没有结果。");
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        // A single shape keeps the client parser simple: payload is present on
        // success, error on failure, and the unused member is simply omitted.
        object reply = error is null
            ? new { version = 1, type = "command", action = "cardReply", requestId, success = true, payload = result }
            : new { version = 1, type = "command", action = "cardReply", requestId, success = false, error };
        await WriteAsync(pipe, reply, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        _wake.Writer.TryComplete();
        if (_run is not null)
        {
            try { await _run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
