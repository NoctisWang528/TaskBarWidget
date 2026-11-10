using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace TaskBarWidgets.Media.Peripherals;

internal interface IWindowsPeripheralCatalog
{
    event Action? Changed;
    IReadOnlyList<WindowsPeripheralNode> Snapshot { get; }
    Task<IReadOnlyList<WindowsPeripheralNode>> GetAsync(CancellationToken token);
    Task StopAsync();
}

internal sealed class WindowsPeripheralCatalog : IWindowsPeripheralCatalog
{
    internal const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    internal static readonly string[] Properties =
    [
        "System.Devices.ContainerId", "System.Devices.Aep.ContainerId", "System.Devices.DeviceInstanceId",
        "System.Devices.HardwareIds", "System.Devices.Category", "System.Devices.ClassGuid",
        "System.Devices.Aep.Category", "System.Devices.Aep.Bluetooth.Cod.Major", "System.Devices.Aep.Bluetooth.Cod.Minor",
        "System.Devices.Present", "System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress",
        "System.Devices.BatteryLife", BatteryProperty,
        "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 1"
    ];
    private readonly object _sync = new();
    private readonly Dictionary<string, (DeviceInformation Info, bool LE, bool Bluetooth)> _devices = new(StringComparer.Ordinal);
    private readonly List<DeviceWatcher> _watchers = [];
    private readonly Dictionary<DeviceWatcher, TaskCompletionSource> _initial = [];
    private readonly Dictionary<DeviceWatcher, TaskCompletionSource> _stopped = [];
    private readonly Dictionary<DeviceWatcher, (bool LE, bool Bluetooth)> _types = [];
    private bool _disposed;
    public event Action? Changed;

