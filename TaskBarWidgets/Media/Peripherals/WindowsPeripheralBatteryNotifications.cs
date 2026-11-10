namespace TaskBarWidgets.Media.Peripherals;

internal sealed partial class WindowsPeripheralBatteryProvider
{
    private readonly object _notificationSync = new();
    private readonly Dictionary<string, BasState> _leases = new(StringComparer.Ordinal);
    private HashSet<string> _demand = new(StringComparer.Ordinal);
    private readonly Func<WindowsPeripheralNode, WindowsPeripheralLifetime, CancellationToken, Task<IWindowsBasBatteryLease?>> _openBas;
    public event Action<PeripheralDeviceSnapshot>? ReadingChanged;
    internal Task CleanupCompletion => _lifetime.Completion;

    private sealed class BasState(string id, string path, PeripheralDeviceSnapshot snapshot)
    {
        internal readonly string Id = id, Path = path;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly Dictionary<string, PeripheralBatteryReading> Values = new(StringComparer.Ordinal);
        internal PeripheralDeviceSnapshot Snapshot = snapshot;
        internal IWindowsBasBatteryLease? Lease;
        internal bool Active = true;
        internal int Retired;
        internal Task Cancellation = Task.CompletedTask;
        internal void Release()
        {
            try
            {
                try { Cancellation.GetAwaiter().GetResult(); } catch { }
                Lease?.Dispose();
            }
            finally { Lease = null; Stop.Dispose(); }
        }
    }

    public Task SetDemandAsync(IReadOnlySet<string> deviceIds, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var wanted = deviceIds.Where(PeripheralIdentity.IsValid).Take(256).ToHashSet(StringComparer.Ordinal);
        BasState[] removed;
        lock (_notificationSync)
        {
            _demand = wanted;
            removed = _leases.Values.Where(s => !wanted.Contains(s.Id)).ToArray();
            foreach (var state in removed) { state.Active = false; _leases.Remove(state.Id); }
        }
        foreach (var state in removed) Retire(state);
        // Empty demand must detach immediately, even if a native cancellation is quarantined.
        if (wanted.Count == 0) return Task.CompletedTask;
        return Setup();

        async Task Setup()
        {
            try { await ReconcileAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (Recoverable(ex) || ex is TimeoutException) { }
        }

        Task<bool> ReconcileAsync() => _lifetime.Run(async token =>
        {
            var nodes = await _catalog.GetAsync(token).ConfigureAwait(false);
            ReconcileLeases(nodes);
            var groups = Groups(nodes);
            foreach (var id in wanted)
            {
                token.ThrowIfCancellationRequested();
                if (!groups.TryGetValue(id, out var group)) continue;
                var node = group.FirstOrDefault(n => n.BluetoothLE && n.Connected == true && n.Present);
                if (node is null || !_known.TryGetValue(id, out var known)) continue;
                BasState state;
                lock (_notificationSync)
                {
                    if (_disposed || !_demand.Contains(id) || _leases.ContainsKey(id)) continue;
                    state = new(id, node.Path, known with { Source = "bluetooth-bas", Compatibility = "implemented", State = "online" });
                    _leases.Add(id, state);
                }
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, state.Stop.Token);
                    var lease = await _openBas(node, _lifetime, linked.Token).ConfigureAwait(false);
                    lock (_notificationSync)
                    {
                        state.Lease = lease; // The retirement owner also owns a late factory result.
                        if (lease is not null)
                            foreach (var key in lease.BatteryKeys) state.Values.TryAdd(key, new());
                    }
                    if (lease is null || !IsCurrent(state)) { Remove(state); continue; }
                    lease.BatteryChanged += (key, percentage) => Accept(state, key, percentage, true);
                    lease.Invalidated += () => Invalidated(state);
                    if (!IsCurrent(state)) { Remove(state); continue; }
                    await lease.SubscribeAsync(linked.Token).ConfigureAwait(false);
                    if (!IsCurrent(state) || !lease.Connected) Remove(state);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { Remove(state); }
                catch (Exception ex) when (Recoverable(ex)) { Remove(state); }
                catch { Remove(state); throw; }
            }
            return true;
        }, cancellationToken);
    }

