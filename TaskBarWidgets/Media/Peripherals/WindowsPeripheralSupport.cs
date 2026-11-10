using System.Globalization;
using Windows.Foundation;

namespace TaskBarWidgets.Media.Peripherals;

internal sealed record WindowsPeripheralNode(
    string Path, string Name, Guid Container, string? Address, string InstanceId,
    string[] HardwareIds, string Kind, bool Present, bool? Connected, bool BluetoothLE,
    bool Bluetooth, object? Battery, bool Pnp = true, bool BatteryService = false);

internal static class WindowsPeripheralIdentity
{
    internal static string? Address(string? value)
    {
        if (value is null) return null;
        var compact = value.Replace(":", "").Replace("-", "").Trim();
        return compact.Length == 12 && compact.All(Uri.IsHexDigit) && compact != "000000000000"
            ? compact.ToLowerInvariant() : null;
    }

    internal static string? Key(WindowsPeripheralNode node, IReadOnlyList<WindowsPeripheralNode> nodes)
    {
        var address = Address(node.Address);
        if (address is null && node.Container != Guid.Empty)
        {
            var addresses = nodes.Where(n => n.Container == node.Container).Select(n => Address(n.Address))
                .Where(a => a is not null).Distinct(StringComparer.Ordinal).ToArray();
            if (addresses.Length == 1) address = addresses[0];
        }
        return address is not null ? "bluetooth:" + address
            : node.Container != Guid.Empty ? "container:" + node.Container.ToString("D") : null;
    }

    internal static int? Percentage(object? value) => value switch
    {
        byte n when n <= 100 => n,
        ushort n when n <= 100 => n,
        uint n when n <= 100 => (int)n,
        int n when n is >= 0 and <= 100 => n,
        _ => null
    };

    internal static PeripheralBatteryReading? Capacity(int? remaining, int? full, bool? charging)
    {
        if (remaining is >= 0 && full is > 0 && remaining <= full)
            return new() { Percentage = (int)Math.Round(100d * remaining.Value / full.Value), Charging = charging };
        return charging == true ? new() { Charging = true } : null;
    }

    // Stable attribute/node ordering only labels batteries; it is never a device identity.
    internal static PeripheralBatteryReading[] Roles(IEnumerable<(string Key, PeripheralBatteryReading Reading)> values)
    {
        var ordered = values.OrderBy(v => v.Key, StringComparer.Ordinal).ToArray();
        return ordered.Select((v, i) => v.Reading with
        {
            Role = !string.IsNullOrWhiteSpace(v.Reading.Role) && v.Reading.Role is not ("main" or "unknown")
                ? v.Reading.Role : ordered.Length == 1 ? "main" : "battery" + (i + 1).ToString(CultureInfo.InvariantCulture)
        }).ToArray();
    }
}

internal static class WindowsPeripheralHistory
{
    internal static void Remember(Dictionary<string, PeripheralDeviceSnapshot> known,
        PeripheralDeviceSnapshot device, IReadOnlySet<string> present)
    {
        if (!known.ContainsKey(device.Id) && known.Count >= 256)
        {
            var victim = known.Values.OrderBy(d => d.State == "disconnected" ? 0 : !present.Contains(d.Id) ? 1 : 2)
                .ThenBy(d => d.UpdatedAt).First();
            if (!present.Contains(device.Id) && present.Contains(victim.Id) && victim.State != "disconnected") return;
            known.Remove(victim.Id);
        }
        known[device.Id] = device;
    }

    internal static PeripheralDeviceSnapshot[] CurrentFirst(IEnumerable<PeripheralDeviceSnapshot> devices, IReadOnlySet<string> present) =>
        devices.OrderByDescending(d => present.Contains(d.Id)).ThenBy(d => d.State == "disconnected").Take(256).ToArray();
}

