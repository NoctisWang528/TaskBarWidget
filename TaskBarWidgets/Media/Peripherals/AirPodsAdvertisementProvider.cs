using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace TaskBarWidgets.Media.Peripherals;

internal sealed record AirPodsSighting(ulong Address, BluetoothAddressType AddressType, short Rssi,
    int Count, AirPodsAdvertisement Advertisement, DateTimeOffset At);

// Proximity advertisements provide coarse battery levels, not authenticated device
// identities. This optional provider accepts only one nearby matching broadcaster
// for one connected paired model. No random address becomes a persisted identity.
internal sealed class AirPodsAdvertisementProvider : IPeripheralBatteryProvider
{
    private readonly IWindowsPeripheralCatalog _catalog;
    private readonly WindowsPeripheralLifetime _lifetime = new();
    private readonly Func<IReadOnlySet<ushort>, CancellationToken, Task<IReadOnlyList<AirPodsSighting>>> _scan;
    private readonly Dictionary<string, PeripheralDeviceSnapshot> _known = new(StringComparer.Ordinal);
    private readonly object _scanSync = new();
    private CancellationTokenSource? _activeScan;
    private Task _scannerStopped = Task.CompletedTask;
    private volatile bool _enabled = true, _disposed;
    public event Action? DevicesChanged;

    internal AirPodsAdvertisementProvider() : this(new WindowsPeripheralCatalog()) { }
    internal AirPodsAdvertisementProvider(IWindowsPeripheralCatalog catalog,
        Func<IReadOnlySet<ushort>, CancellationToken, Task<IReadOnlyList<AirPodsSighting>>>? scan = null)
    {
        _catalog = catalog;
        _scan = scan ?? ScanAsync;
        _catalog.Changed += Changed;
    }

    internal void Configure(PeripheralBatterySettings settings)
    {
        if (_enabled == settings.AirPodsAdvertisementEnabled) return;
        _enabled = settings.AirPodsAdvertisementEnabled;
        if (!_enabled) lock (_scanSync) _activeScan?.Cancel();
        Changed();
    }

    private void Changed()
    {
        if (_disposed) return;
        try { DevicesChanged?.Invoke(); } catch { }
    }

