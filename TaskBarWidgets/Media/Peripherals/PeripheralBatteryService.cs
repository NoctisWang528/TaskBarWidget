using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Endpoint = TaskBarWidgets.Media.Peripherals.PeripheralIdentityResolver.Endpoint;

namespace TaskBarWidgets.Media.Peripherals;

// One owner of device state and polling. Consumers read a published immutable
// snapshot; no hardware work is performed by a settings or card request.
internal sealed class PeripheralBatteryService : IAsyncDisposable
{
    private readonly IPeripheralBatteryProvider[] _providers;
    private readonly Dictionary<string, PeripheralDeviceSnapshot> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<Endpoint, PeripheralDeviceSnapshot> _endpoints = [];
    private Dictionary<Endpoint, string> _routes = [];
    private readonly Dictionary<Endpoint, (DateTimeOffset At, int Failures)> _attempts = [];
    private readonly Dictionary<Endpoint, long> _notificationVersions = [];
    private long _notificationVersion;
    private readonly PeripheralIdentityResolver _resolver = new();
    private readonly Dictionary<string, PeripheralIdentityEvidence[]> _identities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _historical = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, string Role), CachedReading> _readings = [];
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
                    if (!ValidDevice(reading)) return;
                    var endpoint = new Endpoint(owner, reading.Id);
                    if (_disposed || !HasDemandLocked || !_routes.TryGetValue(endpoint, out var id) || !WantedLocked(id)) return;
                    MergeEndpointLocked(reading, endpoint, false);
                    _notificationVersions[endpoint] = ++_notificationVersion;
                    if (reading.State == "online" && !reading.Stale && HasReading(reading)) _attempts[endpoint] = (_clock.GetUtcNow(), 0);
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

    public string ResolveId(string id) { lock (_gate) return _resolver.ResolveId(id); }
    public PeripheralBatterySettings NormalizeSettings(PeripheralBatterySettings settings)
    { lock (_gate) return _resolver.NormalizeSettings(settings); }

    public string ExportDiagnostics(byte[]? reportSalt = null)
    {
        lock (_gate)
        {
            var salt = reportSalt ?? RandomNumberGenerator.GetBytes(32);
            string Hash(string value) => string.IsNullOrWhiteSpace(value) ? "" : "redacted-" +
                Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(value.ToLowerInvariant()))).ToLowerInvariant()[..16];
            object Evidence(PeripheralIdentityEvidence evidence) => new
            {
                containerId = Hash(evidence.ContainerId), bluetoothAddress = Hash(evidence.BluetoothAddress),
                uniqueSerial = Hash(evidence.UniqueSerial), serialScope = Hash(evidence.SerialScope),
                unitId = Hash(evidence.UnitId), unitScope = Hash(evidence.UnitScope), endpointPath = Hash(evidence.EndpointPath),
                evidence.ContainerIsDevice
            };
            return JsonSerializer.Serialize(new
            {
                version = 2, aliases = _resolver.Aliases.Select(p => new { from = Hash(p.Key), to = Hash(p.Value) }),
                decisions = _resolver.Decisions.Select(d => new { left = Hash(d.LeftId), right = Hash(d.RightId), d.Reason, d.Merged }),
                devices = _devices.Values.Select(d => new
                {
                    id = Hash(d.Id), name = Hash(d.Name), d.Kind, d.Connection, d.State, d.Source,
                    d.Compatibility, d.CanRead, d.Stale, d.UpdatedAt,
                    roles = d.Batteries.Select(b => b.Role),
                    evidence = _identities.GetValueOrDefault(d.Id, []).Select(Evidence),
                    endpoints = _routes.Where(p => p.Value == d.Id).Select(p => new
                    { provider = _providers[p.Key.Provider].GetType().Name, id = Hash(p.Key.Id),
                        _endpoints[p.Key].CanRead, _endpoints[p.Key].Source, _endpoints[p.Key].Compatibility })
                })
            }, Json);
        }
    }

    internal void Configure(PeripheralBatterySettings settings, bool cardEnabled, bool visible, bool resident)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_settings.HeadsetControlEnabled != settings.HeadsetControlEnabled ||
                _settings.VendorExtensionsEnabled != settings.VendorExtensionsEnabled ||
                _settings.AirPodsAdvertisementEnabled != settings.AirPodsAdvertisementEnabled) _discoverPending = true;
            _settings = _resolver.NormalizeSettings(settings);
            foreach (var extension in _providers.OfType<HeadsetControlProvider>()) extension.Configure(_settings);
            foreach (var native in _providers.OfType<NativePeripheralBatteryProvider>()) native.Configure(_settings);
            foreach (var hid in _providers.OfType<HidPeripheralBatteryProvider>()) hid.Configure(_settings);
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
            var selected = ids.Where(PeripheralIdentity.IsValid).Take(256).Select(_resolver.ResolveId).ToHashSet(StringComparer.Ordinal);
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
    private bool WantedLocked(string id) => _settingsActive || _taskbar.Contains(id) ||
        (_cardEnabled && (_cardVisible || _resident) && _settings.CardVisibility.GetValueOrDefault(id, true));
    // A powered-off wireless peripheral can still be queried through its present
    // USB receiver. Do not reconnect Bluetooth devices just to read battery state.
    private static bool CanPoll(PeripheralDeviceSnapshot item) => item.CanRead &&
        (item.State != "disconnected" || item.Connection == "wireless");
    private TimeSpan IntervalLocked(string id) => _settingsActive || _taskbar.Contains(id) || _cardVisible
        ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5);

    private DateTimeOffset NextAttemptLocked(Endpoint endpoint)
    {
        if (!_attempts.TryGetValue(endpoint, out var attempt)) return DateTimeOffset.MinValue;
        var interval = IntervalLocked(_routes.GetValueOrDefault(endpoint, endpoint.Id));
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
                    lock (_gate) active = HasDemandLocked && _endpoints.Any(p => p.Key.Provider == index && p.Value.CanRead &&
                        _routes.TryGetValue(p.Key, out var id) && WantedLocked(id));
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
                        foreach (var endpoint in _endpoints.Keys.Where(e => CanPoll(_endpoints[e]) &&
                            _routes.TryGetValue(e, out var id) && WantedLocked(id)))
                            if (NextAttemptLocked(endpoint) < next) next = NextAttemptLocked(endpoint);
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
            var batch = new Dictionary<int, PeripheralDeviceSnapshot[]>();
            var discoveryVersions = new Dictionary<int, long>();
            var retiredBatch = new Dictionary<int, string[]>();
            for (var index = 0; index < _providers.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    lock (_gate) discoveryVersions[index] = _notificationVersion;
                    var found = await _providers[index].DiscoverAsync(token).ConfigureAwait(false);
                    batch[index] = found.Where(ValidDevice).OrderBy(d => d.State == "disconnected").Take(256).ToArray();
                    if (_providers[index] is IPeripheralBatteryRetirements retirements)
                        retiredBatch[index] = retirements.RetiredDeviceIds.Where(PeripheralIdentity.IsValid).Take(256).ToArray();
                }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                { errors.Add("部分设备发现失败（" + error.GetType().Name + "）"); }
            }
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var protectedEndpoints = new HashSet<Endpoint>();
                foreach (var (provider, found) in batch)
                {
                    var present = found.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                    foreach (var endpoint in _endpoints.Keys.Where(e => e.Provider == provider && !present.Contains(e.Id)).ToArray())
                    {
                        if (_notificationVersions.GetValueOrDefault(endpoint) > discoveryVersions[provider]) continue;
                        _endpoints.Remove(endpoint);
                        _attempts.Remove(endpoint);
                        _notificationVersions.Remove(endpoint);
                    }
                    foreach (var item in found)
                    {
                        var endpoint = new Endpoint(provider, item.Id);
                        var preserveMetadata = _notificationVersions.GetValueOrDefault(endpoint) > discoveryVersions[provider];
                        if (preserveMetadata) protectedEndpoints.Add(endpoint);
                        if (_endpoints.GetValueOrDefault(endpoint) is { State: "disconnected" } && item.State != "disconnected")
                            _attempts.Remove(endpoint);
                        // Retain endpoint samples until identity resolution has seen
                        // every provider. A failed provider keeps its last endpoints.
                        _endpoints[endpoint] = RetainEndpointSample(_endpoints.GetValueOrDefault(endpoint), item, true, preserveMetadata);
                    }
                }
                foreach (var (provider, retiredIds) in retiredBatch)
                    foreach (var retired in retiredIds)
                    {
                        // Retire only the disproven endpoint, never a proven alias
                        // belonging to another active device/provider.
                        if (batch[provider].Any(d => d.Id == retired) || _resolver.ResolveId(retired) != retired ||
                            _endpoints.Keys.Any(e => e.Id == retired || _routes.GetValueOrDefault(e) == retired) ||
                            !_devices.TryGetValue(retired, out var cached)) continue;
                        if (HasReading(cached) || cached.UpdatedAt.HasValue)
                            _devices[retired] = cached with { State = "disconnected", Stale = true, CanRead = false };
                        else
                        {
                            _devices.Remove(retired); _identities.Remove(retired); _historical.Remove(retired);
                            foreach (var key in _readings.Keys.Where(k => k.Id == retired).ToArray()) _readings.Remove(key);
                        }
                        _cacheDirty = true;
                    }
                ResolveBatchLocked();
                foreach (var (provider, found) in batch)
                    foreach (var item in found)
                    {
                        var endpoint = new Endpoint(provider, item.Id);
                        MergeEndpointLocked(item, endpoint, true, protectedEndpoints.Contains(endpoint));
                    }
                foreach (var id in _devices.Keys.ToArray()) RebuildEntityLocked(id);
                TrimStateLocked();
                _nextDiscovery = _clock.GetUtcNow().AddMinutes(15);
                PublishLocked(true, "");
            }
        }
        for (var index = 0; index < _providers.Length; index++)
        {
            HashSet<string> ids, wanted; PeripheralBatterySettings settings; long readVersion;
            lock (_gate)
            {
                if (!HasDemandLocked) break;
                var now = _clock.GetUtcNow();
                var endpoints = _endpoints.Where(p => p.Key.Provider == index && CanPoll(p.Value) &&
                    _routes.TryGetValue(p.Key, out var id) && WantedLocked(id)).Select(p => p.Key).ToArray();
                wanted = endpoints.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                ids = endpoints.Where(e => force || NextAttemptLocked(e) <= now).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                settings = _settings.Clone();
                readVersion = _notificationVersion;
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
            try { results = await _providers[index].ReadAsync(ids, settings, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { errors.Add("部分设备电量暂不可读（" + error.GetType().Name + "）"); }
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var returned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in results.Where(ValidDevice).Take(256))
                {
                    if (!ids.Contains(item.Id)) continue;
                    var endpoint = new Endpoint(index, item.Id);
                    returned.Add(item.Id);
                    MergeEndpointLocked(item, endpoint, false, _notificationVersions.GetValueOrDefault(endpoint) > readVersion);
                }
                foreach (var id in ids)
                {
                    var endpoint = new Endpoint(index, id);
                    var result = results.FirstOrDefault(d => d.Id == id);
                    var notified = _endpoints.GetValueOrDefault(endpoint);
                    var notificationDuringRead = _notificationVersions.GetValueOrDefault(endpoint) > readVersion;
                    var newerNotification = notified is { State: "online", Stale: false } &&
                        notificationDuringRead;
                    var succeeded = newerNotification || result is { State: "online", Stale: false } && HasReading(result) ||
                        result is { State: "disconnected", Connection: "wireless", CanRead: true } ||
                        result is { Source: "rog_omni_keyboard", ModelIdentityConfirmed: true, ModelIdentity: not null };
                    var failures = succeeded ? 0 : Math.Min(4, _attempts.GetValueOrDefault(endpoint).Failures + 1);
                    _attempts[endpoint] = (_clock.GetUtcNow(), failures);
                    if (!notificationDuringRead && !returned.Contains(id) && notified is not null)
                    {
                        _endpoints[endpoint] = notified with { Stale = true, State = "unavailable" };
                        if (_routes.TryGetValue(endpoint, out var entity)) RebuildEntityLocked(entity);
                    }
                }
                PublishLocked(true, "");
            }
        }
        lock (_gate) PublishLocked(false, string.Join("；", errors.Distinct()));
    }

    internal static bool HasReading(PeripheralDeviceSnapshot item) => item.Batteries.Any(b =>
        b.Percentage is >= 0 and <= 100 || !string.IsNullOrWhiteSpace(b.LevelText));

    private static bool ValidDevice(PeripheralDeviceSnapshot item) => item is not null &&
        PeripheralIdentity.IsValid(item.Id) && !string.IsNullOrWhiteSpace(item.Name) && item.Name.Length <= 512 &&
        item.Source is not null && item.Kind is not null && item.Brand is not null && item.Connection is not null &&
        item.Batteries is { Length: <= 16 } && item.Batteries.All(b => b is not null &&
            b.Role is { Length: > 0 and <= 64 } && (b.Percentage is null or >= 0 and <= 100) && (b.LevelText?.Length ?? 0) <= 128);

    private static int SourcePriority(string source) => source switch
    {
        "" or "windows" => 0,
        "windows-property" or "windows-battery" or "ble-bas" or "bluetooth-bas" or "xinput" or "winrt-battery" or "battery-report" => 1,
        _ => source.StartsWith("windows", StringComparison.OrdinalIgnoreCase) ? 1 : 2
    };
    private static int NameQuality(PeripheralDeviceSnapshot item) => item.NameQuality != 0 ? item.NameQuality :
        item.Name.StartsWith("Logitech HID++ ", StringComparison.Ordinal) ||
        item.Name is "Peripheral" or "HID Keyboard Device" or "HID-compliant mouse" or "USB Input Device" ? 1 : 10;
    private static int KindQuality(PeripheralDeviceSnapshot item) => item.KindQuality != 0 ? item.KindQuality :
        item.Kind == "unknown" ? 0 : 1;

    private static bool OmniModelChanged(PeripheralDeviceSnapshot? existing, PeripheralDeviceSnapshot incoming) =>
        incoming.Source == "rog_omni_keyboard" && incoming.ModelIdentityConfirmed && incoming.ModelIdentity is not null &&
        existing?.ModelIdentity != incoming.ModelIdentity;

    private static PeripheralDeviceSnapshot RetainOmniModel(PeripheralDeviceSnapshot? existing,
        PeripheralDeviceSnapshot incoming, bool discovery)
    {
        if (existing is not null && incoming.Source == "rog_omni_keyboard" &&
            (existing.Source == incoming.Source || existing.ModelIdentity is not null) &&
            incoming.ModelIdentity is null && !incoming.ModelIdentityConfirmed && (discovery || !HasReading(incoming)))
            return incoming with { ModelIdentity = existing.ModelIdentity,
                Name = incoming.Name == "ROG Omni keyboard" ? existing.Name : incoming.Name,
                NameQuality = incoming.Name == "ROG Omni keyboard" ? existing.NameQuality : incoming.NameQuality };
        return incoming;
    }

    private static PeripheralDeviceSnapshot RetainEndpointSample(PeripheralDeviceSnapshot? existing,
        PeripheralDeviceSnapshot incoming, bool discovery, bool preserveMetadata = false)
    {
        if (existing is null) return incoming;
        incoming = RetainOmniModel(existing, incoming, discovery);
        var modelChanged = OmniModelChanged(existing, incoming);
        var older = incoming.UpdatedAt.HasValue && existing.UpdatedAt > incoming.UpdatedAt;
        preserveMetadata |= !discovery && older;
        if (modelChanged && (preserveMetadata || older)) return existing;
        if (existing.Identity is { } oldIdentity && incoming.Identity is { } newIdentity &&
            PeripheralIdentityResolver.Conflicts(oldIdentity, newIdentity)) return preserveMetadata ? existing : incoming;
        if ((!HasReading(incoming) || older) && !modelChanged)
        {
            var batteries = existing.Batteries.Select(old =>
                !older && incoming.Batteries.FirstOrDefault(value => value.Role == old.Role && value.Charging.HasValue) is { } status
                    ? old with { Charging = status.Charging } : old).ToArray();
            incoming = incoming with { Batteries = batteries, UpdatedAt = older ? existing.UpdatedAt : incoming.UpdatedAt ?? existing.UpdatedAt,
                Stale = older ? existing.Stale : !discovery || incoming.State != "online" || existing.Stale };
        }
        if (preserveMetadata)
            incoming = incoming with { State = existing.State, Stale = existing.Stale, CanRead = existing.CanRead,
                Connection = existing.Connection, Source = existing.Source };
        if (preserveMetadata && existing.Source == "rog_omni_keyboard")
            incoming = incoming with { Name = existing.Name, NameQuality = existing.NameQuality,
                ModelIdentity = existing.ModelIdentity, ModelIdentityConfirmed = existing.ModelIdentityConfirmed };
        if (!modelChanged && NameQuality(existing) > NameQuality(incoming))
            incoming = incoming with { Name = existing.Name, NameQuality = NameQuality(existing) };
        if (KindQuality(existing) > KindQuality(incoming))
            incoming = incoming with { Kind = existing.Kind, KindQuality = KindQuality(existing) };
        return incoming with { Identity = incoming.Identity ?? existing.Identity,
            LegacyIds = incoming.LegacyIds is { Length: > 0 } ? incoming.LegacyIds : existing.LegacyIds };
    }

    private void ResolveBatchLocked()
    {
        var history = _devices.Keys.ToDictionary(id => id, id => _identities.GetValueOrDefault(id, []), StringComparer.Ordinal);
        var previousRoutes = _routes;
        _routes = _resolver.ResolveBatch(_endpoints.Select(p => new PeripheralIdentityResolver.Observation(p.Key, p.Value)),
            history, _routes, _settings, _taskbar);
        foreach (var (endpoint, id) in _routes)
            if (previousRoutes.TryGetValue(endpoint, out var previousId) && previousId != id) _attempts.Remove(endpoint);
        _settings = _resolver.NormalizeSettings(_settings);
        var selected = _taskbar.Select(_resolver.ResolveId).ToArray();
        _taskbar.Clear(); _taskbar.UnionWith(selected);
        foreach (var (id, item) in _devices.ToArray())
        {
            var survivor = _resolver.ResolveId(id);
            if (survivor == id) continue;
            _devices.Remove(id);
            if (!_devices.TryGetValue(survivor, out var other)) _devices[survivor] = item with { Id = survivor };
            else _devices[survivor] = RetainEndpointSample(other, item with { Id = survivor }, true);
        }
        var historical = _historical.Select(_resolver.ResolveId).ToArray();
        _historical.Clear(); _historical.UnionWith(historical);
        foreach (var (key, reading) in _readings.ToArray())
        {
            var survivor = _resolver.ResolveId(key.Id);
            if (survivor == key.Id) continue;
            _readings.Remove(key);
            StoreReadingLocked(survivor, reading);
        }
        var identities = _identities.ToArray(); _identities.Clear();
        foreach (var (id, evidence) in identities)
        {
            var survivor = _resolver.ResolveId(id);
            _identities[survivor] = _identities.GetValueOrDefault(survivor, []).Concat(evidence).Distinct().Take(16).ToArray();
        }
        foreach (var (endpoint, id) in _routes)
        {
            _historical.Remove(id);
            if (_endpoints[endpoint].Identity is { } evidence)
                _identities[id] = _identities.GetValueOrDefault(id, []).Append(evidence).Distinct().TakeLast(16).ToArray();
        }
        _cacheDirty = true;
    }

    private void StoreReadingLocked(string id, CachedReading incoming)
    {
        var key = (id, incoming.Reading.Role);
        if (!_readings.ContainsKey(key) && _readings.Keys.Count(k => k.Id == id) >= 8) return;
        if (!_readings.TryGetValue(key, out var old)) { _readings[key] = incoming; return; }
        var merged = old;
        var freshValue = HasValue(incoming.Reading) && IsCurrent(incoming.UpdatedAt, incoming.Source, old.UpdatedAt, old.Source);
        if (freshValue)
            merged = merged with { Reading = incoming.Reading with { Charging = old.Reading.Charging },
                UpdatedAt = incoming.UpdatedAt, Source = incoming.Source };
        var chargingAt = incoming.ChargingUpdatedAt;
        var chargingSource = incoming.ChargingSource;
        var hasChargingUpdate = incoming.Reading.Charging.HasValue || chargingAt.HasValue || chargingSource.Length > 0;
        if (!hasChargingUpdate && freshValue && incoming.Source == old.ChargingSource)
        {
            hasChargingUpdate = true; chargingAt = incoming.UpdatedAt; chargingSource = incoming.Source;
        }
        if (hasChargingUpdate && IsCurrent(chargingAt, chargingSource,
            old.ChargingUpdatedAt, old.ChargingSource))
            merged = merged with { Reading = merged.Reading with { Charging = incoming.Reading.Charging },
                ChargingUpdatedAt = chargingAt, ChargingSource = chargingSource };
        _readings[key] = merged;
    }

    private static bool HasValue(PeripheralBatteryReading reading) => reading.Percentage.HasValue ||
        !string.IsNullOrWhiteSpace(reading.LevelText);
    private static bool IsCurrent(DateTimeOffset? at, string source, DateTimeOffset? previousAt, string previousSource) =>
        !(previousAt.HasValue && (!at.HasValue || previousAt > at) ||
            previousAt == at && SourcePriority(previousSource) > SourcePriority(source));

    private void TrimStateLocked()
    {
        var retained = _devices.Keys.Concat(_routes.Values).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _identities.Keys.Where(id => !retained.Contains(id)).ToArray()) _identities.Remove(id);
        foreach (var key in _readings.Keys.Where(k => !retained.Contains(k.Id)).ToArray()) _readings.Remove(key);
    }

    private void MergeEndpointLocked(PeripheralDeviceSnapshot incoming, Endpoint endpoint, bool discovery, bool preserveMetadata = false)
    {
        if (!ValidDevice(incoming) || !_routes.TryGetValue(endpoint, out var id)) return;
        var existing = _endpoints.GetValueOrDefault(endpoint);
        if (OmniModelChanged(existing, incoming) && (preserveMetadata ||
            incoming.UpdatedAt.HasValue && existing?.UpdatedAt > incoming.UpdatedAt)) return;
        if (existing?.Identity is { } oldIdentity && incoming.Identity is { } newIdentity &&
            PeripheralIdentityResolver.Conflicts(oldIdentity, newIdentity))
        {
            if (!discovery) { _discoverPending = true; Signal(); }
            return;
        }
        incoming = RetainOmniModel(_devices.GetValueOrDefault(id), incoming, discovery);
        if (!preserveMetadata && OmniModelChanged(_devices.GetValueOrDefault(id), incoming))
        {
            // The receiver endpoint is stable across pairing changes. Samples
            // belong to the confirmed target, not to the receiver's USB identity.
            foreach (var key in _readings.Keys.Where(k => k.Id == id).ToArray()) _readings.Remove(key);
            if (_devices.TryGetValue(id, out var old))
                _devices[id] = old with { Name = incoming.Name, NameQuality = incoming.NameQuality,
                    ModelIdentity = incoming.ModelIdentity, ModelIdentityConfirmed = true, Batteries = [], UpdatedAt = null, Stale = true };
        }
        if (existing is { State: "disconnected" } && incoming.State != "disconnected") _attempts.Remove(endpoint);
        _endpoints[endpoint] = RetainEndpointSample(existing, incoming, discovery, preserveMetadata);
        foreach (var reading in incoming.Batteries.Take(8))
        {
            if (!HasValue(reading) && !reading.Charging.HasValue) continue;
            StoreReadingLocked(id, new() { Reading = reading,
                UpdatedAt = HasValue(reading) ? incoming.UpdatedAt : null, Source = HasValue(reading) ? incoming.Source : "",
                ChargingUpdatedAt = reading.Charging.HasValue ? incoming.UpdatedAt : null,
                ChargingSource = reading.Charging.HasValue ? incoming.Source : "" });
        }
        RebuildEntityLocked(id);
        _cacheDirty = true;
    }

    private void RebuildEntityLocked(string id)
    {
        var endpoints = _routes.Where(p => p.Value == id).Select(p => _endpoints[p.Key]).ToArray();
        var old = _devices.GetValueOrDefault(id);
        if (endpoints.Length == 0)
        {
            if (old is not null) _devices[id] = old with { State = "disconnected", Stale = true, CanRead = false };
            return;
        }
        if (old is null && _devices.Count >= 256)
        {
            var unused = _devices.Values.Where(d => d.State == "disconnected" && !_taskbar.Contains(d.Id) &&
                (_historical.Contains(d.Id) && !_settings.CardVisibility.GetValueOrDefault(d.Id) ||
                    !_settings.Order.Contains(d.Id) && !_settings.CardVisibility.ContainsKey(d.Id)))
                .OrderBy(d => _settings.Order.Contains(d.Id) || _settings.CardVisibility.ContainsKey(d.Id))
                .ThenBy(d => d.UpdatedAt).FirstOrDefault();
            if (unused is null) return;
            _devices.Remove(unused.Id); _identities.Remove(unused.Id); _historical.Remove(unused.Id);
            foreach (var key in _readings.Keys.Where(k => k.Id == unused.Id).ToArray()) _readings.Remove(key);
        }
        var metadata = endpoints.Concat(old is null ? [] : new[] { old }).ToArray();
        var model = endpoints.Where(d => d.Source == "rog_omni_keyboard" && d.ModelIdentity is not null)
            .OrderByDescending(d => d.ModelIdentityConfirmed).FirstOrDefault();
        var name = model is { ModelIdentityConfirmed: true } ? model : metadata.OrderByDescending(NameQuality).First();
        var kind = metadata.OrderByDescending(KindQuality).First();
        var preferred = endpoints.OrderByDescending(d => d.CanRead).ThenByDescending(d => SourcePriority(d.Source))
            .ThenBy(d => d.Source, StringComparer.Ordinal).First();
        var readings = _readings.Where(p => p.Key.Id == id).Select(p => p.Value).ToArray();
        var latest = readings.Where(r => HasValue(r.Reading)).OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => SourcePriority(r.Source)).FirstOrDefault();
        var online = endpoints.Any(d => d.State == "online");
        _devices[id] = preferred with
        {
            Id = id, ModelIdentity = model?.ModelIdentity, ModelIdentityConfirmed = model?.ModelIdentityConfirmed ?? false,
            Name = name.Name, NameQuality = NameQuality(name), Kind = kind.Kind, KindQuality = KindQuality(kind),
            Brand = metadata.FirstOrDefault(d => d.Brand.Length > 0)?.Brand ?? "",
            CanRead = endpoints.Any(d => d.CanRead),
            State = online ? "online" : endpoints.Any(d => d.State == "unavailable") ? "unavailable" : "disconnected",
            Stale = !online || !endpoints.Any(d => d.State == "online" && !d.Stale && HasReading(d)),
            Batteries = readings.Select(r => r.Reading).ToArray(), UpdatedAt = latest?.UpdatedAt,
            Source = latest?.Source ?? preferred.Source,
            Compatibility = endpoints.Any(d => d.Compatibility == "implemented") ? "implemented" : preferred.Compatibility
        };
    }

    private sealed record CachedReading
    {
        public PeripheralBatteryReading Reading { get; init; } = new();
        public DateTimeOffset? UpdatedAt { get; init; }
        public string Source { get; init; } = "";
        public DateTimeOffset? ChargingUpdatedAt { get; init; }
        public string ChargingSource { get; init; } = "";
    }

    private void PublishLocked(bool refreshing, string error)
    {
        if (_disposed) return;
        var rank = _settings.Order.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);
        // Older caches included every HID collection. Keep configured positions,
        // but expose only devices with a battery source or an explicit selection.
        var devices = _devices.Values.Where(d => !_historical.Contains(d.Id) || _taskbar.Contains(d.Id) ||
            _settings.CardVisibility.GetValueOrDefault(d.Id)).Where(d =>
            d.CanRead || HasReading(d) || d.Compatibility == "extension" ||
            _taskbar.Contains(d.Id) || _settings.CardVisibility.GetValueOrDefault(d.Id)).Select(d => d with
        {
            Brand = d.Brand.Length > 0 ? d.Brand : PeripheralIdentity.Brand(d.Name, source: d.Source),
            ShowInCard = _settings.CardVisibility.TryGetValue(d.Id, out var visible) ? visible : HasReading(d)
        }).OrderBy(d => rank.GetValueOrDefault(d.Id, int.MaxValue)).ThenBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
        var next = new PeripheralBatterySnapshot { Mode = _settings.Mode, HideDisconnected = _settings.HideDisconnected,
            Refreshing = refreshing, Error = error, Devices = devices, Aliases = _resolver.Aliases };
        var previous = Snapshot;
        if (JsonSerializer.Serialize(next, Json) == JsonSerializer.Serialize(previous with { Revision = 0 }, Json)) return;
        Volatile.Write(ref _snapshot, next with { Revision = previous.Revision + 1 });
        Changed?.Invoke();
    }

    private sealed class CacheDocument
    {
        public int Version { get; init; } = 2;
        public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);
        public CacheEntry[] Devices { get; init; } = [];
    }
    private sealed class CacheEntry
    {
        public PeripheralDeviceSnapshot Device { get; init; } = new();
        public PeripheralIdentityEvidence[] Evidence { get; init; } = [];
        public CachedReading[] Readings { get; init; } = [];
        public bool Historical { get; init; }
        public int NameQuality { get; init; }
        public int KindQuality { get; init; }
    }

    private static bool ValidEvidence(PeripheralIdentityEvidence evidence) => evidence is not null &&
        new[] { evidence.ContainerId, evidence.BluetoothAddress, evidence.UniqueSerial, evidence.SerialScope,
            evidence.UnitId, evidence.UnitScope, evidence.EndpointPath }.All(s => s is { Length: <= 1024 });

    private async Task LoadCacheAsync(CancellationToken token)
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return;
        try
        {
            if (new FileInfo(_cachePath).Length > 4 * 1024 * 1024) return;
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_cachePath, token).ConfigureAwait(false));
            CacheDocument cache;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                cache = new() { Devices = (document.RootElement.Deserialize<PeripheralDeviceSnapshot[]>(Json) ?? [])
                    .Where(ValidDevice).Take(256).Select(d => new CacheEntry { Device = d, Historical = true }).ToArray() };
            else
            {
                cache = document.RootElement.Deserialize<CacheDocument>(Json) ?? new();
                if (cache.Version != 2) return;
            }
            lock (_gate)
            {
                _resolver.LoadAliases(cache.Aliases ?? []);
                _settings = _resolver.NormalizeSettings(_settings);
                var selected = _taskbar.Select(_resolver.ResolveId).ToArray();
                _taskbar.Clear(); _taskbar.UnionWith(selected);
                foreach (var entry in (cache.Devices ?? []).Where(e => e is not null && ValidDevice(e.Device)).Take(256))
                {
                    var id = _resolver.ResolveId(entry.Device.Id);
                    _devices[id] = entry.Device with { Id = id, State = "disconnected", Stale = true, CanRead = false,
                        NameQuality = entry.NameQuality, KindQuality = entry.KindQuality };
                    if (entry.Historical) _historical.Add(id);
                    _identities[id] = (entry.Evidence ?? []).Where(ValidEvidence).Take(16).ToArray();
                    var readings = (entry.Readings ?? []).Where(r => r is not null && r.Reading is not null &&
                        r.Reading.Role is { Length: > 0 and <= 64 } && (r.Reading.Percentage is null or >= 0 and <= 100) &&
                        (r.Reading.LevelText?.Length ?? 0) <= 128 && r.Source is not null && r.ChargingSource is not null)
                        .Take(8).Select(r => r with
                        {
                            ChargingUpdatedAt = r.ChargingUpdatedAt ?? (r.Reading.Charging.HasValue ? r.UpdatedAt : null),
                            ChargingSource = r.ChargingSource.Length > 0 ? r.ChargingSource : r.Source
                        }).ToArray();
                    foreach (var reading in readings.Length > 0 ? readings : entry.Device.Batteries.Select(b =>
                        new CachedReading { Reading = b, UpdatedAt = HasValue(b) ? entry.Device.UpdatedAt : null,
                            Source = entry.Device.Source, ChargingUpdatedAt = b.Charging.HasValue ? entry.Device.UpdatedAt : null,
                            ChargingSource = b.Charging.HasValue ? entry.Device.Source : "" }).ToArray())
                        StoreReadingLocked(id, reading);
                }
                PublishLocked(false, "");
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private async Task SaveCacheAsync(bool force, CancellationToken token)
    {
        if (_cachePath is null) return;
        CacheDocument cache;
        lock (_gate)
        {
            if (!_cacheDirty || (!force && _clock.GetUtcNow() < _nextCacheWrite)) return;
            cache = new() { Aliases = _resolver.Aliases, Devices = _devices.Values.Select(d => new CacheEntry
            {
                Device = d, Evidence = _identities.GetValueOrDefault(d.Id, []), Historical = _historical.Contains(d.Id),
                NameQuality = NameQuality(d), KindQuality = KindQuality(d),
                Readings = _readings.Where(p => p.Key.Id == d.Id).Select(p => p.Value).ToArray()
            }).ToArray() };
            _cacheDirty = false;
            _nextCacheWrite = _clock.GetUtcNow().AddMinutes(5);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!);
            var pending = _cachePath + ".tmp";
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(cache, Json), token).ConfigureAwait(false);
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