    public async Task<IReadOnlyList<WindowsPeripheralNode>> GetAsync(CancellationToken token)
    {
        Task initial;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watchers.Count == 0)
            {
                try
                {
                    Add("System.Devices.Present:=System.StructuredQueryType.Boolean#True", DeviceInformationKind.Device, false, false);
                    Add("System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\" OR System.Devices.InterfaceClassGuid:=\"{A5DCBF10-6530-11D2-901F-00C04FB951ED}\"",
                        DeviceInformationKind.DeviceInterface, false, false);
                    Add(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), DeviceInformationKind.AssociationEndpoint, true, true);
                    Add(BluetoothDevice.GetDeviceSelectorFromPairingState(true), DeviceInformationKind.AssociationEndpoint, false, true);
                }
                catch
                {
                    // The already-started watchers remain owned and are stopped on disposal.
                    foreach (var pending in _initial.Values) pending.TrySetResult();
                    throw;
                }
            }
            initial = Task.WhenAll(_initial.Values.Select(s => s.Task));
        }
        await initial.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        return Snapshot;
    }

    public IReadOnlyList<WindowsPeripheralNode> Snapshot
    {
        get
        {
            lock (_sync)
            {
                var nodes = _devices.Values.Select(v => Node(v.Info, v.LE, v.Bluetooth)).ToArray();
                return nodes.Select(n => n.Container != Guid.Empty || n.InstanceId.Length == 0 ? n : n with
                {
                    Container = nodes.FirstOrDefault(p => p.Pnp && p.Container != Guid.Empty &&
                        p.InstanceId.Equals(n.InstanceId, StringComparison.OrdinalIgnoreCase))?.Container ?? Guid.Empty
                }).ToArray();
            }
        }
    }

    private void Add(string selector, DeviceInformationKind kind, bool le, bool bluetooth)
    {
        var watcher = DeviceInformation.CreateWatcher(selector, Properties, kind);
        _watchers.Add(watcher);
        _types.Add(watcher, (le, bluetooth));
        _initial.Add(watcher, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _stopped.Add(watcher, new(TaskCreationOptions.RunContinuationsAsynchronously));
        watcher.Added += Added;
        watcher.Updated += Updated;
        watcher.Removed += Removed;
        watcher.EnumerationCompleted += Enumerated;
        watcher.Stopped += Stopped;
        watcher.Start();
    }

    private void Added(DeviceWatcher sender, DeviceInformation info)
    {
        bool relevant;
        lock (_sync)
        {
            if (_disposed) return;
            var type = _types[sender];
            _devices[info.Id] = (info, type.LE, type.Bluetooth);
            relevant = Relevant(Node(info, type.LE, type.Bluetooth));
        }
        if (relevant) Notify();
    }

    private void Updated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        var changed = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (_devices.TryGetValue(update.Id, out var device))
            {
                var before = Node(device.Info, device.LE, device.Bluetooth);
                device.Info.Update(update);
                var after = Node(device.Info, device.LE, device.Bluetooth);
                changed = (Relevant(before) || Relevant(after)) && !Same(before, after);
            }
        }
        if (changed) Notify();
    }

    private void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        var relevant = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (_devices.Remove(update.Id, out var device)) relevant = Relevant(Node(device.Info, device.LE, device.Bluetooth));
        }
        if (relevant) Notify();
    }

    internal static bool Relevant(WindowsPeripheralNode node) => node.Bluetooth || !node.Pnp ||
        node.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
        node.InstanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ||
        node.Kind is "mouse" or "keyboard" or "headset" or "gamepad" or "pen";

    internal static bool Same(WindowsPeripheralNode a, WindowsPeripheralNode b) =>
        a.Path == b.Path && a.Name == b.Name && a.Container == b.Container && a.Address == b.Address &&
        a.InstanceId == b.InstanceId && a.Kind == b.Kind && a.Present == b.Present && a.Connected == b.Connected &&
        a.BluetoothLE == b.BluetoothLE && a.Bluetooth == b.Bluetooth && Equals(a.Battery, b.Battery) &&
        a.HardwareIds.SequenceEqual(b.HardwareIds, StringComparer.OrdinalIgnoreCase);

    private void Notify()
    {
        // Subscribers are notifications, never part of the WinRT callback's success path.
        try { Changed?.Invoke(); } catch { }
    }
    private void Enumerated(DeviceWatcher sender, object args) { lock (_sync) if (_initial.TryGetValue(sender, out var initial)) initial.TrySetResult(); }
    private void Stopped(DeviceWatcher sender, object args)
    {
        lock (_sync)
        {
            if (_initial.TryGetValue(sender, out var initial)) initial.TrySetResult();
            if (_stopped.TryGetValue(sender, out var stopped)) stopped.TrySetResult();
        }
    }

    internal static WindowsPeripheralNode Node(DeviceInformation info, bool le = false, bool bluetooth = false)
    {
        object? Get(string key) => info.Properties.TryGetValue(key, out var value) ? value : null;
        string Text(string key) => Get(key)?.ToString() ?? "";
        Guid.TryParse(Text(bluetooth ? "System.Devices.Aep.ContainerId" : "System.Devices.ContainerId"), out var container);
        var hardware = Get("System.Devices.HardwareIds") as string[] ?? [];
        string Category(string key) => Get(key) is string[] categories ? string.Join(" ", categories) : Text(key);
        var category = Category("System.Devices.Category") + " " + Category("System.Devices.Aep.Category");
        // WinRT returns a Guid here; its default text has no braces.
        var deviceClass = Text("System.Devices.ClassGuid").Trim('{', '}');
        var kind = category.Contains("Mouse", StringComparison.OrdinalIgnoreCase) || deviceClass.Equals("4d36e96f-e325-11ce-bfc1-08002be10318", StringComparison.OrdinalIgnoreCase) ? "mouse"
            : category.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) || deviceClass.Equals("4d36e96b-e325-11ce-bfc1-08002be10318", StringComparison.OrdinalIgnoreCase) ? "keyboard"
            : category.Contains("Audio", StringComparison.OrdinalIgnoreCase) || deviceClass.Equals("4d36e96c-e325-11ce-bfc1-08002be10318", StringComparison.OrdinalIgnoreCase) ? "headset"
            : category.Contains("Game", StringComparison.OrdinalIgnoreCase) ? "gamepad"
            : category.Contains("Pen", StringComparison.OrdinalIgnoreCase) ? "pen" : "unknown";
        _ = uint.TryParse(Text("System.Devices.Aep.Bluetooth.Cod.Major"), out var major);
        _ = uint.TryParse(Text("System.Devices.Aep.Bluetooth.Cod.Minor"), out var minor);
        if (major == 2 || category.Contains("Phone", StringComparison.OrdinalIgnoreCase) && !category.Contains("Headphone", StringComparison.OrdinalIgnoreCase)) kind = "phone";
        else if (kind == "unknown" && major == 4) kind = "headset";
        else if (kind == "unknown" && major == 5)
            kind = (minor & 0x30) == 0x10 ? "keyboard" : (minor & 0x30) == 0x20 ? "mouse"
                : (minor & 0xf) is 1 or 2 ? "gamepad" : "unknown";
        // Names only choose an icon. They never identify or merge devices.
        var name = info.Name.ToLowerInvariant();
        if (kind is "unknown" or "headset")
        {
            if (name.Contains("earbud") || name.Contains("airpods") || name.Contains("buds") || name.Contains("耳夹") || name.Contains("入耳")) kind = "earbuds";
            else if (name.Contains("headset") || name.Contains("headphone") || name.Contains("耳机")) kind = "headset";
            else if (name.Contains("controller") || name.Contains("gamepad") || name.Contains("xbox") || name.Contains("手柄")) kind = "gamepad";
        }
        var instance = Text("System.Devices.DeviceInstanceId");
        if (instance.Length == 0 && info.Kind == DeviceInformationKind.Device) instance = info.Id;
        var address = WindowsPeripheralIdentity.Address(Text("System.Devices.Aep.DeviceAddress"))
            ?? WindowsPeripheralIdentity.Address(Text("{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 1"));
        bluetooth |= instance.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
        return new(info.Id, info.Name, container, address, instance, hardware, kind,
            Get("System.Devices.Present") is not false, Get("System.Devices.Aep.IsConnected") as bool?,
            le, bluetooth, Get(BatteryProperty) ?? Get("System.Devices.BatteryLife"), info.Kind == DeviceInformationKind.Device,
            instance.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase) &&
                instance.Contains("{0000180f-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase));
    }

    public async Task StopAsync()
    {
        Task[] stops;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Changed = null;
            foreach (var watcher in _watchers)
            {
                watcher.Added -= Added;
                watcher.Updated -= Updated;
                watcher.Removed -= Removed;
                watcher.EnumerationCompleted -= Enumerated;
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
                else if (watcher.Status != DeviceWatcherStatus.Stopping) _stopped[watcher].TrySetResult();
                _initial[watcher].TrySetResult();
            }
            stops = _stopped.Values.Select(s => s.Task).ToArray();
        }
        await Task.WhenAll(stops).ConfigureAwait(false);
        lock (_sync)
        {
            foreach (var watcher in _watchers) watcher.Stopped -= Stopped;
            _watchers.Clear();
            _devices.Clear();
            _initial.Clear();
            _stopped.Clear();
            _types.Clear();
        }
    }
}
