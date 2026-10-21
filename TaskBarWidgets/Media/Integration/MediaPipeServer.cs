using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

namespace TaskBarWidgets.Media.Integration;

internal sealed class MediaPipeServer : IAsyncDisposable
{
    internal const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly string _name;
    private readonly Func<object> _initialize;
    private readonly Func<object> _snapshot;
    private readonly Func<JsonElement, long, Task> _command;
    private readonly Action<bool, long>? _connectionChanged;
    private readonly CancellationTokenSource _stop = new();
    // Display snapshots are complete. Replacing one never loses state; commands use
    // a separate reliable bounded queue and are never coalesced.
    private readonly LatestDisplayQueue _outgoing = new();
    private Task? _run;
    // A scoped entry is a reply addressed to one connection generation. A zero
    // generation keeps the old behaviour for protocol notices that any current or
    // next client may consume.
    private readonly record struct OutboundCommand(object Payload, long Generation);
    private readonly Channel<OutboundCommand> _commands = Channel.CreateBounded<OutboundCommand>(16);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private long _connectionGeneration;
    private bool _disposed;

    internal MediaPipeServer(string name, Func<object> initialize, Func<object> snapshot, Func<JsonElement, long, Task> command,
        Action<bool, long>? connectionChanged = null)
    {
        _name = name;
        _initialize = initialize;
        _snapshot = snapshot;
        _command = command;
        _connectionChanged = connectionChanged;
    }

    internal long ConnectionGeneration => Volatile.Read(ref _connectionGeneration);
    internal bool IsConnectionCurrent(long generation) => generation != 0 && ConnectionGeneration == generation;
    internal void Start() => _run = RunAsync(_stop.Token);
    internal void Publish(object frame) { _outgoing.Publish(frame); _wake.Writer.TryWrite(true); }
    internal async Task SendCommandAsync(object command, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await _commands.Writer.WriteAsync(new OutboundCommand(command, 0), linked.Token).ConfigureAwait(false);
        _wake.Writer.TryWrite(true);
    }
    internal async Task SendCommandAsync(long generation, object command, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await _commands.Writer.WriteAsync(new OutboundCommand(command, generation), linked.Token).ConfigureAwait(false);
        _wake.Writer.TryWrite(true);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                // Every accepted connection owns a server-side generation. Requests
                // capture it on the reader; retiring it on close makes their admission
                // stale without re-reading the current generation at execution time.
                var generation = Interlocked.Increment(ref _connectionGeneration);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    _outgoing.Clear();
                    await WriteAsync(pipe, _initialize(), connection.Token).ConfigureAwait(false);
                    var filter = new MediaFrameFilter();
                    await WriteAsync(pipe, filter.Filter(_snapshot()), connection.Token).ConfigureAwait(false);
                    _connectionChanged?.Invoke(true, generation);
                    var reader = ReadCommandsAsync(pipe, generation, connection.Token);
                    var writer = WriteFramesAsync(pipe, filter, generation, connection.Token);
                    await Task.WhenAny(reader, writer).ConfigureAwait(false);
                    connection.Cancel();
                    try { await Task.WhenAll(reader, writer).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
                }
                finally
                {
                    // Retire before notifying: once the bridge hears about the close,
                    // queued work from this generation must already be rejectable.
                    Interlocked.CompareExchange(ref _connectionGeneration, generation + 1, generation);
                    _connectionChanged?.Invoke(false, generation);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                TaskbarLyrics.Core.Utilities.Log.Warn($"Media pipe disconnected: {exception.Message}");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteFramesAsync(Stream pipe, MediaFrameFilter filter, long generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_commands.Reader.TryRead(out var command))
            {
                // A reply produced for a retired connection is dropped instead of
                // being delivered to the client that reconnected in the meantime.
                if (command.Generation != 0 && command.Generation != generation) continue;
                await WriteAsync(pipe, command.Payload, cancellationToken).ConfigureAwait(false);
            }
            if (_outgoing.TryTake(out var frame) && frame is not null)
            {
                await WriteAsync(pipe, filter.Filter(frame), cancellationToken).ConfigureAwait(false);
                continue;
            }
            await _wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(Stream pipe, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Media frame exceeds protocol limit.");
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { 10 }, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadCommandsAsync(Stream pipe, long generation, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var length = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (length == 0) return;
            for (var i = 0; i < length; ++i)
            {
                if (buffer[i] == 10)
                {
                    var bytes = message.ToArray();
                    message.SetLength(0);
                    try
                    {
                        using var json = JsonDocument.Parse(bytes);
                        if (IsCommand(json.RootElement)) await _command(json.RootElement.Clone(), generation).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
                    {
                        TaskbarLyrics.Core.Utilities.Log.Warn($"Rejected media command: {exception.Message}");
                    }
                }
                else
                {
                    if (message.Length >= MaximumMessageBytes) throw new InvalidDataException("Media command exceeds protocol limit.");
                    message.WriteByte(buffer[i]);
                }
            }
        }
    }

    internal static bool IsCommand(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1 &&
        value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "command" &&
        value.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _commands.Writer.TryComplete();
        _wake.Writer.TryComplete();
        if (_run is not null)
        {
            try { await _run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
