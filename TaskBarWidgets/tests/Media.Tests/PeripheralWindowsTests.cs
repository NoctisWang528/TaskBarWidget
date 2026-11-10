using TaskBarWidgets.Media.Peripherals;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class PeripheralWindowsTests
{
    [Fact]
    public void CatalogPropertiesAreAcceptedByWindows()
    {
        // The former IsPresent spelling aborted the entire provider before its first event.
        var watcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(
            "System.Devices.Present:=System.StructuredQueryType.Boolean#True", WindowsPeripheralCatalog.Properties,
            Windows.Devices.Enumeration.DeviceInformationKind.Device);
        Assert.Equal(Windows.Devices.Enumeration.DeviceWatcherStatus.Created, watcher.Status);
    }

    [Fact]
    public async Task DiscoveryHidesInputCollectionsAndPhoneEndpointsWithoutHidingBatteryDevices()
    {
        var battery = Node("battery", battery: (byte)0);
        var generic = Node("generic", Guid.NewGuid()) with { Kind = "keyboard", Name = "HID Keyboard Device" };
        var phone = Node("phone", Guid.NewGuid(), battery: (byte)76) with { Kind = "phone", Bluetooth = true };
        var phoneBle = phone with { Path = "phone-bas", Kind = "unknown", BatteryService = true, BluetoothLE = true, Pnp = false };
        var catalog = new Catalog { Nodes = [battery, generic, phone, phoneBle] };
        await using var provider = new WindowsPeripheralBatteryProvider(catalog);
        var found = Assert.Single(await provider.DiscoverAsync(default));
        Assert.Equal(PeripheralIdentity.FromKey("container:" + Container.ToString("D")), found.Id);
        Assert.True(found.CanRead);
    }

    private static readonly Guid Container = Guid.Parse("a640316e-35ed-49b3-aad5-be7f366c3741");
    private static WindowsPeripheralNode Node(string path = "node", Guid? container = null, string? address = null,
        object? battery = null, string pair = "046d:0ac4") => new(path, "Same display name", container ?? Container,
        address, @"HID\VID_" + pair[..4] + "&PID_" + pair[5..] + @"\INSTANCE", [], "headset", true,
        null, false, false, battery);

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "aabbccddeeff")]
    [InlineData("aa-bb-cc-dd-ee-ff", "aabbccddeeff")]
    [InlineData("000000000000", null)]
    [InlineData("same name", null)]
    public void AddressNormalizationRejectsInventedIdentities(string input, string? expected) =>
        Assert.Equal(expected, WindowsPeripheralIdentity.Address(input));

    [Fact]
    public void ContainerSiblingsUseOneBluetoothIdentity()
    {
        var pnp = Node("pnp");
        var aep = Node("aep", address: "AA:BB:CC:DD:EE:FF") with { Bluetooth = true, BluetoothLE = true, Pnp = false };
        var nodes = new[] { pnp, aep };
        Assert.Equal("bluetooth:aabbccddeeff", WindowsPeripheralIdentity.Key(pnp, nodes));
        Assert.Equal(WindowsPeripheralIdentity.Key(aep, nodes), WindowsPeripheralIdentity.Key(pnp, nodes));
        Assert.Null(WindowsPeripheralIdentity.Key(Node(container: Guid.Empty), []));
        Assert.NotEqual(WindowsPeripheralIdentity.Key(pnp, [pnp]),
            WindowsPeripheralIdentity.Key(pnp with { Container = Guid.NewGuid() }, []));
    }

    [Fact]
    public void ConflictingContainerAddressesAreNotMergedAsBluetooth()
    {
        var pnp = Node();
        var nodes = new[] { pnp, Node("a", address: "112233445566"), Node("b", address: "aabbccddeeff") };
        Assert.Equal("container:" + Container.ToString("D"), WindowsPeripheralIdentity.Key(pnp, nodes));
    }

    [Fact]
    public void UnknownAndOutOfRangeBatteryAreNeverZero()
    {
        Assert.Equal(0, WindowsPeripheralIdentity.Percentage((byte)0));
        Assert.Null(WindowsPeripheralIdentity.Percentage((byte)255));
        Assert.Null(WindowsPeripheralIdentity.Percentage(-1));
        Assert.Null(WindowsPeripheralIdentity.Percentage("80"));
        Assert.Null(WindowsPeripheralIdentity.Capacity(null, null, null));
        Assert.Null(WindowsPeripheralIdentity.Capacity(3, 0, false));
        Assert.Null(WindowsPeripheralIdentity.Capacity(101, 100, null));
        Assert.Null(WindowsPeripheralIdentity.Capacity(null, null, true)!.Percentage);
    }

    [Fact]
    public void MultipleBatteryLabelsFollowAttributesRatherThanEnumerationOrder()
    {
        var readings = WindowsPeripheralIdentity.Roles([
            ("00002", new PeripheralBatteryReading { Percentage = 90 }),
            ("00001", new PeripheralBatteryReading { Percentage = 20 })]);
        Assert.Equal(["battery1", "battery2"], readings.Select(r => r.Role).ToArray());
        Assert.Equal([20, 90], readings.Select(r => r.Percentage!.Value).ToArray());
        Assert.Equal("left", Assert.Single(WindowsPeripheralIdentity.Roles([
            ("attribute", new PeripheralBatteryReading { Role = "left", Percentage = 42 })])).Role);
    }

    [Fact]
    public void WatcherFilteringKeepsUsbHidAndBluetoothAndIgnoresUnrelatedUpdates()
    {
        var usb = Node() with { InstanceId = @"USB\VID_046D&PID_0AC4\instance", Kind = "unknown" };
        Assert.True(WindowsPeripheralCatalog.Relevant(usb));
        Assert.True(WindowsPeripheralCatalog.Relevant(Node() with { Bluetooth = true }));
        Assert.True(WindowsPeripheralCatalog.Relevant(Node() with { Pnp = false }));
        Assert.False(WindowsPeripheralCatalog.Relevant(Node() with { Kind = "unknown", InstanceId = @"PCI\system" }));
        Assert.True(WindowsPeripheralCatalog.Same(usb with { HardwareIds = ["USB\\VID_046D"] },
            usb with { HardwareIds = ["USB\\VID_046D"] }));
        Assert.False(WindowsPeripheralCatalog.Same(usb, usb with { Battery = (byte)20 }));
    }

    [Fact]
    public void PartialBatteryFailureDoesNotRelabelAnotherBattery()
    {
        var at = DateTimeOffset.UtcNow;
        var device = WindowsPeripheralBatteryProvider.Apply(new(), [
            ("attribute1", new PeripheralBatteryReading()),
            ("attribute2", new PeripheralBatteryReading { Percentage = 50 })], "bluetooth-bas", at);
        Assert.Equal("battery1", device.Batteries[0].Role);
        Assert.Null(device.Batteries[0].Percentage);
        Assert.Equal("battery2", device.Batteries[1].Role);
        Assert.Equal(50, device.Batteries[1].Percentage);
        Assert.Equal(at, device.UpdatedAt);
    }

    [Fact]
    public void FailedReadsKeepTimestampAndLastValue()
    {
        var at = DateTimeOffset.Parse("2026-11-01T10:00:00Z");
        var previous = new PeripheralDeviceSnapshot { Id = "known", UpdatedAt = at, Batteries = [new() { Percentage = 68 }] };
        var failed = WindowsPeripheralBatteryProvider.Apply(previous, [], "windows-pnp", at.AddHours(1));
        Assert.Equal(at, failed.UpdatedAt);
        Assert.Equal(68, Assert.Single(failed.Batteries).Percentage);
        Assert.True(failed.Stale);
    }

    [Fact]
    public async Task DiscoveryDoesNotReadAndRemovalRetainsDisconnectedDevice()
    {
        var catalog = new Catalog { Nodes = [Node(battery: (byte)72)] };
        var calls = 0;
        await using var provider = new WindowsPeripheralBatteryProvider(catalog,
            (_, _) => { calls++; return Task.FromResult<int?>(72); });
        var discovered = Assert.Single(await provider.DiscoverAsync(default));
        Assert.Equal(0, calls);
        Assert.Null(discovered.UpdatedAt);
        var read = Assert.Single(await provider.ReadAsync(new HashSet<string> { discovered.Id }, new(), default));
        Assert.Equal(1, calls);
        Assert.Equal(72, Assert.Single(read.Batteries).Percentage);
        catalog.Nodes = [];
        var disconnected = Assert.Single(await provider.ReadAsync(new HashSet<string> { discovered.Id }, new(), default));
        Assert.Equal("disconnected", disconnected.State);
        Assert.Equal(read.UpdatedAt, disconnected.UpdatedAt);
        Assert.True(disconnected.Stale);
    }

    [Fact]
    public async Task ChangesAreForwardedAndDisposeCancelsAndDrainsRead()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var catalog = new Catalog { Nodes = [Node(battery: (byte)50)] };
        var provider = new WindowsPeripheralBatteryProvider(catalog, async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return 50; }
            finally { stopped = true; }
        });
        var changes = 0;
        provider.DevicesChanged += () => changes++;
        catalog.Signal();
        Assert.Equal(1, changes);
        var id = Assert.Single(await provider.DiscoverAsync(default)).Id;
        var read = provider.ReadAsync(new HashSet<string> { id }, new(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(stopped);
        Assert.True(catalog.Stopped);
        catalog.Signal();
        Assert.Equal(1, changes);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await provider.DiscoverAsync(default));
    }

    [Fact]
    public void HeadsetMappingRejectsSameVidPidAcrossContainersAndUnknownContainer()
    {
        var first = Node("a");
        Assert.Single(HeadsetControlDevices.Map([first, first with { Path = "another interface" }]));
        Assert.Empty(HeadsetControlDevices.Map([first, Node("b", Guid.NewGuid())]));
        Assert.Empty(HeadsetControlDevices.Map([first, Node("unknown", Guid.Empty)]));
        Assert.Empty(HeadsetControlDevices.Map([first with { Battery = (byte)70 }]));
        Assert.Empty(HeadsetControlDevices.Map([first, Node("ble") with { BluetoothLE = true }]));
        Assert.Empty(HeadsetControlDevices.Map([Node(pair: "3329:4b19")]));
        Assert.Empty(HeadsetControlDevices.Map([Node(pair: "1038:230a")]));
    }

    [Fact]
    public async Task CancelledPublicReadDoesNotAbandonAnUncooperativeOperationOnDispose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var catalog = new Catalog { Nodes = [Node(battery: (byte)50)] };
        var provider = new WindowsPeripheralBatteryProvider(catalog, async (_, _) =>
        {
            entered.TrySetResult();
            try { return await release.Task; }
            finally { finished = true; }
        }, shutdownBudget: TimeSpan.FromMilliseconds(50));
        var id = Assert.Single(await provider.DiscoverAsync(default)).Id;
        using var cancel = new CancellationTokenSource();
        var read = provider.ReadAsync(new HashSet<string> { id }, new(), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(finished);
        Assert.False(provider.CleanupCompletion.IsCompleted);
        await catalog.StoppedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(catalog.Stopped);
        release.TrySetResult(50);
        await provider.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(finished);
        Assert.True(catalog.Stopped);
    }

    private static WindowsPeripheralNode BasNode(bool connected = true) => Node("ble", address: "11:22:33:44:55:66") with
    { Pnp = false, Bluetooth = true, BluetoothLE = true, Connected = connected };

    [Fact]
    public async Task ReadableSubscribedBasSeedsOnceThenUsesNotificationsWithoutPolling()
    {
        var catalog = new Catalog { Nodes = [BasNode()] };
        var lease = new BasLease();
        var seed = new WindowsBasInitialRead();
        var initialReads = 0;
        lease.Subscribe = token => seed.SeedAsync("00001", true, true, _ =>
        {
            initialReads++;
            return Task.FromResult<int?>(62);
        }, level => lease.Notify("00001", level), token);
        await using var provider = new WindowsPeripheralBatteryProvider(catalog,
            openBas: (_, _, _) => Task.FromResult<IWindowsBasBatteryLease?>(lease));
        var wanted = new HashSet<string> { Assert.Single(await provider.DiscoverAsync(default)).Id };
        var changes = new List<PeripheralDeviceSnapshot>();
        provider.ReadingChanged += changes.Add;
        await provider.SetDemandAsync(wanted, default);
        var seeded = Assert.Single(changes);
        Assert.Equal(62, seeded.Batteries[0].Percentage);
        for (var i = 0; i < 3; i++)
        {
            await provider.SetDemandAsync(wanted, default);
            var cached = Assert.Single(await provider.ReadAsync(wanted, new(), default));
            Assert.Equal(seeded.UpdatedAt, cached.UpdatedAt);
        }
        Assert.Equal(1, initialReads);
        Assert.Equal(0, lease.FallbackReads);
        lease.Notify("00001", 61);
        Assert.Equal(61, changes[^1].Batteries[0].Percentage);
    }

    [Fact]
    public async Task InitialSeedRequiresReadAndSuccessfulSubscriptionAndRejectsAnOlderRead()
    {
        var seed = new WindowsBasInitialRead();
        var reads = 0;
        var published = new List<int>();
        Task<int?> Read(CancellationToken _) { reads++; return Task.FromResult<int?>(50); }
        await seed.SeedAsync("notify-only", false, true, Read, published.Add, default);
        await seed.SeedAsync("unsubscribed", true, false, Read, published.Add, default);
        Assert.Equal(0, reads);
        Assert.Empty(published);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = seed.SeedAsync("read-notify", true, true, _ =>
        {
            reads++; entered.TrySetResult(); return finish.Task;
        }, published.Add, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        seed.Notified("read-notify");
        finish.TrySetResult(10);
        await pending;
        Assert.Empty(published);
        await seed.SeedAsync("read-notify", true, true, Read, published.Add, default);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task WindowsHistoryIsBoundedAndNewPresentDevicesPrecedeDisconnectedHistory()
    {
        var catalog = new Catalog();
        await using var provider = new WindowsPeripheralBatteryProvider(catalog);
        IReadOnlyList<PeripheralDeviceSnapshot> found = [];
        for (var i = 0; i < 270; i++)
        {
            var node = Node("new-" + i, Guid.NewGuid(), battery: (byte)50);
            catalog.Nodes = [node];
            found = await provider.DiscoverAsync(default);
            Assert.InRange(found.Count, 1, 256);
            Assert.Equal(PeripheralIdentity.FromKey("container:" + node.Container.ToString("D")), found[0].Id);
            Assert.Equal("online", found[0].State);
            Assert.All(found.Skip(1), old => Assert.Equal("disconnected", old.State));
        }
        catalog.Nodes = [];
        var history = await provider.DiscoverAsync(default);
        Assert.Equal(256, history.Count);
        Assert.All(history, old => Assert.Equal("disconnected", old.State));
    }

    [Fact]
    public async Task HeadsetHistoryIsBoundedAndDiscoveryDoesNotRunTheToolDuringDeviceChurn()
    {
        var catalog = new Catalog();
        var queries = 0;
        await using var provider = new HeadsetControlProvider(null, catalog, (_, _) =>
        { queries++; return Task.FromResult(""); }, () => true);
        provider.Configure(new() { HeadsetControlEnabled = true });
        for (var i = 0; i < 270; i++)
        {
            var node = Node("headset-" + i, Guid.NewGuid());
            catalog.Nodes = [node];
            var found = await provider.DiscoverAsync(default);
            Assert.InRange(found.Count, 1, 256);
            Assert.Equal(PeripheralIdentity.FromKey("container:" + node.Container.ToString("D")), found[0].Id);
            Assert.Equal("online", found[0].State);
            Assert.All(found.Skip(1), old => Assert.Equal("disconnected", old.State));
        }
        catalog.Nodes = [];
        Assert.Equal(256, (await provider.DiscoverAsync(default)).Count);
        Assert.Equal(0, queries);
    }

    [Fact]
    public async Task BasNotificationsRequireConnectedDemandAndCachedReadsDoNotPollOrRefreshTimestamp()
    {
        var catalog = new Catalog { Nodes = [BasNode(false)] };
        var lease = new BasLease();
        var opens = 0;
        await using var provider = new WindowsPeripheralBatteryProvider(catalog, openBas: (_, _, _) =>
        { opens++; return Task.FromResult<IWindowsBasBatteryLease?>(lease); });
        await provider.SetDemandAsync(new HashSet<string>(), default);
        Assert.Equal(0, catalog.Enumerations);
        var id = Assert.Single(await provider.DiscoverAsync(default)).Id;
        var wanted = new HashSet<string> { id };
        await provider.SetDemandAsync(wanted, default);
        Assert.Equal(0, opens);
        catalog.Nodes = [BasNode()]; catalog.Signal();
        await provider.SetDemandAsync(wanted, default);
        Assert.Equal(1, opens);
        Assert.Equal(1, lease.Subscriptions);
        var notifications = new List<PeripheralDeviceSnapshot>();
        provider.ReadingChanged += notifications.Add;
        lease.Notify("00001", 255);
        lease.Notify("unknown", 50);
        Assert.Empty(notifications);
        lease.Notify("00002", 0);
        var snapshot = Assert.Single(notifications);
        Assert.Equal("bluetooth-bas", snapshot.Source);
        Assert.Equal("battery1", snapshot.Batteries[0].Role);
        Assert.Null(snapshot.Batteries[0].Percentage);
        Assert.Equal("battery2", snapshot.Batteries[1].Role);
        Assert.Equal(0, snapshot.Batteries[1].Percentage);
        var cached = Assert.Single(await provider.ReadAsync(wanted, new(), default));
        Assert.Equal(snapshot.UpdatedAt, cached.UpdatedAt);
        Assert.Equal(0, lease.FallbackReads);
        await provider.SetDemandAsync(wanted, default);
        Assert.Equal(1, lease.Subscriptions);
    }

    [Fact]
    public async Task EmptyDemandDetachesLateEventsAndReenableAcquiresANewLease()
    {
        var catalog = new Catalog { Nodes = [BasNode()] };
        var leases = new List<BasLease>();
        await using var provider = new WindowsPeripheralBatteryProvider(catalog, openBas: (_, _, _) =>
        {
            var lease = new BasLease(); leases.Add(lease);
            return Task.FromResult<IWindowsBasBatteryLease?>(lease);
        });
        var wanted = new HashSet<string> { Assert.Single(await provider.DiscoverAsync(default)).Id };
        var count = 0;
        provider.ReadingChanged += _ => count++;
        await provider.SetDemandAsync(wanted, default);
        var late = leases[0].CapturedNotification;
        await provider.SetDemandAsync(new HashSet<string>(), default);
        Assert.True(leases[0].Deactivated);
        late?.Invoke("00001", 80);
        Assert.Equal(0, count);
        await leases[0].Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.ReadAsync(wanted, new(), default);
        Assert.Equal(0, leases[0].FallbackReads);
        Assert.Single(leases);
        await provider.SetDemandAsync(wanted, default);
        Assert.Equal(2, leases.Count);
        leases[1].Notify("00001", 80);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DisconnectAndRemovalDetachWithoutWaitingForAnotherDemandCycle()
    {
        var catalog = new Catalog { Nodes = [BasNode()] };
        var leases = new List<BasLease>();
        await using var provider = new WindowsPeripheralBatteryProvider(catalog, openBas: (_, _, _) =>
        {
            var lease = new BasLease(); leases.Add(lease);
            return Task.FromResult<IWindowsBasBatteryLease?>(lease);
        });
        var wanted = new HashSet<string> { Assert.Single(await provider.DiscoverAsync(default)).Id };
        var notifications = new List<PeripheralDeviceSnapshot>();
        provider.ReadingChanged += notifications.Add;
        await provider.SetDemandAsync(wanted, default);
        leases[0].Notify("00001", 70);
        var timestamp = notifications[^1].UpdatedAt;
        catalog.Nodes = [BasNode(false)];
        leases[0].Disconnect();
        Assert.True(leases[0].Deactivated);
        Assert.Equal("disconnected", notifications[^1].State);
        Assert.Equal(timestamp, notifications[^1].UpdatedAt);
        await provider.SetDemandAsync(wanted, default);
        Assert.Single(leases);
        catalog.Nodes = [BasNode()];
        await provider.SetDemandAsync(wanted, default);
        Assert.Equal(2, leases.Count);
        catalog.Nodes = []; catalog.Signal();
        Assert.True(leases[1].Deactivated);
        await leases[1].Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("disconnected", notifications[^1].State);
    }

    [Fact]
    public async Task NonNotifyingBatteryFallsBackToReadWithoutLosingOtherBatteryRoles()
    {
        var catalog = new Catalog { Nodes = [BasNode()] };
        var lease = new BasLease { HasFallback = true };
        await using var provider = new WindowsPeripheralBatteryProvider(catalog,
            openBas: (_, _, _) => Task.FromResult<IWindowsBasBatteryLease?>(lease));
        var wanted = new HashSet<string> { Assert.Single(await provider.DiscoverAsync(default)).Id };
        await provider.SetDemandAsync(wanted, default);
        lease.Notify("00001", 90);
        var read = Assert.Single(await provider.ReadAsync(wanted, new(), default));
        Assert.Equal(1, lease.FallbackReads);
        Assert.Equal([90, 30], read.Batteries.Select(b => b.Percentage!.Value).ToArray());
        Assert.Equal(["battery1", "battery2"], read.Batteries.Select(b => b.Role).ToArray());
    }

    [Fact]
    public async Task IgnoredSubscriptionCancellationQuarantinesOneOwnerAndShutdownReturnsBeforeLateCleanup()
    {
        var catalog = new Catalog { Nodes = [BasNode()] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new BasLease { Subscribe = async _ => { entered.TrySetResult(); await release.Task; } };
        var opens = 0;
        var provider = new WindowsPeripheralBatteryProvider(catalog, openBas: (_, _, _) =>
        { opens++; return Task.FromResult<IWindowsBasBatteryLease?>(lease); }, shutdownBudget: TimeSpan.FromMilliseconds(50));
        var wanted = new HashSet<string> { Assert.Single(await provider.DiscoverAsync(default)).Id };
        using var cancel = new CancellationTokenSource();
        var demand = provider.SetDemandAsync(wanted, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var late = lease.CapturedNotification;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => demand);
        await Assert.ThrowsAsync<TimeoutException>(async () => await provider.DiscoverAsync(default));
        await provider.SetDemandAsync(new HashSet<string>(), default);
        var events = 0;
        provider.ReadingChanged += _ => events++;
        late?.Invoke("00001", 77);
        Assert.Equal(0, events);
        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await catalog.StoppedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(catalog.Stopped);
        Assert.False(lease.Released.Task.IsCompleted);
        Assert.False(provider.CleanupCompletion.IsCompleted);
        Assert.Equal(1, opens);
        release.TrySetResult();
        await provider.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(lease.Released.Task.IsCompleted);
        Assert.Equal(1, lease.Disposals);
    }

    private sealed class BasLease : IWindowsBasBatteryLease
    {
        public event Action<string, int>? BatteryChanged;
        public event Action? Invalidated;
        public bool Connected { get; private set; } = true;
        public bool HasFallback { get; set; }
        public IReadOnlyList<string> BatteryKeys => ["00001", "00002"];
        internal bool Deactivated;
        internal int Subscriptions, FallbackReads, Disposals;
        internal Func<CancellationToken, Task>? Subscribe;
        internal TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<string, int>? CapturedNotification => BatteryChanged;
        internal void Notify(string key, int level) => BatteryChanged?.Invoke(key, level);
        internal void Disconnect() { Connected = false; Invalidated?.Invoke(); }
        public Task SubscribeAsync(CancellationToken token) { Subscriptions++; return Subscribe?.Invoke(token) ?? Task.CompletedTask; }
        public Task<IReadOnlyList<(string Key, PeripheralBatteryReading Reading)>> ReadFallbackAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); FallbackReads++;
            return Task.FromResult<IReadOnlyList<(string, PeripheralBatteryReading)>>([("00002", new() { Percentage = 30 })]);
        }
        public void Deactivate() { Deactivated = true; BatteryChanged = null; Invalidated = null; }
        public void Dispose() { Disposals++; Deactivate(); Released.TrySetResult(); }
    }

    private static string Output(string status, int level = 0, string pair = "046d:0ac4") =>
        $"Found 1 supported device(s):\n Headset [0x{pair[..4]}:0x{pair[5..]}]\nBattery:\n\tStatus: {status}\n\tLevel: {level}%\n";

    [Fact]
    public void HeadsetParserPreservesUnknownChargingDiscreteAndEstimatedValues()
    {
        var voltage = HeadsetControlDevices.Reading(Output("BATTERY_AVAILABLE", 73), "046d:0ac4", out var disconnected);
        Assert.False(disconnected);
        Assert.Equal(73, voltage!.Percentage);
        Assert.True(voltage.Estimated);
        var charging = HeadsetControlDevices.Reading(Output("BATTERY_CHARGING", -1), "046d:0ac4", out _);
        Assert.True(charging!.Charging);
        Assert.Null(charging.Percentage);
        Assert.Null(HeadsetControlDevices.Reading(Output("BATTERY_UNAVAILABLE"), "046d:0ac4", out disconnected));
        Assert.True(disconnected);
        var coarse = HeadsetControlDevices.Reading(Output("BATTERY_AVAILABLE", 50, "1038:2202"), "1038:2202", out _);
        Assert.Null(coarse!.Percentage);
        Assert.Equal("2/4", coarse.LevelText);
        Assert.Null(HeadsetControlDevices.Reading(Output("BATTERY_AVAILABLE", 101), "046d:0ac4", out _));
        Assert.Null(HeadsetControlDevices.Reading(Output("BATTERY_AVAILABLE", 70), "046d:0a66", out _));
        Assert.Null(HeadsetControlDevices.Reading("{}", "046d:0ac4", out _));
        Assert.Equal(["--device", "046d:0ac4", "--battery", "--output", "standard"], HeadsetControlProvider.Arguments("046d:0ac4"));
    }

    [Fact]
    public async Task HeadsetIsDefaultOffAndDiscoveryNeverInvokesTool()
    {
        var catalog = new Catalog { Nodes = [Node()] };
        var queries = 0;
        await using var provider = new HeadsetControlProvider(null, catalog, (_, _) =>
        {
            queries++;
            return Task.FromResult(Output("BATTERY_AVAILABLE", 80));
        }, () => true);
        Assert.Empty(await provider.DiscoverAsync(default));
        Assert.Equal(0, catalog.Enumerations);
        var settings = new PeripheralBatterySettings { HeadsetControlEnabled = true };
        provider.Configure(settings);
        var device = Assert.Single(await provider.DiscoverAsync(default));
        Assert.Equal(0, queries);
        var read = Assert.Single(await provider.ReadAsync(new HashSet<string> { device.Id }, settings, default));
        Assert.Equal(1, queries);
        Assert.Equal(80, Assert.Single(read.Batteries).Percentage);
        provider.Configure(new());
        Assert.Empty(await provider.DiscoverAsync(default));
    }

    [Fact]
    public async Task ArrivalDuringToolQueryInvalidatesAttribution()
    {
        var catalog = new Catalog { Nodes = [Node()] };
        await using var provider = new HeadsetControlProvider(null, catalog, (_, _) =>
        {
            catalog.Nodes = [Node(), Node("other headset", Guid.NewGuid())];
            return Task.FromResult(Output("BATTERY_AVAILABLE", 80));
        }, () => true);
        var settings = new PeripheralBatterySettings { HeadsetControlEnabled = true };
        provider.Configure(settings);
        var id = Assert.Single(await provider.DiscoverAsync(default)).Id;
        var read = Assert.Single(await provider.ReadAsync(new HashSet<string> { id }, settings, default));
        Assert.Null(read.UpdatedAt);
        Assert.Empty(read.Batteries);
        Assert.Equal("unavailable", read.State);
    }

    private sealed class Catalog : IWindowsPeripheralCatalog
    {
        internal IReadOnlyList<WindowsPeripheralNode> Nodes { get; set; } = [];
        public IReadOnlyList<WindowsPeripheralNode> Snapshot => Nodes;
        internal int Enumerations { get; private set; }
        internal bool Stopped { get; private set; }
        internal TaskCompletionSource StoppedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action? Changed;
        internal void Signal() => Changed?.Invoke();
        public Task<IReadOnlyList<WindowsPeripheralNode>> GetAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Enumerations++;
            return Task.FromResult(Nodes);
        }
        public Task StopAsync() { Stopped = true; StoppedCompletion.TrySetResult(); return Task.CompletedTask; }
    }
}
