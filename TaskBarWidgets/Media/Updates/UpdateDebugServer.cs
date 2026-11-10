using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace TaskBarWidgets.Media.Updates;

/// <summary>Current-user local import only. Installation still requires the shared UI confirmation.</summary>
internal sealed class UpdateDebugServer(string pipeName, UpdateCoordinator updates) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private int _disposed;

    internal static bool IsEnabled(string installationRoot)
    {
        try
        {
            var marker = UpdatePackage.ChildPath(installationRoot, "Data/config/update-debug.enabled");
            return File.Exists(marker);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal void Start() => _worker ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                string? requestId = null;
                object response;
                try
                {
                    var bytes = await ReadRequestAsync(pipe, deadline.Token);
                    var request = UpdatePackage.ParseJson<Request>(bytes);
                    if (Guid.TryParseExact(request.RequestId, "D", out _)) requestId = request.RequestId;
                    if (request.Version != 1 || requestId is null ||
                        request.Action != "importLocalUpdate" || string.IsNullOrWhiteSpace(request.ReleaseDirectory))
                        throw new InvalidDataException("Invalid local update request.");
                    deadline.CancelAfter(TimeSpan.FromMinutes(5));
                    await updates.Initialization.WaitAsync(deadline.Token);
                    var snapshot = await updates.ImportLocalAsync(request.ReleaseDirectory, deadline.Token);
                    var success = snapshot.Phase == "ready" && snapshot.CanInstall;
                    response = new { version = 1, requestId, success, snapshot = success ? snapshot : null,
                        error = success ? null : snapshot.Error ?? snapshot.Message ?? "Local update import failed." };
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
                {
                    response = new { version = 1, requestId, success = false,
                        error = error is InvalidOperationException or InvalidDataException ? error.Message : "Invalid or inaccessible local update package." };
                }
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var reply = JsonSerializer.SerializeToUtf8Bytes(response, UpdatePackage.Json);
                if (reply.Length + 1 > 64 * 1024)
                    reply = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, requestId, success = false,
                        error = "Local update response exceeds its size limit." }, UpdatePackage.Json);
                await pipe.WriteAsync(reply, deadline.Token);
                await pipe.WriteAsync(new byte[] { 10 }, deadline.Token);
                await pipe.FlushAsync(deadline.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // A rejected client or unavailable pipe must not stop Media or spin on failures.
            if (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(100, _stop.Token); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task<byte[]> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        using var request = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) throw new InvalidDataException("Incomplete local update request.");
            var newline = Array.IndexOf(buffer, (byte)10, 0, count);
            var length = newline >= 0 ? newline : count;
            if (request.Length + length + (newline >= 0 ? 1 : 0) > 16 * 1024) throw new InvalidDataException("Local update request exceeds its size limit.");
            request.Write(buffer, 0, length);
            if (newline >= 0) return request.ToArray();
        }
    }

    private sealed record Request(int Version, string RequestId, string Action, string ReleaseDirectory);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_worker is not null) await _worker;
        _stop.Dispose();
    }
}
