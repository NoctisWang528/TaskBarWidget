using System.IO;
using System.Text.Json;

namespace TaskBarWidgets.Media.Peripherals;

// One owner of device state and polling. Consumers read a published immutable
// snapshot; no hardware work is performed by a settings or card request.
internal sealed class PeripheralBatteryService : IAsyncDisposable
{
    private readonly IPeripheralBatteryProvider[] _providers;
    private readonly Dictionary<string, PeripheralDeviceSnapshot> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, int Failures)> _attempts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _taskbar = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _cachePath;
    private readonly TimeProvider _clock;
    private readonly List<(IPeripheralBatteryNotifications Provider, Action<PeripheralDeviceSnapshot> Handler)> _notifications = [];
    private Task? _run;
    private PeripheralBatterySettings _settings = new();
    private PeripheralBatterySnapshot _snapshot = new();
    private bool _cardEnabled, _cardVisible, _resident, _settingsActive;
    private bool _discoverPending = true, _refreshRequested, _disposed;
    private DateTimeOffset _nextDiscovery, _nextCacheWrite;
    private bool _cacheDirty;
    private CancellationTokenSource? _cycleStop;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal PeripheralBatteryService(IEnumerable<IPeripheralBatteryProvider> providers, string? cachePath,
        TimeProvider? clock = null)
    {
        _providers = providers.ToArray(); _cachePath = cachePath; _clock = clock ?? TimeProvider.System;
        foreach (var provider in _providers) provider.DevicesChanged += OnDevicesChanged;
        for (var index = 0; index < _providers.Length; index++)
        {
            if (_providers[index] is not IPeripheralBatteryNotifications notifications) continue;
            var owner = index;
            void ChangedReading(PeripheralDeviceSnapshot reading)
            {
                lock (_gate)
                {
                    if (_disposed || !HasDemandLocked || _owners.GetValueOrDefault(reading.Id, -1) != owner || !WantedLocked(reading)) return;
                    MergeLocked(reading, owner, false);
                    if (reading.State == "online" && !reading.Stale && HasReading(reading)) _attempts[reading.Id] = (_clock.GetUtcNow(), 0);
                    PublishLocked(Snapshot.Refreshing, Snapshot.Error);
                }
            }
            notifications.ReadingChanged += ChangedReading;
            _notifications.Add((notifications, ChangedReading));
        }
    }

    internal event Action? Changed;
    internal PeripheralBatterySnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal void Start() { lock (_gate) { if (!_disposed) _run ??= Task.Run(RunAsync); } }

    internal void Configure(PeripheralBatterySettings settings, bool cardEnabled, bool visible, bool resident)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_settings.HeadsetControlEnabled != settings.HeadsetControlEnabled ||
                _settings.VendorExtensionsEnabled != settings.VendorExtensionsEnabled ||
                _settings.AirPodsAdvertisementEnabled != settings.AirPodsAdvertisementEnabled) _discoverPending = true;
            _settings = settings.Clone(); _settings.Normalize();
            foreach (var extension in _providers.OfType<HeadsetControlProvider>()) extension.Configure(_settings);
            foreach (var native in _providers.OfType<NativePeripheralBatteryProvider>()) native.Configure(_settings);
            foreach (var airpods in _providers.OfType<AirPodsAdvertisementProvider>()) airpods.Configure(_settings);
            _cardEnabled = cardEnabled; _cardVisible = visible; _resident = resident;
            if (!HasDemandLocked) _cycleStop?.Cancel();
            PublishLocked(Snapshot.Refreshing, Snapshot.Error);
        }
        Signal();
    }

    internal void SetSettingsActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (active && !_settingsActive) { _discoverPending = true; _refreshRequested = true; }
            _settingsActive = active;
            if (!HasDemandLocked) _cycleStop?.Cancel();
        }
        Signal();
    }

    internal void SetTaskbarDemand(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var selected = ids.Where(PeripheralIdentity.IsValid).Take(256).ToHashSet(StringComparer.Ordinal);
            if (_taskbar.SetEquals(selected)) return;
            _taskbar.Clear(); _taskbar.UnionWith(selected);
            if (!HasDemandLocked) _cycleStop?.Cancel();
        }
        Signal();
    }

    internal void Refresh()
    {
        lock (_gate)
        {
            if (_disposed || Snapshot.Refreshing) return;
            _refreshRequested = true; _discoverPending = true;
        }
        Signal();
    }

    private void OnDevicesChanged()
    {
        lock (_gate) { if (_disposed) return; _discoverPending = true; }
        Signal();
    }

    private void Signal()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private bool HasDemandLocked => _settingsActive || _taskbar.Count != 0 || (_cardEnabled && (_cardVisible || _resident));
    private bool WantedLocked(PeripheralDeviceSnapshot item) => _settingsActive || _taskbar.Contains(item.Id) ||
        (_cardEnabled && (_cardVisible || _resident) && _settings.CardVisibility.GetValueOrDefault(item.Id, true));
    // A powered-off wireless peripheral can still be queried through its present
    // USB receiver. Do not reconnect Bluetooth devices just to read battery state.
    private static bool CanPoll(PeripheralDeviceSnapshot item) => item.CanRead &&
        (item.State != "disconnected" || item.Connection == "wireless");
    private TimeSpan IntervalLocked(string id) => _settingsActive || _taskbar.Contains(id) || _cardVisible
        ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5);

    private DateTimeOffset NextAttemptLocked(string id)
    {
        if (!_attempts.TryGetValue(id, out var attempt)) return DateTimeOffset.MinValue;
        var interval = IntervalLocked(id);
        if (attempt.Failures > 0) interval = TimeSpan.FromSeconds(Math.Max(interval.TotalSeconds,
            attempt.Failures switch { 1 => 60, 2 => 120, 3 => 300, _ => 900 }));
        return attempt.At + interval;
    }

    private async Task RunAsync()
    {
        try
        {
            await LoadCacheAsync(_stop.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                bool discover, force, demand;
                lock (_gate)
                {
                    demand = HasDemandLocked;
                    discover = demand && (_discoverPending || _clock.GetUtcNow() >= _nextDiscovery);
                    force = demand && _refreshRequested;
                    if (discover) _discoverPending = false;
                    _refreshRequested = false;
                }
                if (demand)
                {
                    using var cycle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    lock (_gate) { _cycleStop = cycle; if (!HasDemandLocked) cycle.Cancel(); }
                    try { await RefreshCycleAsync(discover, force, cycle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested && cycle.IsCancellationRequested)
                    { lock (_gate) _discoverPending = true; }
                    finally { lock (_gate) { _cycleStop = null; PublishLocked(false, Snapshot.Error); } }
                }
                if (!demand)
                    foreach (var notifications in _providers.OfType<IPeripheralBatteryNotifications>())
                        await notifications.SetDemandAsync(new HashSet<string>(), _stop.Token).ConfigureAwait(false);
                for (var index = 0; index < _providers.Length; index++)
                {
                    if (_providers[index] is not IPeripheralBatteryIdle idle) continue;
                    bool active;
                    lock (_gate) active = HasDemandLocked && _devices.Values.Any(d => d.CanRead &&
                        _owners.GetValueOrDefault(d.Id, -1) == index && WantedLocked(d));
                    if (!active) await idle.SuspendAsync().ConfigureAwait(false);
                }
                await SaveCacheAsync(false, _stop.Token).ConfigureAwait(false);
                TimeSpan wait;
                lock (_gate)
                {
                    if (!HasDemandLocked) wait = Timeout.InfiniteTimeSpan;
                    else if (_discoverPending || _refreshRequested) wait = TimeSpan.Zero;
                    else
                    {
                        var next = _nextDiscovery;
                        foreach (var item in _devices.Values.Where(d => CanPoll(d) && WantedLocked(d)))
                            if (NextAttemptLocked(item.Id) < next) next = NextAttemptLocked(item.Id);
                        wait = next - _clock.GetUtcNow();
                        if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
                    }
                }
                await _wake.WaitAsync(wait, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task RefreshCycleAsync(bool discover, bool force, CancellationToken token)
    {
        var errors = new List<string>();
        if (discover)
        {
            lock (_gate) PublishLocked(true, "");
            for (var index = 0; index < _providers.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var found = await _providers[index].DiscoverAsync(token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        found = found.Select(RetainLegacySelectionLocked).ToArray();
                        var present = found.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                        foreach (var id in _owners.Where(p => p.Value == index && !present.Contains(p.Key)).Select(p => p.Key).ToArray())
                            _devices[id] = _devices[id] with { State = "disconnected", Stale = true, CanRead = false };
                        foreach (var item in found.OrderBy(d => d.State == "disconnected").Take(256)) MergeLocked(item, index, discovery: true);
                        PublishLocked(true, "");
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                { errors.Add("部分设备发现失败（" + error.GetType().Name + "）"); }
            }
            lock (_gate) _nextDiscovery = _clock.GetUtcNow().AddMinutes(15);
        }
        for (var index = 0; index < _providers.Length; index++)
        {
            HashSet<string> ids, wanted; PeripheralBatterySettings settings;
            lock (_gate)
            {
                if (!HasDemandLocked) break;
                var now = _clock.GetUtcNow();
                wanted = _devices.Values.Where(d => _owners.GetValueOrDefault(d.Id, -1) == index && CanPoll(d) &&
                    WantedLocked(d)).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                ids = _devices.Values.Where(d => _owners.GetValueOrDefault(d.Id, -1) == index && CanPoll(d) &&
                    WantedLocked(d) && (force || NextAttemptLocked(d.Id) <= now))
                    .Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                settings = _settings.Clone();
                if (ids.Count != 0) PublishLocked(true, "");
            }
            if (_providers[index] is IPeripheralBatteryNotifications notifications)
                await notifications.SetDemandAsync(wanted, token).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                if (wanted.Count == 0 && _providers[index] is IPeripheralBatteryIdle idle) await idle.SuspendAsync().ConfigureAwait(false);
                continue;
            }
            IReadOnlyList<PeripheralDeviceSnapshot> results = [];
            var readStarted = _clock.GetUtcNow();
            try { results = await _providers[index].ReadAsync(ids, settings, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { errors.Add("部分设备电量暂不可读（" + error.GetType().Name + "）"); }
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var returned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in results.Take(256))
                {
                    if (!ids.Contains(item.Id)) continue;
                    returned.Add(item.Id);
                    if (_devices.TryGetValue(item.Id, out var notified) && notified is { State: "online", Stale: false } &&
                        notified.UpdatedAt > readStarted && (item.UpdatedAt is null || item.UpdatedAt < notified.UpdatedAt)) continue;
                    MergeLocked(item, index, discovery: false);
                }
                foreach (var id in ids)
                {
                    var result = results.FirstOrDefault(d => d.Id == id);
                    var notified = _devices.GetValueOrDefault(id);
                    var newerNotification = notified is { State: "online", Stale: false } && notified.UpdatedAt > readStarted;
                    var succeeded = newerNotification || result is { State: "online", Stale: false } && HasReading(result) ||
                        result is { State: "disconnected", Connection: "wireless", CanRead: true };
                    var failures = succeeded ? 0 : Math.Min(4, _attempts.GetValueOrDefault(id).Failures + 1);
                    _attempts[id] = (_clock.GetUtcNow(), failures);
                    if (!newerNotification && !returned.Contains(id) && _devices.TryGetValue(id, out var existing))
                        _devices[id] = existing with { Stale = true, State = "unavailable" };
                }
                PublishLocked(true, "");
            }
        }
        lock (_gate) PublishLocked(false, string.Join("；", errors.Distinct()));
    }

    internal static bool HasReading(PeripheralDeviceSnapshot item) => item.Batteries.Any(b =>
        b.Percentage is >= 0 and <= 100 || !string.IsNullOrWhiteSpace(b.LevelText));

    private PeripheralDeviceSnapshot RetainLegacySelectionLocked(PeripheralDeviceSnapshot incoming)
    {
        var aliases = incoming.LegacyIds.Where(PeripheralIdentity.IsValid).Where(id => id != incoming.Id).Distinct().ToArray();
        if (aliases.Length == 0) return incoming;
        var linked = new[] { incoming.Id }.Concat(aliases).ToArray();
        var retained = linked.FirstOrDefault(id => _taskbar.Contains(id) || _settings.CardVisibility.ContainsKey(id) || _settings.Order.Contains(id))
            ?? aliases.FirstOrDefault(_devices.ContainsKey) ?? incoming.Id;
        if (!_devices.ContainsKey(retained))
        {
            var previous = linked.Select(id => _devices.GetValueOrDefault(id)).FirstOrDefault(d => d is not null);
            if (previous is not null) _devices[retained] = previous with { Id = retained };
        }
        foreach (var redundant in linked.Where(id => id != retained))
        {
            _devices.Remove(redundant); _owners.Remove(redundant); _attempts.Remove(redundant);
        }
        return incoming with { Id = retained };
    }

    private void MergeLocked(PeripheralDeviceSnapshot incoming, int provider, bool discovery)
    {
        if (!PeripheralIdentity.IsValid(incoming.Id) || string.IsNullOrWhiteSpace(incoming.Name)) return;
        if (_devices.Count >= 256 && !_devices.ContainsKey(incoming.Id))
        {
            var unused = _devices.Values.Where(d => d.State == "disconnected" && !_taskbar.Contains(d.Id) &&
                !_settings.Order.Contains(d.Id) && !_settings.CardVisibility.ContainsKey(d.Id))
                .OrderBy(d => d.UpdatedAt).FirstOrDefault();
            if (unused is null) return;
            _devices.Remove(unused.Id); _owners.Remove(unused.Id); _attempts.Remove(unused.Id);
        }
        if (_owners.TryGetValue(incoming.Id, out var owner) && owner != provider &&
            _devices.TryGetValue(incoming.Id, out var readable) && readable.CanRead && readable.State != "disconnected" && !incoming.CanRead) return;
        incoming = incoming with { Batteries = incoming.Batteries.Where(b =>
            b.Percentage is null or >= 0 and <= 100).Take(8).ToArray() };
        if (_devices.TryGetValue(incoming.Id, out var newer) && incoming.UpdatedAt.HasValue &&
            newer.UpdatedAt > incoming.UpdatedAt && HasReading(incoming)) return;
        if (_devices.TryGetValue(incoming.Id, out var existing) && !HasReading(incoming))
        {
            var batteries = existing.Batteries.Select(old =>
                incoming.Batteries.FirstOrDefault(value => value.Role == old.Role && value.Charging.HasValue) is { } status
                    ? old with { Charging = status.Charging } : old).ToArray();
            incoming = incoming with { Batteries = batteries, UpdatedAt = existing.UpdatedAt,
                Stale = !discovery || incoming.State != "online" || existing.Stale };
        }
        // A sleeping HID++ child can be rediscovered without optional name metadata.
        // The stable child ID was already proven by the provider; retain its real name.
        if (existing is { Source: "hidpp20" } && incoming.Source == "hidpp20" &&
            incoming.Name is "Logitech HID++ mouse" or "Logitech HID++ keyboard" or
                "Logitech HID++ device" or "Logitech HID++ receiver")
            incoming = incoming with { Name = existing.Name };
        if (existing is { State: "disconnected" } && incoming.State != "disconnected") _attempts.Remove(incoming.Id);
        _devices[incoming.Id] = incoming; _owners[incoming.Id] = provider;
        _cacheDirty = true;
    }

    private void PublishLocked(bool refreshing, string error)
    {
        if (_disposed) return;
        var rank = _settings.Order.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);
        // Older caches included every HID collection. Keep configured positions,
        // but expose only devices with a battery source or an explicit selection.
        var devices = _devices.Values.Where(d => d.CanRead || HasReading(d) || d.Compatibility == "extension" ||
            _taskbar.Contains(d.Id) || _settings.CardVisibility.GetValueOrDefault(d.Id)).Select(d => d with
        {
            Brand = d.Brand.Length > 0 ? d.Brand : PeripheralIdentity.Brand(d.Name, source: d.Source),
            ShowInCard = _settings.CardVisibility.TryGetValue(d.Id, out var visible) ? visible : HasReading(d)
        }).OrderBy(d => rank.GetValueOrDefault(d.Id, int.MaxValue)).ThenBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
        var next = new PeripheralBatterySnapshot { Mode = _settings.Mode, HideDisconnected = _settings.HideDisconnected,
            Refreshing = refreshing, Error = error, Devices = devices };
        var previous = Snapshot;
        if (JsonSerializer.Serialize(next, Json) == JsonSerializer.Serialize(previous with { Revision = 0 }, Json)) return;
        Volatile.Write(ref _snapshot, next with { Revision = previous.Revision + 1 });
        Changed?.Invoke();
    }

    private async Task LoadCacheAsync(CancellationToken token)
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return;
        try
        {
            if (new FileInfo(_cachePath).Length > 1024 * 1024) return;
            var items = JsonSerializer.Deserialize<PeripheralDeviceSnapshot[]>(await File.ReadAllTextAsync(_cachePath, token), Json) ?? [];
            lock (_gate)
            {
                foreach (var item in items.Where(d => d is not null && d.Batteries is not null && d.Batteries.Length <= 16 &&
                    d.Batteries.All(b => b is not null && b.Role is not null && b.Role.Length <= 64 &&
                        (b.Percentage is null or >= 0 and <= 100) && (b.LevelText?.Length ?? 0) <= 128)).Take(256))
                    if (PeripheralIdentity.IsValid(item.Id)) _devices[item.Id] = item with { State = "disconnected", Stale = true };
                PublishLocked(false, "");
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private async Task SaveCacheAsync(bool force, CancellationToken token)
    {
        if (_cachePath is null) return;
        PeripheralDeviceSnapshot[] items;
        lock (_gate)
        {
            if (!_cacheDirty || (!force && _clock.GetUtcNow() < _nextCacheWrite)) return;
            items = _devices.Values.ToArray(); _cacheDirty = false;
            _nextCacheWrite = _clock.GetUtcNow().AddMinutes(5);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var pending = _cachePath + ".tmp";
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(items, Json), token).ConfigureAwait(false);
            File.Move(pending, _cachePath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { lock (_gate) _cacheDirty = true; }
        catch (OperationCanceledException)
        { lock (_gate) _cacheDirty = true; throw; }
    }

    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_gate) { if (_disposed) return; _disposed = true; run = _run; }
        foreach (var provider in _providers) provider.DevicesChanged -= OnDevicesChanged;
        foreach (var (provider, handler) in _notifications) provider.ReadingChanged -= handler;
        _stop.Cancel(); Signal();
        try { if (run is not null) await run.ConfigureAwait(false); }
        finally
        {
            foreach (var provider in _providers)
                try { await provider.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await SaveCacheAsync(true, CancellationToken.None).ConfigureAwait(false);
            _stop.Dispose();
        }
        // A device watcher callback may already have passed its disposed check.
        // SemaphoreSlim owns no OS wait handle here; leaving it avoids a late release race.
    }
}