    private bool IsCurrent(BasState state)
    {
        lock (_notificationSync) return !_disposed && state.Active && _demand.Contains(state.Id) &&
            _leases.TryGetValue(state.Id, out var current) && ReferenceEquals(current, state);
    }

    private void Remove(BasState state)
    {
        lock (_notificationSync)
        {
            state.Active = false;
            if (_leases.TryGetValue(state.Id, out var current) && ReferenceEquals(current, state)) _leases.Remove(state.Id);
        }
        Retire(state);
    }

    private void Retire(BasState state)
    {
        if (Interlocked.Exchange(ref state.Retired, 1) != 0) return;
        state.Lease?.Deactivate();
        // Only managed token registrations execute here; native Cancel is independently retained.
        state.Cancellation = state.Stop.CancelAsync();
        _ = _lifetime.Retire(state.Release);
    }

    private void ReconcileLeases(IReadOnlyList<WindowsPeripheralNode> nodes)
    {
        var paths = nodes.Where(n => n.BluetoothLE && n.Connected == true && n.Present).Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        BasState[] removed;
        lock (_notificationSync)
        {
            removed = _leases.Values.Where(s => !paths.Contains(s.Path)).ToArray();
            foreach (var state in removed) { state.Active = false; _leases.Remove(state.Id); }
        }
        foreach (var state in removed)
        {
            Retire(state);
            Emit(state.Snapshot with { State = "disconnected", Stale = state.Snapshot.UpdatedAt.HasValue });
        }
    }

    private void Invalidated(BasState state)
    {
        if (!IsCurrent(state)) return;
        var connected = state.Lease?.Connected == true;
        Remove(state);
        Emit(state.Snapshot with { State = connected ? "unavailable" : "disconnected", Stale = state.Snapshot.UpdatedAt.HasValue });
        Changed();
    }

    private void Accept(BasState state, string key, int percentage, bool publish)
    {
        if (percentage is < 0 or > 100) return;
        PeripheralDeviceSnapshot snapshot;
        lock (_notificationSync)
        {
            if (!IsCurrent(state) || state.Lease?.Connected != true || !state.Values.ContainsKey(key)) return;
            state.Values[key] = new() { Percentage = percentage };
            snapshot = state.Snapshot = state.Snapshot with
            {
                Batteries = WindowsPeripheralIdentity.Roles(state.Values.Select(p => (p.Key, p.Value))),
                UpdatedAt = DateTimeOffset.UtcNow, Stale = false, State = "online", Source = "bluetooth-bas", CanRead = true
            };
        }
        if (publish) Emit(snapshot);
    }

    private void Emit(PeripheralDeviceSnapshot snapshot)
    {
        if (_disposed) return;
        try { ReadingChanged?.Invoke(snapshot); } catch { }
    }

    private async Task<PeripheralDeviceSnapshot?> ReadLease(string id, CancellationToken token)
    {
        BasState? state;
        lock (_notificationSync) _leases.TryGetValue(id, out state);
        if (state?.Lease is null || !IsCurrent(state) || !state.Lease.Connected) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, state.Stop.Token);
        if (state.Lease.HasFallback)
        {
            var readings = await state.Lease.ReadFallbackAsync(linked.Token).ConfigureAwait(false);
            foreach (var (key, reading) in readings)
                if (reading.Percentage.HasValue) Accept(state, key, reading.Percentage.Value, false);
        }
        lock (_notificationSync)
            return IsCurrent(state) && state.Snapshot.Batteries.Any(b => b.Percentage.HasValue) ? state.Snapshot : null;
    }

    private void StopNotifications()
    {
        BasState[] states;
        lock (_notificationSync)
        {
            _demand.Clear();
            states = _leases.Values.ToArray();
            foreach (var state in states) state.Active = false;
            _leases.Clear();
        }
        foreach (var state in states) Retire(state);
    }
}
