using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Gaming.Input;
using Windows.System.Power;

namespace TaskBarWidgets.Media.Peripherals;

internal sealed partial class WindowsPeripheralBatteryProvider : IPeripheralBatteryProvider, IPeripheralBatteryNotifications
{
    private readonly WindowsPeripheralLifetime _lifetime;
    private readonly IWindowsPeripheralCatalog _catalog;
    private readonly Dictionary<string, PeripheralDeviceSnapshot> _known = new(StringComparer.Ordinal);
    private HashSet<string> _present = new(StringComparer.Ordinal);
    private readonly Func<WindowsPeripheralNode, CancellationToken, Task<int?>>? _pnpReader;
    private readonly bool _useControllers;
    private bool _controllersSubscribed;
    private volatile bool _disposed;
    public event Action? DevicesChanged;

    /// <summary>Starts no enumeration until DiscoverAsync/ReadAsync is requested.</summary>
    public WindowsPeripheralBatteryProvider() : this(new WindowsPeripheralCatalog(), null, true) { }

    internal WindowsPeripheralBatteryProvider(IWindowsPeripheralCatalog catalog,
        Func<WindowsPeripheralNode, CancellationToken, Task<int?>>? pnpReader = null, bool useControllers = false,
        Func<WindowsPeripheralNode, WindowsPeripheralLifetime, CancellationToken, Task<IWindowsBasBatteryLease?>>? openBas = null,
        TimeSpan? shutdownBudget = null)
    {
        _lifetime = new(shutdownBudget);
        _openBas = openBas ?? WindowsBasBatteryLease.OpenAsync;
        _catalog = catalog;
        _pnpReader = pnpReader;
        _useControllers = useControllers;
        _catalog.Changed += Changed;
    }

    private void Changed()
    {
        if (_disposed) return;
        bool hasLeases;
        lock (_notificationSync) hasLeases = _leases.Count != 0;
        if (hasLeases) ReconcileLeases(_catalog.Snapshot);
        try { DevicesChanged?.Invoke(); } catch { }
    }
    private void ControllerChanged(object? sender, RawGameController controller) => Changed();

    public Task<IReadOnlyList<PeripheralDeviceSnapshot>> DiscoverAsync(CancellationToken cancellationToken) =>
        _lifetime.Run<IReadOnlyList<PeripheralDeviceSnapshot>>(async token =>
        {
            var nodes = (await _catalog.GetAsync(token).ConfigureAwait(false)).ToList();
            var controllers = await Controllers(nodes, token).ConfigureAwait(false);
            return WindowsPeripheralHistory.CurrentFirst(Discover(nodes, controllers).Values, _present);
        }, cancellationToken);

