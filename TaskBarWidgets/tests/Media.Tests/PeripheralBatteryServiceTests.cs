using System.IO;
using System.Text;
using System.Text.Json;
using TaskbarLyrics.App;
using TaskBarWidgets.Media.Peripherals;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class PeripheralBatteryServiceTests
{
    private static readonly string Id = PeripheralIdentity.FromKey("test:mouse:serial1");
    private static PeripheralDeviceSnapshot Device(int? percent = null) => new()
    {
        Id = Id, Name = "鼠标", Kind = "mouse", State = "online", CanRead = true,
        Batteries = percent.HasValue ? [new() { Role = "main", Percentage = percent }] : [],
        UpdatedAt = percent.HasValue ? DateTimeOffset.UtcNow : null
    };

    [Fact]
    public async Task ProvenLegacyIdentityRetainsTaskbarSelectionAndCardOptOut()
    {
        var provider = new FakeProvider
        {
            DiscoveryResult = Device() with { Id = PeripheralIdentity.FromKey("corrected:usb-serial"), LegacyIds = [Id] },
            Result = Device(64)
        };
        await using var service = new PeripheralBatteryService([provider], null);
        var settings = new PeripheralBatterySettings { CardVisibility = new() { [Id] = false }, Order = [Id] };
        service.Configure(settings, false, false, false);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => service.Snapshot.Devices.SingleOrDefault()?.Batteries.FirstOrDefault()?.Percentage == 64 && !service.Snapshot.Refreshing);
        var device = Assert.Single(service.Snapshot.Devices);
        Assert.Equal(Id, device.Id);
        Assert.False(device.ShowInCard);
        provider.Notify();
        await Until(() => provider.DiscoveryCalls >= 2 && !service.Snapshot.Refreshing);
        Assert.Equal(Id, Assert.Single(service.Snapshot.Devices).Id);
    }

    [Fact]
    public async Task UnreadableIdentityOnlyRowsAreHiddenUnlessExplicitlySelected()
    {
        var provider = new FakeProvider { Result = Device() with { Name = "HID Keyboard Device", CanRead = false } };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetSettingsActive(true); service.Start();
        await Until(() => provider.DiscoveryCalls > 0 && !service.Snapshot.Refreshing);
        Assert.Empty(service.Snapshot.Devices);
        service.Configure(new() { CardVisibility = new() { [Id] = true } }, true, false, false);
        Assert.Single(service.Snapshot.Devices);
        service.Configure(new() { CardVisibility = new() { [Id] = false } }, true, false, false);
        Assert.Empty(service.Snapshot.Devices);
    }

    [Fact]
    public async Task ZeroIsValidAndFailedReadsKeepTheLastValueWithoutDeclaringDisconnect()
    {
        var provider = new FakeProvider { Result = Device(0) };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetSettingsActive(true); service.Start();
        await Until(() => service.Snapshot.Devices.SingleOrDefault()?.Batteries.FirstOrDefault()?.Percentage == 0 && !service.Snapshot.Refreshing);
        Assert.True(service.Snapshot.Devices[0].ShowInCard);
        var lastSuccess = service.Snapshot.Devices[0].UpdatedAt;
        provider.FailRead = true; service.Refresh();
        await Until(() => service.Snapshot.Devices[0].Stale && !service.Snapshot.Refreshing);
        Assert.Equal("unavailable", service.Snapshot.Devices[0].State);
        Assert.Equal(0, service.Snapshot.Devices[0].Batteries[0].Percentage);
        Assert.Equal(lastSuccess, service.Snapshot.Devices[0].UpdatedAt);
    }

    [Fact]
    public async Task HidppRediscoveryKeepsTheProvenModelNameAndLastReading()
    {
        var named = Device(25) with { Name = "G502 X LIGHTSPEED", Source = "hidpp20" };
        var provider = new FakeProvider { DiscoveryResult = named, Result = named };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetSettingsActive(true); service.Start();
        await Until(() => service.Snapshot.Devices.SingleOrDefault()?.Name == named.Name && !service.Snapshot.Refreshing);
        provider.DiscoveryResult = provider.Result = Device() with
            { Name = "Logitech HID++ mouse", Source = "hidpp20", State = "unavailable" };
        provider.Notify();
        await Until(() => provider.DiscoveryCalls >= 2 && !service.Snapshot.Refreshing);
        var retained = Assert.Single(service.Snapshot.Devices);
        Assert.Equal(named.Name, retained.Name);
        Assert.Equal(25, Assert.Single(retained.Batteries).Percentage);
        Assert.Equal(named.UpdatedAt, retained.UpdatedAt);
        provider.Notify(named with { Name = "G502 X PLUS", UpdatedAt = DateTimeOffset.UtcNow });
        await Until(() => service.Snapshot.Devices[0].Name == "G502 X PLUS");
    }

    [Fact]
    public async Task CardOptOutDoesNotPreventIndependentTaskbarDemandOrForgetItsChoice()
    {
        var provider = new FakeProvider();
        await using var service = new PeripheralBatteryService([provider], null);
        var settings = new PeripheralBatterySettings { CardVisibility = new() { [Id] = false } };
        service.Configure(settings, true, true, true); service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => provider.ReadCalls != 0 && !service.Snapshot.Refreshing);
        Assert.False(service.Snapshot.Devices[0].ShowInCard);
        var calls = provider.ReadCalls;
        service.SetTaskbarDemand([]); service.Refresh();
        await Until(() => provider.DiscoveryCalls >= 2 && !service.Snapshot.Refreshing);
        Assert.Equal(calls, provider.ReadCalls);
        provider.Present = false; provider.Notify();
        await Until(() => service.Snapshot.Devices[0].State == "disconnected");
        provider.Present = true; provider.Notify();
        await Until(() => service.Snapshot.Devices[0].State == "online");
        Assert.False(service.Snapshot.Devices[0].ShowInCard);
    }

    [Fact]
    public async Task FailedDiscoveryDoesNotRemovePreviouslyConnectedDevices()
    {
        var provider = new FakeProvider();
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetSettingsActive(true); service.Start();
        await Until(() => service.Snapshot.Devices.Length == 1 && !service.Snapshot.Refreshing);
        provider.FailDiscovery = true; provider.Notify();
        await Until(() => service.Snapshot.Error.Length != 0 && !service.Snapshot.Refreshing);
        Assert.Equal("online", service.Snapshot.Devices[0].State);
        Assert.Equal(75, service.Snapshot.Devices[0].Batteries[0].Percentage);
    }

    [Fact]
    public async Task ConcurrentRefreshesCoalesceAndShutdownCancelsTheOwnedQuery()
    {
        var provider = new FakeProvider { BlockRead = true };
        var service = new PeripheralBatteryService([provider], null);
        service.SetSettingsActive(true); service.Start();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50; i++) service.Refresh();
        Assert.Equal(1, provider.ReadCalls);
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(provider.Disposed);
        Assert.True(provider.ReadCancelled);
    }

    [Fact]
    public async Task MultipleBatteryRolesAndUnknownChargingRemainSeparateOnTheWire()
    {
        var provider = new FakeProvider { Result = Device() with { Batteries =
            [new() { Role = "left", Percentage = 0, Charging = null }, new() { Role = "right", Percentage = 80, Estimated = true }] } };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetSettingsActive(true); service.Start();
        await Until(() => service.Snapshot.Devices.SingleOrDefault()?.Batteries.Length == 2 && !service.Snapshot.Refreshing);
        var wire = JsonSerializer.SerializeToElement(service.Snapshot);
        var batteries = wire.GetProperty("devices")[0].GetProperty("batteries");
        Assert.Equal(0, batteries[0].GetProperty("percentage").GetInt32());
        Assert.Equal(JsonValueKind.Null, batteries[0].GetProperty("charging").ValueKind);
        Assert.True(batteries[1].GetProperty("estimated").GetBoolean());
    }

    [Fact]
    public async Task VisibleOrTaskbarDemandUsesSixtySecondFreshnessAfterBackgroundPolling()
    {
        var provider = new FakeProvider(); var clock = new ManualClock();
        await using var service = new PeripheralBatteryService([provider], null, clock);
        service.Configure(new(), true, false, true); service.Start();
        await Until(() => provider.ReadCalls == 1 && !service.Snapshot.Refreshing);
        clock.Utc = clock.Utc.AddMinutes(2);
        service.SetTaskbarDemand([Id]);
        await Until(() => provider.ReadCalls == 2 && !service.Snapshot.Refreshing);
    }

    [Fact]
    public async Task RemovingAllDemandCancelsAnInFlightQueryAndCanResumeLater()
    {
        var provider = new FakeProvider { BlockRead = true };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetSettingsActive(true); service.Start();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.SetSettingsActive(false);
        await Until(() => provider.ReadCancelled && !service.Snapshot.Refreshing);
        provider.BlockRead = false;
        service.SetTaskbarDemand([Id]);
        await Until(() => service.Snapshot.Devices[0].Batteries.Length == 1 && !service.Snapshot.Refreshing);
    }

    [Fact]
    public async Task CacheRestoresDisconnectedIdentityBeforeLateDiscovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json");
        try
        {
            await using (var service = new PeripheralBatteryService([new FakeProvider()], cache))
            {
                service.SetSettingsActive(true); service.Start();
                await Until(() => service.Snapshot.Devices.SingleOrDefault()?.Batteries.Length == 1 && !service.Snapshot.Refreshing);
            }
            var provider = new FakeProvider();
            await using var restored = new PeripheralBatteryService([provider], cache);
            restored.Start();
            await Until(() => restored.Snapshot.Devices.Length == 1);
            Assert.Equal("disconnected", restored.Snapshot.Devices[0].State);
            Assert.True(restored.Snapshot.Devices[0].Stale);
            Assert.Equal(0, provider.DiscoveryCalls);
            restored.SetTaskbarDemand([Id]);
            await Until(() => restored.Snapshot.Devices[0].State == "online" && !restored.Snapshot.Refreshing);
            Assert.Equal(Id, restored.Snapshot.Devices[0].Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SettingsMigrationAppendsRightCardAndKeepsExistingPositionsAndExplicitChoices()
    {
        var settings = new AppSettings { QuickCardOrder = ["media", "traffic", "weather", "device"],
            QuickCardColumns = new() { ["media"] = 0, ["traffic"] = 0, ["weather"] = 1, ["device"] = 1 },
            PeripheralBattery = new() { Mode = "detailed", CardVisibility = new() { [Id] = false }, Order = [Id] } };
        settings.NormalizeQuickCards();
        Assert.Equal(new[] { "media", "traffic", "weather", "device", "peripheralBattery" }, settings.QuickCardOrder);
        Assert.Equal(1, settings.QuickCardColumns["peripheralBattery"]);
        var clone = settings.Clone(); clone.PeripheralBattery.CardVisibility[Id] = true;
        Assert.False(settings.PeripheralBattery.CardVisibility[Id]);
        using var adapter = MediaSettingsServiceTestFactory.Create(settings, saveSettings: _ => true, getDisplays: () => [],
            getPrimaryDpiScale: () => 1, getWorkAreaWidth: () => 1920);
        var output = adapter.ApplyServiceSettings(settings, JsonSerializer.SerializeToElement(new { peripheralBattery = new
        { mode = "compact", cardVisibility = new Dictionary<string, bool> { [Id] = false }, order = new[] { Id } } }));
        Assert.False(output.GetProperty("peripheralBattery").GetProperty("cardVisibility").GetProperty(Id).GetBoolean());
        Assert.Equal("compact", output.GetProperty("peripheralBattery").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task NotificationsUpdateOnlyDemandedDevicesAndReleaseWhenHidden()
    {
        var provider = new FakeProvider();
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => provider.Demand.Contains(Id) && !service.Snapshot.Refreshing && provider.ReadCalls == 1);
        provider.Notify(Device(26));
        Assert.Equal(26, service.Snapshot.Devices[0].Batteries[0].Percentage);
        Assert.Equal(1, provider.ReadCalls);
        service.SetTaskbarDemand([]);
        await Until(() => provider.Demand.Count == 0);
        provider.Notify(Device(20));
        Assert.Equal(26, service.Snapshot.Devices[0].Batteries[0].Percentage);
    }

    [Theory]
    [InlineData("receiver|unit=12345678|model=001122334455", true)]
    [InlineData("other|unit=12345678|model=001122334455", false)]
    [InlineData("receiver|unit=00000000|model=001122334455", false)]
    [InlineData("receiver|unit=ffffffff|model=001122334455", false)]
    [InlineData("receiver|unit=12345678|model=000000000000", false)]
    [InlineData("receiver|unit=slot1", false)]
    [InlineData("receiver|unit=12345678|model=001122334455|extra=1", false)]
    public void HidppUnitsRequireTheRequestedReceiverAndARealUnitModelIdentity(string identity, bool accepted) =>
        Assert.Equal(accepted, NativePeripheralBatteryProvider.IsHidppChild("receiver", identity));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerNotificationSurvivesAnOlderOrFailedRead(bool failure)
    {
        var provider = new FakeProvider { FinishRead = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Result = Device(40) with { UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, FailRead = failure };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Notify(Device(88));
        provider.FinishRead.SetResult();
        await Until(() => !service.Snapshot.Refreshing);
        Assert.Equal(88, service.Snapshot.Devices[0].Batteries[0].Percentage);
        Assert.False(service.Snapshot.Devices[0].Stale);
        Assert.Equal("online", service.Snapshot.Devices[0].State);
    }

    [Fact]
    public async Task MalformedCacheEntriesCannotStopDiscoveryOrProviderCleanup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json"); var provider = new FakeProvider();
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(cache, "[null,{\"id\":\"" + Id + "\",\"batteries\":null}]");
            await using (var service = new PeripheralBatteryService([provider], cache))
            {
                service.SetSettingsActive(true); service.Start();
                await Until(() => service.Snapshot.Devices.SingleOrDefault()?.Batteries.Length == 1 && !service.Snapshot.Refreshing);
            }
            Assert.True(provider.Disposed);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HistoricalCacheMakesRoomForNewDevicesWithoutEvictingConfiguredIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json");
        var history = Enumerable.Range(0, 256).Select(n => Device() with { Id = PeripheralIdentity.FromKey("old:" + n) }).ToArray();
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(history));
            await using var service = new PeripheralBatteryService([new FakeProvider()], cache);
            service.Configure(new() { Order = [history[0].Id] }, false, false, false); service.Start();
            await Until(() => service.Snapshot.Devices.Length == 256);
            service.SetSettingsActive(true);
            await Until(() => service.Snapshot.Devices.Any(d => d.Id == Id && d.Batteries.Length == 1) && !service.Snapshot.Refreshing);
            Assert.Equal(256, service.Snapshot.Devices.Length);
            Assert.Contains(service.Snapshot.Devices, d => d.Id == history[0].Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ProbeRejectsOversizedOrIncompleteResponses()
    {
        using var oversized = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1) + "\n")));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativePeripheralBatteryProvider.ReadBoundedLineAsync(oversized, default));
        using var incomplete = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("{\"version\":1}")));
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativePeripheralBatteryProvider.ReadBoundedLineAsync(incomplete, default));
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeProvider : IPeripheralBatteryProvider, IPeripheralBatteryNotifications
    {
        public TaskCompletionSource? FinishRead;
        public event Action<PeripheralDeviceSnapshot>? ReadingChanged;
        public IReadOnlySet<string> Demand { get; private set; } = new HashSet<string>();
        public Task SetDemandAsync(IReadOnlySet<string> deviceIds, CancellationToken cancellationToken)
        { Demand = deviceIds.ToHashSet(); return Task.CompletedTask; }
        public void Notify(PeripheralDeviceSnapshot reading) => ReadingChanged?.Invoke(reading);
        public event Action? DevicesChanged;
        public volatile int DiscoveryCalls, ReadCalls;
        public bool Present = true, FailRead, FailDiscovery, BlockRead, Disposed, ReadCancelled;
        public PeripheralDeviceSnapshot Result = Device(75);
        public PeripheralDeviceSnapshot? DiscoveryResult;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Notify() => DevicesChanged?.Invoke();
        public Task<IReadOnlyList<PeripheralDeviceSnapshot>> DiscoverAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DiscoveryCalls);
            if (FailDiscovery) throw new IOException("test discovery failure");
            return Task.FromResult<IReadOnlyList<PeripheralDeviceSnapshot>>(Present ? [DiscoveryResult ?? Device()] : []);
        }
        public async Task<IReadOnlyList<PeripheralDeviceSnapshot>> ReadAsync(IReadOnlySet<string> deviceIds,
            PeripheralBatterySettings settings, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReadCalls); Entered.TrySetResult();
            if (FinishRead is not null) await FinishRead.Task.WaitAsync(cancellationToken);
            if (BlockRead)
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { ReadCancelled = true; throw; }
            if (FailRead) throw new IOException("test read failure");
            return [Result];
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