    public Task<IReadOnlyList<PeripheralDeviceSnapshot>> DiscoverAsync(CancellationToken cancellationToken) =>
        _lifetime.Run<IReadOnlyList<PeripheralDeviceSnapshot>>(async token =>
        {
            if (!_enabled) return [];
            return Discover(await _catalog.GetAsync(token).ConfigureAwait(false)).Values.Select(t => t.Device).ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<PeripheralDeviceSnapshot>> ReadAsync(IReadOnlySet<string> deviceIds,
        PeripheralBatterySettings settings, CancellationToken cancellationToken) =>
        _lifetime.Run<IReadOnlyList<PeripheralDeviceSnapshot>>(async token =>
        {
            if (!_enabled || !settings.AirPodsAdvertisementEnabled) return [];
            var targets = Discover(await _catalog.GetAsync(token).ConfigureAwait(false));
            var connected = targets.Values.Where(t => t.Device.State == "online").ToArray();
            var selected = connected.Where(t => deviceIds.Contains(t.Device.Id) &&
                connected.Count(other => other.Model == t.Model) == 1).ToArray();
            var scanStarted = DateTimeOffset.UtcNow;
            IReadOnlyList<AirPodsSighting> sightings = [];
            if (selected.Length != 0)
            {
                using var scanStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (_scanSync) { if (!_enabled) return []; _activeScan = scanStop; }
                try { sightings = await _scan(selected.Select(t => t.Model).ToHashSet(), scanStop.Token).ConfigureAwait(false); }
                finally { lock (_scanSync) _activeScan = null; }
            }
            token.ThrowIfCancellationRequested();
            if (!_enabled) return [];
            // Audio may have disconnected while listening. Recheck paired endpoints;
            // a nearby broadcast alone never establishes a connection to this PC.
            targets = Discover(_catalog.Snapshot);
            foreach (var id in deviceIds)
            {
                if (!targets.TryGetValue(id, out var target) || target.Device.State != "online") continue;
                var sameModel = targets.Values.Count(t => t.Model == target.Model && t.Device.State == "online");
                var candidates = sightings.Where(s => s.Advertisement.Model == target.Model && s.Rssi >= -85 &&
                        s.At >= scanStarted.AddSeconds(-2) && s.At <= DateTimeOffset.UtcNow.AddSeconds(2))
                    .GroupBy(s => (s.Address, s.AddressType)).Select(g => g.OrderByDescending(s => s.At).First()).ToArray();
                var matched = sameModel == 1 && candidates.Length == 1 && candidates[0].Count >= 2
                    ? candidates[0] : null;
                _known[id] = matched is not null && matched.Advertisement.Batteries.Any(b => b.Percentage.HasValue)
                    ? target.Device with { Batteries = matched.Advertisement.Batteries, UpdatedAt = matched.At,
                        State = "online", Stale = false }
                    : target.Device with { State = "unavailable", Stale = target.Device.UpdatedAt.HasValue };
            }
            return deviceIds.Where(_known.ContainsKey).Select(id => _known[id]).ToArray();
        }, cancellationToken);

    private sealed record Target(PeripheralDeviceSnapshot Device, ushort Model);

    private Dictionary<string, Target> Discover(IReadOnlyList<WindowsPeripheralNode> nodes)
    {
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        foreach (var (id, group) in WindowsPeripheralBatteryProvider.Groups(nodes))
        {
            var endpoint = group.FirstOrDefault(n => n.Bluetooth && !n.BluetoothLE && !n.Pnp &&
                WindowsPeripheralIdentity.Address(n.Address) is not null);
            if (endpoint is null || group.Any(n => WindowsPeripheralIdentity.Percentage(n.Battery).HasValue || n.BatteryService)) continue;
            var models = group.SelectMany(n => n.HardwareIds.Append(n.InstanceId)).Select(AppleModel)
                .Where(m => m.HasValue).Select(m => m!.Value).Distinct().ToArray();
            if (models.Length != 1) continue;
            var previous = _known.GetValueOrDefault(id) ?? new() { Id = id };
            var name = endpoint.Name.EndsWith(" - Find My", StringComparison.Ordinal)
                ? endpoint.Name[..^10] : endpoint.Name;
            var state = !endpoint.Present || endpoint.Connected == false ? "disconnected"
                : endpoint.Connected == true ? "online" : "unavailable";
            var device = previous with { Name = name, Kind = models[0] is 0x200a or 0x201f or 0x202d ? "headset" : "earbuds",
                Brand = "apple", Connection = "bluetooth", State = state, CanRead = true,
                Source = "apple-ble-proximity", Compatibility = "implemented",
                Stale = previous.UpdatedAt.HasValue && (previous.Stale || state != "online") };
            _known[id] = device;
            targets[id] = new(device, models[0]);
        }
        foreach (var id in _known.Keys.Where(id => !targets.ContainsKey(id)).ToArray())
            _known[id] = _known[id] with { State = "disconnected", Stale = _known[id].UpdatedAt.HasValue };
        return targets;
    }

    internal static ushort? AppleModel(string identity)
    {
        // Windows Bluetooth Device ID profile encodes the company-ID namespace as
        // 0001. Do not admit a renameable name, USB VID, or an arbitrary Apple ad.
        var match = Regex.Match(identity, @"(?:^|[\\_])VID&0001004C_PID&([0-9A-F]{4})(?:[\\_&]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && ushort.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var model) && AirPodsAdvertisement.IsSupportedModel(model)
            ? model : null;
    }

    private async Task<IReadOnlyList<AirPodsSighting>> ScanAsync(IReadOnlySet<ushort> models, CancellationToken token)
    {
        await _scannerStopped.WaitAsync(token).ConfigureAwait(false);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = 0x004c });
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new Dictionary<(ulong, BluetoothAddressType), AirPodsSighting>();
        var sync = new object();
        void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (token.IsCancellationRequested || !_enabled || args.RawSignalStrengthInDBm < -85) return;
            try
            {
                foreach (var manufacturer in args.Advertisement.ManufacturerData)
                {
                    if (manufacturer.CompanyId != 0x004c || manufacturer.Data.Length != 27) continue;
                    using var reader = DataReader.FromBuffer(manufacturer.Data);
                    var bytes = new byte[27]; reader.ReadBytes(bytes);
                    if (!AirPodsAdvertisement.TryParse(bytes, out var packet) || packet is null || !models.Contains(packet.Model)) continue;
                    var key = (args.BluetoothAddress, args.BluetoothAddressType);
                    lock (sync)
                    {
                        if (samples.Count >= 32 && !samples.ContainsKey(key)) continue;
                        samples[key] = new(args.BluetoothAddress, args.BluetoothAddressType, args.RawSignalStrengthInDBm,
                            Math.Min(1000, (samples.GetValueOrDefault(key)?.Count ?? 0) + 1), packet, args.Timestamp);
                    }
                }
            }
            catch (Exception error) when (WindowsPeripheralBatteryProvider.Recoverable(error)) { }
        }
        void Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            watcher.Stopped -= Stopped;
            stopped.TrySetResult();
        }
        watcher.Received += Received;
        watcher.Stopped += Stopped;
        try
        {
            watcher.Start();
            _scannerStopped = stopped.Task;
            var window = Task.Delay(TimeSpan.FromSeconds(8), token);
            var completed = await Task.WhenAny(window, stopped.Task).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (completed == stopped.Task) return [];
        }
        finally
        {
            watcher.Received -= Received;
            watcher.Stop();
            // Retain a late stop; never start a second watcher over a stuck driver.
            if (ReferenceEquals(_scannerStopped, stopped.Task))
                try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            else watcher.Stopped -= Stopped;
        }
        lock (sync) return samples.Values.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        DevicesChanged = null;
        _catalog.Changed -= Changed;
        return _lifetime.DisposeAsync(async () => await _catalog.StopAsync().ConfigureAwait(false));
    }
}