    public Task<IReadOnlyList<PeripheralDeviceSnapshot>> ReadAsync(IReadOnlySet<string> deviceIds,
        PeripheralBatterySettings settings, CancellationToken cancellationToken) =>
        _lifetime.Run<IReadOnlyList<PeripheralDeviceSnapshot>>(async token =>
        {
            var nodes = (await _catalog.GetAsync(token).ConfigureAwait(false)).ToList();
            var controllers = await Controllers(nodes, token).ConfigureAwait(false);
            var devices = Discover(nodes, controllers);
            var groups = Groups(nodes);
            foreach (var id in deviceIds)
            {
                token.ThrowIfCancellationRequested();
                if (!devices.TryGetValue(id, out var device) || device.State == "disconnected") continue;
                try
                {
                    var notified = await ReadLease(id, token).ConfigureAwait(false);
                    if (notified is not null) { _known[id] = notified; continue; }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception ex) when (Recoverable(ex)) { }
                var readings = new List<(string Key, PeripheralBatteryReading Reading)>();
                string source = "windows-pnp";
                if (groups.TryGetValue(id, out var group))
                {
                    foreach (var node in group.Where(n => n.Pnp && n.Present && n.Battery is not null &&
                        (!n.Bluetooth || group.Any(e => e.Connected == true))))
                    {
                        await _lifetime.WaitNative(token).ConfigureAwait(false);
                        int? percentage = null;
                        try
                        {
                            // Reading the exact Windows property is passive; charging stays unknown.
                            percentage = _pnpReader is not null ? await _pnpReader(node, token).ConfigureAwait(false)
                                : await ReadPnp(node, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                        catch (Exception ex) when (Recoverable(ex)) { }
                        readings.Add((node.InstanceId, new() { Percentage = percentage is >= 0 and <= 100 ? percentage : null }));
                    }
                    // Notification leases own BAS. Without active demand no BLE I/O starts.

                }
                if (!HasValue(readings) && controllers.TryGetValue(id, out var controller))
                {
                    try
                    {
                        var report = controller.TryGetBatteryReport();
                        var reading = report is null || report.Status == BatteryStatus.NotPresent ? null
                            : WindowsPeripheralIdentity.Capacity(report.RemainingCapacityInMilliwattHours,
                                report.FullChargeCapacityInMilliwattHours, report.Status == BatteryStatus.Charging ? true
                                    : report.Status == BatteryStatus.Discharging || report.Status == BatteryStatus.Idle ? false : null);
                        if (reading is not null) { readings = [(controller.NonRoamableId, reading)]; source = "windows-gamepad"; }
                    }
                    catch (Exception ex) when (Recoverable(ex)) { }
                }
                token.ThrowIfCancellationRequested();
                _known[id] = Apply(device, readings, source, DateTimeOffset.UtcNow);
            }
            return deviceIds.Where(_known.ContainsKey).Select(id => _known[id]).ToArray();
        }, cancellationToken);

    internal static PeripheralDeviceSnapshot Apply(PeripheralDeviceSnapshot previous,
        IEnumerable<(string Key, PeripheralBatteryReading Reading)> readings, string source, DateTimeOffset now)
    {
        var batteries = WindowsPeripheralIdentity.Roles(readings);
        return HasValue(readings) ? previous with
        {
            Batteries = batteries, UpdatedAt = now, Stale = false, State = "online",
            Source = source, Compatibility = "implemented", CanRead = true
        } : previous with { Stale = previous.UpdatedAt.HasValue, State = previous.State == "disconnected" ? "disconnected" : "unavailable" };
    }

    private static bool HasValue(IEnumerable<(string Key, PeripheralBatteryReading Reading)> readings) =>
        readings.Any(v => v.Reading.Percentage.HasValue || v.Reading.LevelText is not null || v.Reading.Charging.HasValue);

    internal static Dictionary<string, WindowsPeripheralNode[]> Groups(IReadOnlyList<WindowsPeripheralNode> nodes) =>
        nodes.Select(n => (Node: n, Key: WindowsPeripheralIdentity.Key(n, nodes))).Where(n => n.Key is not null)
            .GroupBy(n => PeripheralIdentity.FromKey(n.Key!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Node).ToArray(), StringComparer.Ordinal);

    private Dictionary<string, PeripheralDeviceSnapshot> Discover(IReadOnlyList<WindowsPeripheralNode> nodes,
        Dictionary<string, RawGameController> controllers)
    {
        ReconcileLeases(nodes);
        var phones = nodes.Where(n => n.Kind == "phone" && n.Container != Guid.Empty).Select(n => n.Container).ToHashSet();
        var groups = Groups(nodes).Where(p => !p.Value.Any(n => phones.Contains(n.Container)) &&
            (p.Value.Any(IsBatteryCandidate) || controllers.ContainsKey(p.Key))).ToArray();
        _present = groups.Where(p => p.Value.Any(n => n.Present)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _known.Keys.Where(id => !_present.Contains(id)).ToArray())
            _known[id] = _known[id] with { State = "disconnected", Stale = _known[id].UpdatedAt.HasValue };
        lock (_notificationSync)
            foreach (var state in _leases.Values)
                if (state.Active && state.Snapshot.Batteries.Any(b => b.Percentage.HasValue))
                    WindowsPeripheralHistory.Remember(_known, state.Snapshot, _present);
        foreach (var (id, group) in groups)
        {
            var readable = group.Any(IsBatteryCandidate) || controllers.ContainsKey(id);
            var old = _known.GetValueOrDefault(id) ?? new() { Id = id };
            var bt = group.Any(n => n.Bluetooth);
            var connected = controllers.ContainsKey(id) || (bt ? group.Any(n => n.Connected == true) : group.Any(n => n.Present));
            var disconnected = bt && group.Where(n => n.Bluetooth && !n.Pnp).Any() &&
                group.Where(n => n.Bluetooth && !n.Pnp).All(n => n.Connected == false);
            var name = group.OrderByDescending(n => n.Bluetooth && !n.Pnp && n.Address is not null)
                .Select(n => n.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Peripheral";
            WindowsPeripheralHistory.Remember(_known, old with
            {
                Name = name, Brand = PeripheralIdentity.Brand(name, string.Join(" ", group.Select(n => n.InstanceId))),
                Kind = controllers.ContainsKey(id) ? "gamepad" : group.Select(n => n.Kind).FirstOrDefault(k => k != "unknown") ?? "unknown",
                Connection = bt ? "bluetooth" : "usb", CanRead = readable,
                State = disconnected || !group.Any(n => n.Present) ? "disconnected" : connected ? "online" : "unavailable",
                Source = old.Source.Length > 0 ? old.Source : "windows", Compatibility = readable ? "implemented" : "identityOnly",
                Stale = old.UpdatedAt.HasValue && (old.Stale || !connected || disconnected)
            }, _present);
        }
        return _known;
    }

    private static bool IsBatteryCandidate(WindowsPeripheralNode node) =>
        WindowsPeripheralIdentity.Percentage(node.Battery).HasValue || node.BatteryService ||
        (node.BluetoothLE && node.Kind is "mouse" or "keyboard" or "headset" or "earbuds" or "gamepad" or "pen");

    private async Task<int?> ReadPnp(WindowsPeripheralNode node, CancellationToken token)
    {
        if (WindowsPeripheralIdentity.Percentage(node.Battery) is null) return null;
        var info = await _lifetime.Await(() => DeviceInformation.CreateFromIdAsync(node.Path,
            WindowsPeripheralCatalog.Properties, DeviceInformationKind.Device), token).ConfigureAwait(false);
        return info is null ? null : WindowsPeripheralIdentity.Percentage(WindowsPeripheralCatalog.Node(info).Battery);
    }

    private async Task<Dictionary<string, RawGameController>> Controllers(List<WindowsPeripheralNode> nodes, CancellationToken token)
    {
        var result = new Dictionary<string, RawGameController>(StringComparer.Ordinal);
        if (!_useControllers) return result;
        token.ThrowIfCancellationRequested();
        try
        {
            if (!_controllersSubscribed)
            {
                RawGameController.RawGameControllerAdded += ControllerChanged;
                RawGameController.RawGameControllerRemoved += ControllerChanged;
                _controllersSubscribed = true;
            }
            foreach (var controller in RawGameController.RawGameControllers.ToArray())
            {
                token.ThrowIfCancellationRequested();
                var rawId = controller.NonRoamableId;
                if (string.IsNullOrWhiteSpace(rawId)) continue;
                // No XInput ordinal: resolve the WinRT instance back to a real device container.
                var node = nodes.FirstOrDefault(n => n.Path.Equals(rawId, StringComparison.OrdinalIgnoreCase)
                    || n.InstanceId.Equals(rawId, StringComparison.OrdinalIgnoreCase));
                if (node is null)
                {
                    try
                    {
                        var info = await _lifetime.Await(() => DeviceInformation.CreateFromIdAsync(rawId,
                            WindowsPeripheralCatalog.Properties, DeviceInformationKind.DeviceInterface), token).ConfigureAwait(false);
                        if (info is not null) node = WindowsPeripheralCatalog.Node(info);
                        if (node is { Container: var container } && container == Guid.Empty && node.InstanceId.Length > 0)
                        {
                            var pnp = await _lifetime.Await(() => DeviceInformation.CreateFromIdAsync(node.InstanceId,
                                WindowsPeripheralCatalog.Properties, DeviceInformationKind.Device), token).ConfigureAwait(false);
                            if (pnp is not null) node = WindowsPeripheralCatalog.Node(pnp);
                        }
                    }
                    catch (Exception ex) when (Recoverable(ex)) { }
                }
                if (node is null) continue;
                var key = WindowsPeripheralIdentity.Key(node, nodes);
                if (key is not null)
                {
                    result.TryAdd(PeripheralIdentity.FromKey(key), controller);
                    if (!nodes.Contains(node)) nodes.Add(node with { Kind = "gamepad" });
                }
            }
        }
        catch (Exception ex) when (Recoverable(ex)) { }
        return result;
    }

    internal static bool Recoverable(Exception ex) => ex is System.Runtime.InteropServices.COMException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.IO.IOException;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        ReadingChanged = null;
        DevicesChanged = null;
        StopNotifications();
        _catalog.Changed -= Changed;
        return _lifetime.DisposeAsync(async () =>
        {
            _ = _lifetime.Retire(() =>
            {
                if (_controllersSubscribed)
                {
                    RawGameController.RawGameControllerAdded -= ControllerChanged;
                    RawGameController.RawGameControllerRemoved -= ControllerChanged;
                }
            });
            await _catalog.StopAsync().ConfigureAwait(false);
        });
    }
}