// One serialized I/O owner. Cancellation may outlive a public call; no replacement
// I/O is admitted until that owner and its native cancellation/cleanup have settled.
internal sealed class WindowsPeripheralLifetime
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HashSet<Task> _calls = [];
    private readonly Queue<Action> _retired = [];
    private readonly TimeSpan _shutdownBudget;
    private Task _pending = Task.CompletedTask;
    private Task _cleanup = Task.CompletedTask;
    private Task _blockedCall = Task.CompletedTask;
    private Task _completion = Task.CompletedTask;
    private Task? _dispose;
    private bool _disposed;
    private bool _cleanupRunning;

    internal WindowsPeripheralLifetime(TimeSpan? shutdownBudget = null) =>
        _shutdownBudget = shutdownBudget ?? TimeSpan.FromSeconds(2);
    internal Task Completion { get { lock (_sync) return _completion; } }
    internal bool Quarantined { get { lock (_sync) return !_pending.IsCompleted || !_blockedCall.IsCompleted; } }

    internal Task WaitNative(CancellationToken token)
    {
        lock (_sync) return _pending.WaitAsync(token);
    }

    internal Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pending.IsCompleted || !_blockedCall.IsCompleted)
                return Task.FromException<T>(new TimeoutException("Windows peripheral I/O is still settling."));
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            var callToken = linked.Token;
            var cleanup = _cleanup;
            var task = Task.Run(async () =>
            {
                try
                {
                    await cleanup.WaitAsync(callToken).ConfigureAwait(false);
                    await _gate.WaitAsync(callToken).ConfigureAwait(false);
                    try
                    {
                        await WaitNative(callToken).ConfigureAwait(false);
                        callToken.ThrowIfCancellationRequested();
                        return await action(callToken).ConfigureAwait(false);
                    }
                    finally { _gate.Release(); }
                }
                finally { linked.Dispose(); }
            });
            _calls.Add(task);
            _ = task.ContinueWith(t => { _ = t.Exception; lock (_sync) _calls.Remove(t); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return BoundCall();

            async Task<T> BoundCall()
            {
                try { return await task.WaitAsync(callToken).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    lock (_sync) if (!task.IsCompleted) _blockedCall = task;
                    throw;
                }
            }
        }
    }

    // Factories prevent starting a native operation before an older cancellation settles.
    internal async Task<T> Await<T>(Func<IAsyncOperation<T>> start, CancellationToken token, Action<T>? disposeLate = null)
    {
        await WaitNative(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var operation = start();
        var task = operation.AsTask();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var result = await task.WaitAsync(deadline.Token).ConfigureAwait(false);
            operation.Close();
            return result;
        }
        catch
        {
            // Cancel can itself enter a driver. There is only one cancellation task,
            // and the provider stays quarantined until it AND the result have settled.
            var cancel = Task.Run(() => { try { operation.Cancel(); } catch { } });
            var drain = Drain();
            lock (_sync) _pending = drain;
            throw;

            async Task Drain()
            {
                try
                {
                    try { var result = await task.ConfigureAwait(false); disposeLate?.Invoke(result); }
                    catch { }
                    await cancel.ConfigureAwait(false);
                }
                finally { try { operation.Close(); } catch { } }
            }
        }
    }

    // Retain handles until their in-flight operation settles. One cleanup owner,
    // no per-handle retry threads and no disposal of objects still used by native I/O.
    internal Task Retire(Action release)
    {
        lock (_sync)
        {
            _retired.Enqueue(release);
            if (!_cleanupRunning) { _cleanupRunning = true; _cleanup = Task.Run(CleanRetired); }
            return _cleanup;
        }
    }

    private async Task CleanRetired()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task pending;
                lock (_sync) pending = _pending;
                await pending.ConfigureAwait(false);
                Action[] releases;
                lock (_sync)
                {
                    if (_retired.Count == 0) { _cleanupRunning = false; return; }
                    releases = _retired.ToArray();
                    _retired.Clear();
                }
                foreach (var release in releases) { try { release(); } catch { } }
            }
        }
        finally { _gate.Release(); }
    }

    internal ValueTask DisposeAsync(Func<Task> stop)
    {
        lock (_sync)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            _completion = Task.Run(async () =>
            {
                // Stop event producers immediately, independently of a poisoned I/O owner.
                var stopping = Task.Run(stop);
                await _shutdown.CancelAsync().ConfigureAwait(false);
                Task[] calls;
                lock (_sync) calls = _calls.ToArray();
                try { await Task.WhenAll(calls).ConfigureAwait(false); } catch { }
                Task pending;
                lock (_sync) pending = _pending;
                await pending.ConfigureAwait(false);
                await stopping.ConfigureAwait(false);
                while (true)
                {
                    Task cleanup;
                    lock (_sync) cleanup = _cleanup;
                    await cleanup.ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (ReferenceEquals(cleanup, _cleanup) && !_cleanupRunning && _retired.Count == 0) break;
                        if (!_cleanupRunning)
                        {
                            _cleanupRunning = true;
                            _cleanup = Task.Run(CleanRetired);
                        }
                    }
                }
                _shutdown.Dispose();
                _gate.Dispose();
            });
            _ = _completion.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            _dispose = BoundShutdown(_completion);
            return new(_dispose);
        }
    }

    private async Task BoundShutdown(Task completion)
    {
        try { await completion.WaitAsync(_shutdownBudget).ConfigureAwait(false); }
        catch (TimeoutException) { } // The single retained completion owns all late cleanup.
    }
}
