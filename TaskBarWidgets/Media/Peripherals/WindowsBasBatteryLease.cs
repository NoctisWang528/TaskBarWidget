using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace TaskBarWidgets.Media.Peripherals;

internal interface IWindowsBasBatteryLease : IDisposable
{
    event Action<string, int>? BatteryChanged;
    event Action? Invalidated;
    bool Connected { get; }
    bool HasFallback { get; }
    IReadOnlyList<string> BatteryKeys { get; }
    Task SubscribeAsync(CancellationToken token);
    Task<IReadOnlyList<(string Key, PeripheralBatteryReading Reading)>> ReadFallbackAsync(CancellationToken token);
    void Deactivate();
}

// Only 180F/2A19. A lease never requests/maintains a connection, and is retained
// only while its existing connection and the consumer's demand remain active.
internal sealed class WindowsBasBatteryLease : IWindowsBasBatteryLease
{
    private readonly WindowsPeripheralLifetime _lifetime;
    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService[] _services;
    private readonly GattCharacteristic[] _characteristics;
    private readonly object _sync = new();
    private readonly HashSet<ushort> _subscribed = [];
    private readonly HashSet<ushort> _attempting = [];
    private readonly Dictionary<ushort, int> _earlyValues = [];
    private readonly WindowsBasInitialRead _initialRead = new();
    private volatile bool _active = true;
    private volatile bool _connected;
    private bool _disposed;
    public event Action<string, int>? BatteryChanged;
    public event Action? Invalidated;
    public bool Connected => _active && _connected;
    public bool HasFallback
    {
        get
        {
            lock (_sync) return _characteristics.Any(c => (!_subscribed.Contains(c.AttributeHandle) || !_initialRead.Attempted(Key(c.AttributeHandle))) &&
                c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read));
        }
    }
    public IReadOnlyList<string> BatteryKeys { get; }

    private WindowsBasBatteryLease(WindowsPeripheralLifetime lifetime, BluetoothLEDevice device,
        GattDeviceService[] services, GattCharacteristic[] characteristics)
    {
        _lifetime = lifetime; _device = device; _services = services; _characteristics = characteristics;
        _connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        BatteryKeys = characteristics.Select(c => Key(c.AttributeHandle)).ToArray();
        _device.ConnectionStatusChanged += ConnectionChanged;
        _device.GattServicesChanged += ServicesChanged;
        foreach (var characteristic in characteristics) characteristic.ValueChanged += ValueChanged;
    }

    internal static async Task<IWindowsBasBatteryLease?> OpenAsync(WindowsPeripheralNode node,
        WindowsPeripheralLifetime lifetime, CancellationToken token)
    {
        if (!node.BluetoothLE || node.Connected != true) return null;
        BluetoothLEDevice? device = null;
        GattDeviceService[] services = [];
        var transferred = false;
        try
        {
            device = await lifetime.Await(() => BluetoothLEDevice.FromIdAsync(node.Path), token, d => d?.Dispose()).ConfigureAwait(false);
            if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
            var result = await lifetime.Await(() => device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached),
                token, r => { foreach (var s in r.Services) Release(s.Dispose); }).ConfigureAwait(false);
            services = result.Services.ToArray();
            if (result.Status != GattCommunicationStatus.Success) return null;
            var characteristics = new List<GattCharacteristic>();
            foreach (var service in services.OrderBy(s => s.AttributeHandle))
            {
                token.ThrowIfCancellationRequested();
                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
                var chars = await lifetime.Await(() => service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                    BluetoothCacheMode.Cached), token).ConfigureAwait(false);
                if (chars.Status == GattCommunicationStatus.Success) characteristics.AddRange(chars.Characteristics);
            }
            token.ThrowIfCancellationRequested();
            if (characteristics.Count == 0 || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
            var lease = new WindowsBasBatteryLease(lifetime, device, services,
                characteristics.OrderBy(c => c.AttributeHandle).ToArray());
            transferred = true;
            return lease;
        }
        finally
        {
            if (!transferred)
            {
                var retainedDevice = device;
                var retainedServices = services;
                _ = lifetime.Retire(() =>
                {
                    foreach (var service in retainedServices) Release(service.Dispose);
                    if (retainedDevice is not null) Release(retainedDevice.Dispose);
                });
            }
        }
    }

    public async Task SubscribeAsync(CancellationToken token)
    {
        foreach (var characteristic in _characteristics)
        {
            token.ThrowIfCancellationRequested();
            if (!Connected || _device.ConnectionStatus != BluetoothConnectionStatus.Connected) return;
            var properties = characteristic.CharacteristicProperties;
            var mode = properties.HasFlag(GattCharacteristicProperties.Notify) ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : properties.HasFlag(GattCharacteristicProperties.Indicate) ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : GattClientCharacteristicConfigurationDescriptorValue.None;
            if (mode == GattClientCharacteristicConfigurationDescriptorValue.None) continue;
            lock (_sync)
            {
                if (_subscribed.Contains(characteristic.AttributeHandle)) continue;
                _attempting.Add(characteristic.AttributeHandle);
            }
            try
            {
                // Recheck the existing connection immediately before writing CCCD only.
                var status = await _lifetime.Await(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!Connected || _device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                        throw new OperationCanceledException(token);
                    return characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode);
                }, token, late => CompleteSubscription(characteristic.AttributeHandle, late)).ConfigureAwait(false);
                CompleteSubscription(characteristic.AttributeHandle, status);
                if (status == GattCommunicationStatus.Success)
                    await SeedAsync(characteristic, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }
            catch (Exception ex) when (WindowsPeripheralBatteryProvider.Recoverable(ex)) { }
            finally
            {
                lock (_sync) { _attempting.Remove(characteristic.AttributeHandle); _earlyValues.Remove(characteristic.AttributeHandle); }
            }
        }
    }

    private void CompleteSubscription(ushort handle, GattCommunicationStatus status)
    {
        int? early = null;
        lock (_sync)
        {
            if (status != GattCommunicationStatus.Success || !Connected) return;
            _subscribed.Add(handle);
            if (_earlyValues.TryGetValue(handle, out var value)) early = value;
        }
        if (early.HasValue) BatteryChanged?.Invoke(Key(handle), early.Value);
    }

    public async Task<IReadOnlyList<(string Key, PeripheralBatteryReading Reading)>> ReadFallbackAsync(CancellationToken token)
    {
        var readings = new List<(string, PeripheralBatteryReading)>();
        foreach (var characteristic in _characteristics)
        {
            token.ThrowIfCancellationRequested();
            // Also covers a CCCD acknowledgement that completed after cancellation.
            await SeedAsync(characteristic, token).ConfigureAwait(false);
            lock (_sync) if (_subscribed.Contains(characteristic.AttributeHandle)) continue;
            if (!Connected || _device.ConnectionStatus != BluetoothConnectionStatus.Connected) break;
            if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) continue;
            var result = await _lifetime.Await(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!Connected || _device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    throw new OperationCanceledException(token);
                return characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            }, token).ConfigureAwait(false);
            var level = result.Status == GattCommunicationStatus.Success ? Percentage(result.Value) : null;
            if (level.HasValue) readings.Add((Key(characteristic.AttributeHandle), new() { Percentage = level }));
        }
        return readings;
    }

    private Task SeedAsync(GattCharacteristic characteristic, CancellationToken token)
    {
        bool subscribed;
        lock (_sync) subscribed = _subscribed.Contains(characteristic.AttributeHandle);
        return _initialRead.SeedAsync(Key(characteristic.AttributeHandle),
            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read), subscribed,
            async readToken =>
            {
                var result = await _lifetime.Await(() =>
                {
                    readToken.ThrowIfCancellationRequested();
                    if (!Connected || _device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                        throw new OperationCanceledException(readToken);
                    return characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                }, readToken).ConfigureAwait(false);
                return result.Status == GattCommunicationStatus.Success ? Percentage(result.Value) : null;
            }, level => { if (Connected) BatteryChanged?.Invoke(Key(characteristic.AttributeHandle), level); }, token);
    }

    private static string Key(ushort handle) => handle.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
    private static int? Percentage(Windows.Storage.Streams.IBuffer? value)
    {
        if (value is null || value.Length != 1) return null;
        using var reader = DataReader.FromBuffer(value);
        var level = reader.ReadByte();
        return level <= 100 ? level : null;
    }

    private void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (!Connected) return;
        try
        {
            var level = Percentage(args.CharacteristicValue);
            if (!level.HasValue) return;
            _initialRead.Notified(Key(sender.AttributeHandle));
            lock (_sync)
            {
                if (!_subscribed.Contains(sender.AttributeHandle))
                {
                    if (_attempting.Contains(sender.AttributeHandle)) _earlyValues[sender.AttributeHandle] = level.Value;
                    return;
                }
            }
            BatteryChanged?.Invoke(Key(sender.AttributeHandle), level.Value);
        }
        catch (Exception ex) when (WindowsPeripheralBatteryProvider.Recoverable(ex)) { }
    }

    private void ConnectionChanged(BluetoothLEDevice sender, object args)
    {
        _connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        if (!_connected && _active) Invalidated?.Invoke();
    }
    private void ServicesChanged(BluetoothLEDevice sender, object args) { if (_active) Invalidated?.Invoke(); }

    public void Deactivate() { _active = false; BatteryChanged = null; Invalidated = null; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        Release(() => _device.ConnectionStatusChanged -= ConnectionChanged);
        Release(() => _device.GattServicesChanged -= ServicesChanged);
        foreach (var characteristic in _characteristics) Release(() => characteristic.ValueChanged -= ValueChanged);
        // Releasing our client registration closes our subscription. Never send an
        // unsubscribe write to a disconnected/sleeping device during cleanup.
        foreach (var service in _services) Release(service.Dispose);
        Release(_device.Dispose);
        Array.Clear(_characteristics);
        Array.Clear(_services);
    }

    private static void Release(Action release) { try { release(); } catch { } }
}

// One initial read per readable subscribed attribute, with a notification generation
// check so a delayed seed cannot overwrite a notification received during the read.
internal sealed class WindowsBasInitialRead
{
    private readonly object _sync = new();
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

    internal bool Attempted(string key) { lock (_sync) return _attempted.Contains(key); }
    internal void Notified(string key) { lock (_sync) _versions[key] = _versions.GetValueOrDefault(key) + 1; }

    internal async Task SeedAsync(string key, bool readable, bool subscribed,
        Func<CancellationToken, Task<int?>> read, Action<int> publish, CancellationToken token)
    {
        long version;
        lock (_sync)
        {
            if (!readable || !subscribed || !_attempted.Add(key)) return;
            version = _versions.GetValueOrDefault(key);
        }
        var percentage = await read(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_sync)
            if (percentage is >= 0 and <= 100 && version == _versions.GetValueOrDefault(key)) publish(percentage.Value);
    }
}
