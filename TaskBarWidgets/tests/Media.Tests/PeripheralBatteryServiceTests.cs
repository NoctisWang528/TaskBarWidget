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
    public async Task PoweredOffReceiverDeviceKeepsLastBatteryAndRecoversWithoutRediscovery()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider
        {
            DiscoveryResult = Device() with { Connection = "wireless" },
            Result = Device(74) with { Connection = "wireless", UpdatedAt = clock.Utc }
        };
        var settings = new PeripheralBatterySettings { HideDisconnected = true };
        await using var service = new PeripheralBatteryService([provider], null, clock);
        service.Configure(settings, true, true, true); service.Start();
        await Until(() => provider.ReadCalls == 1 && !service.Snapshot.Refreshing);
        var lastRead = service.Snapshot.Devices[0].UpdatedAt;
        provider.Result = Device() with { Connection = "wireless", State = "disconnected", Stale = true };
        for (var count = 2; count <= 4; count++)
        {
            clock.Utc += TimeSpan.FromSeconds(61);
            service.Configure(settings, true, true, true);
            await Until(() => provider.ReadCalls == count && !service.Snapshot.Refreshing);
            Assert.Equal("disconnected", service.Snapshot.Devices[0].State);
            Assert.Equal(74, service.Snapshot.Devices[0].Batteries[0].Percentage);
            Assert.Equal(lastRead, service.Snapshot.Devices[0].UpdatedAt);
        }
        Assert.True(service.Snapshot.HideDisconnected);
        provider.Result = Device(73) with { Connection = "wireless", UpdatedAt = clock.Utc.AddSeconds(61) };
        clock.Utc += TimeSpan.FromSeconds(61);
        service.Configure(settings, true, true, true);
        await Until(() => provider.ReadCalls == 5 && !service.Snapshot.Refreshing);
        Assert.Equal("online", service.Snapshot.Devices[0].State);
        Assert.Equal(73, service.Snapshot.Devices[0].Batteries[0].Percentage);
        Assert.Equal(1, provider.DiscoveryCalls);

        provider.Present = false; provider.Notify();
        await Until(() => provider.DiscoveryCalls == 2 && provider.Demand.Count == 0 && !service.Snapshot.Refreshing);
        Assert.Equal("disconnected", service.Snapshot.Devices[0].State);
        Assert.False(service.Snapshot.Devices[0].CanRead);
        Assert.Equal(5, provider.ReadCalls);
    }

    [Fact]
    public async Task DisconnectedBluetoothDeviceIsNotActivelyPolled()
    {
        var provider = new FakeProvider { DiscoveryResult = Device(82) with
            { Connection = "bluetooth", State = "disconnected", Stale = true } };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetSettingsActive(true); service.Start();
        await Until(() => provider.DiscoveryCalls > 0 && !service.Snapshot.Refreshing);
        Assert.Empty(provider.Demand);
        Assert.Equal(0, provider.ReadCalls);
        Assert.Equal(82, service.Snapshot.Devices[0].Batteries[0].Percentage);
    }

    [Fact]
    public async Task ProvenLegacyIdentityRetainsTaskbarSelectionAndCardOptOut()
    {
        var endpointId = PeripheralIdentity.FromKey("corrected:usb-serial");
        var provider = new FakeProvider
        {
            DiscoveryResult = Device() with { Id = endpointId, LegacyIds = [Id] },
            Result = Device(64) with { Id = endpointId }
        };
        await using var service = new PeripheralBatteryService([provider], null);
        var settings = new PeripheralBatterySettings { CardVisibility = new() { [Id] = false }, Order = [Id] };
        service.Configure(settings, false, false, false);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => service.Snapshot.Devices.SingleOrDefault()?.Batteries.FirstOrDefault()?.Percentage == 64 && !service.Snapshot.Refreshing);
        var device = Assert.Single(service.Snapshot.Devices);
        Assert.Equal(Id, device.Id);
        Assert.Contains(endpointId, provider.ReadIds);
        Assert.DoesNotContain(Id, provider.ReadIds);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRetiredReceiverDropsOnlyPhantomCacheAndPreservesSettingsAndHistory(bool hadReading)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json");
        var retiredId = PeripheralIdentity.FromKey("hid:v1|serial=41424344|vid=0b05|pid=1ace|mi=2|usage=ff01:0001|channel=rog-report3");
        var unrelatedId = PeripheralIdentity.FromKey("another:physical-receiver");
        var obsolete = Device(hadReading ? 61 : null) with { Id = retiredId, Name = "ROG receiver", Source = "rog-report3" };
        var unrelated = Device() with { Id = unrelatedId, Name = "ROG receiver", Source = "rog-report3" };
        var settings = new PeripheralBatterySettings { Order = [retiredId], CardVisibility = new() { [retiredId] = false } };
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(new[] { obsolete, unrelated }));
            var provider = new FakeProvider { Present = false, RetiredDeviceIds = new HashSet<string> { retiredId } };
            await using (var service = new PeripheralBatteryService([provider], cache))
            {
                service.Configure(settings, true, false, false);
                service.SetTaskbarDemand([retiredId, unrelatedId]);
                service.SetSettingsActive(true); service.Start();
                await Until(() => provider.DiscoveryCalls > 0 && !service.Snapshot.Refreshing);
                Assert.Contains(service.Snapshot.Devices, d => d.Id == unrelatedId);
                if (hadReading)
                {
                    var retained = Assert.Single(service.Snapshot.Devices, d => d.Id == retiredId);
                    Assert.False(retained.CanRead);
                    Assert.Equal("disconnected", retained.State);
                    Assert.Equal(61, Assert.Single(retained.Batteries).Percentage);
                    Assert.Equal(obsolete.UpdatedAt, retained.UpdatedAt);
                }
                else Assert.DoesNotContain(service.Snapshot.Devices, d => d.Id == retiredId);
                Assert.Equal(new[] { retiredId }, settings.Order);
                Assert.False(settings.CardVisibility[retiredId]);
            }
            using var savedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(cache));
            var saved = savedDocument.RootElement.GetProperty("devices").EnumerateArray()
                .Select(e => e.GetProperty("device").Deserialize<PeripheralDeviceSnapshot>()!).ToArray();
            Assert.Equal(hadReading, saved.Any(d => d.Id == retiredId));
            Assert.Contains(saved, d => d.Id == unrelatedId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(null, "7409", false)]
    [InlineData("6978", "7409", false)]
    [InlineData("7409", "7409", true)]
    [InlineData("7409", "6918", false)]
    [InlineData("7409", null, true)]
    public async Task OmniPairedModelChangesCannotInheritAnotherModelsCachedBattery(string? cachedModel, string? currentModel, bool retainsReading)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json");
        var previous = Device(61) with { Source = "rog_omni_keyboard", Name = "Previously confirmed keyboard", ModelIdentity = cachedModel };
        var metadata = Device() with { Source = "rog_omni_keyboard", State = "unavailable", ModelIdentity = currentModel,
            ModelIdentityConfirmed = currentModel is not null, Name = currentModel is null or "6918" ? "ROG Omni keyboard" : "Newly confirmed keyboard" };
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(new[] { previous }));
            var provider = new FakeProvider { DiscoveryResult = metadata, Result = metadata };
            await using (var service = new PeripheralBatteryService([provider], cache))
            {
                service.SetTaskbarDemand([Id]); service.SetSettingsActive(true); service.Start();
                await Until(() => provider.ReadCalls > 0 && !service.Snapshot.Refreshing);
                var result = Assert.Single(service.Snapshot.Devices);
                Assert.Equal(currentModel ?? cachedModel, result.ModelIdentity);
                Assert.Equal(currentModel is null ? previous.Name : metadata.Name, result.Name);
                if (retainsReading)
                {
                    Assert.Equal(61, Assert.Single(result.Batteries).Percentage);
                    Assert.Equal(previous.UpdatedAt, result.UpdatedAt);
                }
                else { Assert.Empty(result.Batteries); Assert.Null(result.UpdatedAt); }
            }
            using var restoredDocument = JsonDocument.Parse(await File.ReadAllTextAsync(cache));
            var restored = restoredDocument.RootElement.GetProperty("devices").EnumerateArray()
                .Select(e => e.GetProperty("device").Deserialize<PeripheralDeviceSnapshot>()!).ToArray();
            Assert.Equal(currentModel ?? cachedModel, Assert.Single(restored).ModelIdentity);
            Assert.False(restored[0].ModelIdentityConfirmed);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("7409", true)]
    [InlineData("6918", false)]
    public async Task OmniModelProofSurvivesEntityAliasingAndWindowsReadingPriority(string nextModel, bool retainsReading)
    {
        var nativeId = PeripheralIdentity.FromKey("omni-native-endpoint");
        var evidence = new PeripheralIdentityEvidence { EndpointPath = @"hid\omni-keyboard" };
        var at = DateTimeOffset.UtcNow;
        var original = Device(61) with { Id = nativeId, Source = "rog_omni_keyboard", ModelIdentity = "7409",
            ModelIdentityConfirmed = true, Name = "ROG AZOTH EXTREME SPECIAL EDITION", NameQuality = 50, Identity = evidence, UpdatedAt = at };
        var windowsSample = Device(72) with { Identity = evidence, Source = "windows-property", CanRead = false,
            Name = "Old Windows model", NameQuality = 40, UpdatedAt = at.AddSeconds(1) };
        var windows = new FakeProvider { DiscoveryResult = windowsSample };
        var native = new FakeProvider { DiscoveryResult = original, Result = original };
        await using var service = new PeripheralBatteryService([windows, native], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => native.ReadCalls == 1 && !service.Snapshot.Refreshing);
        Assert.Equal("7409", Assert.Single(service.Snapshot.Devices).ModelIdentity);
        Assert.Equal("windows-property", service.Snapshot.Devices[0].Source);
        var unavailable = original with { Batteries = [], UpdatedAt = null, State = "unavailable", Stale = true,
            ModelIdentity = nextModel, Name = nextModel == "6918" ? "ROG Omni keyboard" : original.Name,
            NameQuality = nextModel == "6918" ? 10 : 50 };
        native.Notify(unavailable);
        var result = Assert.Single(service.Snapshot.Devices);
        Assert.Equal(Id, result.Id); Assert.Equal(Id, service.Snapshot.Aliases[nativeId]);
        Assert.Equal(nextModel, result.ModelIdentity); Assert.Equal(unavailable.Name, result.Name);
        if (retainsReading) { Assert.Equal(72, Assert.Single(result.Batteries).Percentage); Assert.Equal(at.AddSeconds(1), result.UpdatedAt); }
        else { Assert.Empty(result.Batteries); Assert.Null(result.UpdatedAt); }
    }

    [Fact]
    public async Task LateOldModelReadCannotReplaceNewModelNotification()
    {
        var original = Device(61) with { Source = "rog_omni_keyboard", ModelIdentity = "6978", ModelIdentityConfirmed = true,
            Name = "ROG AZOTH EXTREME", NameQuality = 50 };
        var provider = new FakeProvider { DiscoveryResult = original, Result = original,
            FinishRead = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Notify(original with { Batteries = [], UpdatedAt = null, ModelIdentity = "6918", Name = "ROG Omni keyboard",
            NameQuality = 10, State = "unavailable", Stale = true });
        provider.FinishRead.SetResult();
        await Until(() => !service.Snapshot.Refreshing);
        var result = Assert.Single(service.Snapshot.Devices);
        Assert.Equal("6918", result.ModelIdentity); Assert.Equal("ROG Omni keyboard", result.Name);
        Assert.Empty(result.Batteries); Assert.Null(result.UpdatedAt);
    }

    [Fact]
    public async Task DiscoveryBatchCannotRestoreModelReplacedByNewerNotification()
    {
        var original = Device(61) with { Source = "rog_omni_keyboard", ModelIdentity = "6978", ModelIdentityConfirmed = true,
            Name = "ROG AZOTH EXTREME", NameQuality = 50 };
        var native = new FakeProvider { DiscoveryResult = original, Result = original };
        var slow = new FakeProvider { Present = false };
        await using var service = new PeripheralBatteryService([native, slow], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => native.ReadCalls == 1 && !service.Snapshot.Refreshing);
        slow.FinishDiscovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Notify();
        await slow.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        native.Notify(original with { Batteries = [], UpdatedAt = null, ModelIdentity = "6918", Name = "ROG Omni keyboard",
            NameQuality = 10, State = "unavailable", Stale = true });
        slow.FinishDiscovery.SetResult();
        await Until(() => slow.DiscoveryCalls == 2 && !service.Snapshot.Refreshing);
        var result = Assert.Single(service.Snapshot.Devices);
        Assert.Equal("6918", result.ModelIdentity); Assert.Equal("ROG Omni keyboard", result.Name);
        Assert.Empty(result.Batteries); Assert.Null(result.UpdatedAt);
    }

    [Fact]
    public async Task RetiredEndpointCannotRemoveItsProvenActiveAlias()
    {
        var retired = PeripheralIdentity.FromKey("old-rogreceiver-endpoint");
        var evidence = new PeripheralIdentityEvidence { EndpointPath = @"hid\proven-endpoint" };
        var real = Device(61) with { Identity = evidence };
        var obsolete = real with { Id = retired, Source = "rog-report3", CanRead = false, Batteries = [] };
        var provider = new FakeProvider { DiscoveryResults = [obsolete, real], Result = real };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => provider.ReadCalls == 1 && !service.Snapshot.Refreshing);
        Assert.Equal(Id, service.Snapshot.Aliases[retired]);
        provider.DiscoveryResults = [real]; provider.RetiredDeviceIds = new HashSet<string> { retired }; provider.Notify();
        await Until(() => provider.DiscoveryCalls == 2 && !service.Snapshot.Refreshing);
        var result = Assert.Single(service.Snapshot.Devices);
        Assert.Equal(Id, result.Id); Assert.Equal(61, Assert.Single(result.Batteries).Percentage);
        Assert.Equal(Id, service.Snapshot.Aliases[retired]); Assert.True(result.CanRead);
    }

    [Fact]
    public async Task ConfirmedOmniPairWithUnavailableBatteryKeepsNormalPollingForRecovery()
    {
        var clock = new ManualClock();
        var metadata = Device() with { Source = "rog_omni_keyboard", State = "unavailable", ModelIdentity = "7409", ModelIdentityConfirmed = true };
        var provider = new FakeProvider { DiscoveryResult = metadata, Result = metadata };
        await using var service = new PeripheralBatteryService([provider], null, clock);
        var settings = new PeripheralBatterySettings();
        service.Configure(settings, true, true, true); service.Start();
        await Until(() => provider.ReadCalls == 1 && !service.Snapshot.Refreshing);
        for (var count = 2; count <= 6; count++)
        {
            clock.Utc += TimeSpan.FromSeconds(61);
            service.Configure(settings, true, true, true);
            await Until(() => provider.ReadCalls == count && !service.Snapshot.Refreshing);
            Assert.Empty(Assert.Single(service.Snapshot.Devices).Batteries);
        }
    }

    [Fact]
    public void SettingsMigrationAppendsRightCardAndKeepsExistingPositionsAndExplicitChoices()
    {
        var settings = new AppSettings { QuickCardOrder = ["media", "traffic", "weather", "device"],
            QuickCardColumns = new() { ["media"] = 0, ["traffic"] = 0, ["weather"] = 1, ["device"] = 1 },
            PeripheralBattery = new() { Mode = "detailed", CardVisibility = new() { [Id] = false }, Order = [Id] } };
        settings.NormalizeQuickCards();
        Assert.Equal(new[] { "media", "traffic", "weather", "device", "peripheralBattery", "aiUsage" }, settings.QuickCardOrder);
        Assert.Equal(1, settings.QuickCardColumns["peripheralBattery"]);
        Assert.Equal(1, settings.QuickCardColumns["aiUsage"]);
        Assert.True(settings.QuickCardEnabled["aiUsage"]);
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
    [InlineData("receiver|unit=pid=00e3;serial=4142434431323334", true)]
    [InlineData("other|unit=pid=00e3;serial=4142434431323334", false)]
    [InlineData("receiver|unit=pid=00e2;serial=4142434431323334", false)]
    [InlineData("receiver|unit=pid=00e3;serial=414243", false)]
    [InlineData("receiver|unit=pid=00e3;serial=41424344ff", false)]
    [InlineData("receiver|unit=pid=00e3;serial=30303030", false)]
    [InlineData("receiver|unit=pid=00e3;serial=554e4b574e313233", false)]
    [InlineData("receiver|unit=pid=00e3;serial=4142434431323334|extra=1", false)]
    public void DockChildRequiresTheExactReceiverSupportedModelAndRealSerial(string identity, bool accepted) =>
        Assert.Equal(accepted, NativePeripheralBatteryProvider.IsRazerDockChild("receiver", identity));

    [Theory]
    [InlineData("hid:v1|serial=41424344|vid=0b05|pid=1ace|mi=2|usage=ff01:0001|channel=rog-report3", true)]
    [InlineData("hid:v1|container={533529bb-8f25-5ff3-8159-744243565b01}|vid=0b05|pid=1ace|mi=2|usage=ff01:0001|channel=rog-report3", true)]
    [InlineData("hid:v1|serial=41424344|vid=0b05|pid=1ad0|mi=2|usage=ff01:0001|channel=rog-report3", false)]
    [InlineData("hid:v1|serial=41424344|vid=0b05|pid=1a85|mi=2|usage=ff01:0001|channel=rog-report3", false)]
    [InlineData("hid:v1|serial=41424344|vid=0b05|pid=1ace|mi=2|usage=ff00:0001|channel=rog_omni_keyboard", false)]
    [InlineData("hid:v1|serial=41424344|vid=0b05|pid=1ace|mi=2|usage=ff01:0001|channel=rog-report3|unit=1", false)]
    [InlineData("ROG receiver", false)]
    public void RetirementRequiresAnExactLegacyOmniEndpointIdentity(string identity, bool accepted) =>
        Assert.Equal(accepted, NativePeripheralBatteryProvider.IsRetiredRogReceiverIdentity(identity));

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
    public async Task IdentityLessV1HistoryIsHiddenUntilSelectedOrRediscovered()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json");
        var history = Enumerable.Range(0, 256).Select(n => Device() with { Id = PeripheralIdentity.FromKey("old:" + n) }).ToArray();
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(history));
            await using var service = new PeripheralBatteryService([new FakeProvider()], cache);
            service.Configure(new() { Order = [history[0].Id], CardVisibility = new() { [history[0].Id] = false } }, false, false, false);
            service.Start();
            service.SetSettingsActive(true);
            await Until(() => service.Snapshot.Devices.Any(d => d.Id == Id && d.Batteries.Length == 1) && !service.Snapshot.Refreshing);
            Assert.Single(service.Snapshot.Devices);
            service.SetTaskbarDemand([history[0].Id]);
            service.Configure(new() { Order = [history[0].Id], CardVisibility = new() { [history[0].Id] = false } }, false, false, false);
            Assert.Equal(2, service.Snapshot.Devices.Length);
            Assert.Contains(service.Snapshot.Devices, d => d.Id == history[0].Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SharedEntityRoutesReadsAndNotificationsToEachProviderEndpoint()
    {
        var other = PeripheralIdentity.FromKey("protocol-endpoint");
        var evidence = new PeripheralIdentityEvidence { BluetoothAddress = "11:22:33:44:55:66" };
        var windows = new FakeProvider { DiscoveryResult = Device() with { Identity = evidence, Source = "windows" },
            Result = Device(31) with { Identity = evidence, Source = "windows-pnp" } };
        var protocol = new FakeProvider { DiscoveryResult = Device() with { Id = other, Identity = evidence, Source = "hidpp20" },
            Result = Device(72) with { Id = other, Identity = evidence, Source = "hidpp20", UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1) } };
        await using var service = new PeripheralBatteryService([windows, protocol], null);
        service.Configure(new() { CardVisibility = new() { [Id] = false } }, false, false, false);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => protocol.ReadCalls == 1 && !service.Snapshot.Refreshing);
        var merged = Assert.Single(service.Snapshot.Devices);
        Assert.Equal(Id, merged.Id); Assert.False(merged.ShowInCard);
        Assert.Equal(72, Assert.Single(merged.Batteries).Percentage);
        Assert.Equal(new[] { Id }, windows.ReadIds);
        Assert.Equal(new[] { other }, protocol.ReadIds);
        Assert.Contains(other, protocol.Demand);
        Assert.Equal(Id, service.Snapshot.Aliases[other]);
        protocol.Notify(protocol.Result with { Batteries = [new() { Percentage = 85 }], UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(2) });
        Assert.Equal(85, Assert.Single(Assert.Single(service.Snapshot.Devices).Batteries).Percentage);
        service.SetTaskbarDemand([]);
        await Until(() => windows.Demand.Count == 0 && protocol.Demand.Count == 0);
        protocol.Notify(protocol.Result with { Batteries = [new() { Percentage = 90 }], UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(3) });
        Assert.Equal(85, service.Snapshot.Devices[0].Batteries[0].Percentage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DedicatedProtocolWinsTiedReadingTimeAndQualityControlsDescriptions(bool protocolFirst)
    {
        var other = PeripheralIdentity.FromKey("dedicated-protocol"); var at = DateTimeOffset.UtcNow;
        var identity = new PeripheralIdentityEvidence { EndpointPath = @"HID\VID_0001&PID_0002\instance" };
        var windowsSample = Device(20) with { Name = "Actual paired product", NameQuality = 50, Kind = "mouse", KindQuality = 40,
            Identity = identity, UpdatedAt = at, Source = "windows-pnp" };
        var protocolSample = Device(75) with { Id = other, Name = "HID-compliant mouse", Kind = "unknown",
            Identity = identity, UpdatedAt = at, Source = "vigil-watt:MouseBattery" };
        var windows = new FakeProvider { DiscoveryResult = windowsSample, Result = windowsSample };
        var protocol = new FakeProvider { DiscoveryResult = protocolSample, Result = protocolSample };
        await using var service = new PeripheralBatteryService(protocolFirst ? [protocol, windows] : [windows, protocol], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => windows.ReadCalls == 1 && protocol.ReadCalls == 1 && !service.Snapshot.Refreshing);
        var merged = Assert.Single(service.Snapshot.Devices);
        Assert.Equal(75, Assert.Single(merged.Batteries).Percentage);
        Assert.Equal("vigil-watt:MouseBattery", merged.Source);
        Assert.Equal("Actual paired product", merged.Name); Assert.Equal("mouse", merged.Kind);
        windows.Notify(windowsSample with { UpdatedAt = at.AddSeconds(-1), Batteries = [new() { Percentage = 10 }] });
        Assert.Equal(75, service.Snapshot.Devices[0].Batteries[0].Percentage);
        windows.Notify(windowsSample with { UpdatedAt = at.AddSeconds(1), Batteries = [new() { Percentage = 19 }] });
        Assert.Equal(19, service.Snapshot.Devices[0].Batteries[0].Percentage);
    }

    [Fact]
    public async Task NewSettingsWrittenDuringDiscoveryWinAliasMigration()
    {
        var other = PeripheralIdentity.FromKey("late-protocol");
        var evidence = new PeripheralIdentityEvidence { UniqueSerial = "unique", SerialScope = "usb:0001:0002" };
        var windows = new FakeProvider { DiscoveryResult = Device() with { Identity = evidence } };
        var protocol = new FakeProvider { DiscoveryResult = Device() with { Id = other, Identity = evidence },
            Result = Device(40) with { Id = other }, FinishDiscovery = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new PeripheralBatteryService([windows, protocol], null);
        service.Configure(new() { CardVisibility = new() { [other] = false } }, false, false, false);
        service.SetTaskbarDemand([Id]); service.Start();
        await protocol.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Configure(new() { CardVisibility = new() { [Id] = true, [other] = false } }, false, false, false);
        protocol.FinishDiscovery.SetResult();
        await Until(() => protocol.ReadCalls > 0 && !service.Snapshot.Refreshing);
        Assert.True(Assert.Single(service.Snapshot.Devices).ShowInCard);
        var input = new PeripheralBatterySettings { Order = [other, Id], CardVisibility = new() { [Id] = true, [other] = false } };
        var normalized = service.NormalizeSettings(input);
        Assert.Equal(new[] { Id }, normalized.Order); Assert.True(normalized.CardVisibility[Id]);
        Assert.Equal(2, input.Order.Count);
        service.Configure(new() { CardVisibility = new() { [Id] = false } }, false, false, false);
        Assert.False(service.Snapshot.Devices[0].ShowInCard);
    }

    [Fact]
    public async Task V2CacheRetainsProvenAliasesRoleTimesAndOfflineEvidenceAcrossNewEndpointIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json");
        var other = PeripheralIdentity.FromKey("replacement-endpoint"); var at = DateTimeOffset.UtcNow;
        var evidence = new PeripheralIdentityEvidence { UniqueSerial = "cache-unit", SerialScope = "usb:0001:0002" };
        var sample = Device() with { Identity = evidence, UpdatedAt = at, Source = "hidpp20", Batteries =
            [new() { Role = "left", Percentage = 40 }, new() { Role = "right", Percentage = 60 }] };
        try
        {
            var provider = new FakeProvider { DiscoveryResult = sample, Result = sample };
            await using (var service = new PeripheralBatteryService([provider], cache))
            {
                service.SetTaskbarDemand([Id]); service.Start();
                await Until(() => provider.ReadCalls > 0 && !service.Snapshot.Refreshing);
                provider.Notify(sample with { UpdatedAt = at.AddSeconds(5), Batteries = [new() { Role = "left", Percentage = 39 }] });
                Assert.Equal(2, service.Snapshot.Devices[0].Batteries.Length);
            }
            using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(cache)))
                Assert.Equal(2, saved.RootElement.GetProperty("version").GetInt32());
            var replacement = new FakeProvider { DiscoveryResult = Device() with { Id = other, Identity = evidence },
                Result = Device() with { Id = other, Identity = evidence, UpdatedAt = at.AddSeconds(2),
                    Batteries = [new() { Role = "right", Percentage = 58 }] } };
            await using (var restored = new PeripheralBatteryService([replacement], cache))
            {
                restored.SetTaskbarDemand([Id]); restored.Start();
                await Until(() => replacement.ReadCalls > 0 && !restored.Snapshot.Refreshing);
                var merged = Assert.Single(restored.Snapshot.Devices);
                Assert.Equal(Id, merged.Id); Assert.Equal(Id, restored.ResolveId(other));
                Assert.Equal(at.AddSeconds(5), merged.UpdatedAt);
                Assert.Equal(39, merged.Batteries.Single(b => b.Role == "left").Percentage);
                Assert.Equal(58, merged.Batteries.Single(b => b.Role == "right").Percentage);
                Assert.Equal(new[] { other }, replacement.ReadIds);
            }
            await using var again = new PeripheralBatteryService([], cache);
            again.Start();
            await Until(() => again.Snapshot.Devices.Length == 1);
            Assert.Equal(Id, again.ResolveId(other));
            Assert.Equal(2, again.Snapshot.Devices[0].Batteries.Length);
            Assert.True(again.Snapshot.Devices[0].Stale);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task V1HistoryOrderAndOptOutAreNotSelectionButExplicitTrueIs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json"); var at = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(new[] { Device(41) with { UpdatedAt = at } }));
            var provider = new FakeProvider { Present = false };
            await using var service = new PeripheralBatteryService([provider], cache);
            service.Configure(new() { Order = [Id], CardVisibility = new() { [Id] = false } }, false, false, false);
            service.SetSettingsActive(true); service.Start();
            await Until(() => provider.DiscoveryCalls > 0 && !service.Snapshot.Refreshing);
            Assert.Empty(service.Snapshot.Devices);
            service.Configure(new() { Order = [Id], CardVisibility = new() { [Id] = true } }, false, false, false);
            var selected = Assert.Single(service.Snapshot.Devices);
            Assert.Equal(41, Assert.Single(selected.Batteries).Percentage); Assert.Equal(at, selected.UpdatedAt);
            Assert.Equal("disconnected", selected.State); Assert.True(selected.Stale);
            Assert.False(selected.CanRead);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DiagnosticsUseOneReportSaltAndNeverExposePrivateIdentityValues()
    {
        var other = PeripheralIdentity.FromKey("diagnostic-alias");
        var sample = Device(50) with { Name = "Private user name", Source = "hidpp20", LegacyIds = [other], Identity = new()
            { ContainerId = "private-container", BluetoothAddress = "112233445566", UniqueSerial = "private-serial",
              SerialScope = "private-scope", EndpointPath = @"\\?\hid#private-endpoint", UnitId = "private-unit", UnitScope = "unit-scope" } };
        var provider = new FakeProvider { DiscoveryResult = sample, Result = sample };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => provider.ReadCalls > 0 && !service.Snapshot.Refreshing);
        var salt = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var report = service.ExportDiagnostics(salt);
        Assert.Equal(report, service.ExportDiagnostics(salt));
        Assert.NotEqual(report, service.ExportDiagnostics());
        foreach (var secret in new[] { Id, other, sample.Name, "private-container", "112233445566", "private-serial", "private-scope",
            "private-endpoint", "private-unit", "unit-scope" }) Assert.DoesNotContain(secret, report);
        using var json = JsonDocument.Parse(report);
        var entity = json.RootElement.GetProperty("devices")[0];
        Assert.Equal(entity.GetProperty("id").GetString(), entity.GetProperty("endpoints")[0].GetProperty("id").GetString());
        Assert.StartsWith("redacted-", entity.GetProperty("id").GetString());
        Assert.True(entity.GetProperty("canRead").GetBoolean());
        Assert.Equal(entity.GetProperty("id").GetString(), json.RootElement.GetProperty("aliases")[0].GetProperty("to").GetString());
        Assert.Equal(25, entity.GetProperty("evidence")[0].GetProperty("uniqueSerial").GetString()!.Length);
    }

    [Fact]
    public async Task SameReceiverPathKeepsChildReadingsIndependent()
    {
        var other = PeripheralIdentity.FromKey("receiver-child-b");
        var identity = new PeripheralIdentityEvidence { EndpointPath = @"hid\shared-receiver", UnitId = "unit-a|model",
            UnitScope = "logitech:hidpp-unit-model" };
        var a = Device(25) with { Identity = identity };
        var b = Device(80) with { Id = other, Identity = identity with { UnitId = "unit-b|model" } };
        var provider = new FakeProvider { DiscoveryResults = [a, b], Results = [a, b] };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetTaskbarDemand([Id, other]); service.Start();
        await Until(() => provider.ReadCalls > 0 && !service.Snapshot.Refreshing);
        Assert.Equal(2, service.Snapshot.Devices.Length);
        Assert.Equal(25, Assert.Single(service.Snapshot.Devices.Single(d => d.Id == Id).Batteries).Percentage);
        Assert.Equal(80, Assert.Single(service.Snapshot.Devices.Single(d => d.Id == other).Batteries).Percentage);
        Assert.Empty(service.Snapshot.Aliases);
        using var diagnostics = JsonDocument.Parse(service.ExportDiagnostics());
        Assert.Equal("", diagnostics.RootElement.GetProperty("devices")[0].GetProperty("evidence")[0].GetProperty("bluetoothAddress").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedEndpointKeepsTheSelectedOriginalOfflineAndTheReplacementSeparate(bool losingAlias)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json"); var at = DateTimeOffset.UtcNow;
        var endpointId = losingAlias ? PeripheralIdentity.FromKey("reused-losing-alias") : Id;
        var evidence = new PeripheralIdentityEvidence { UniqueSerial = "original-device", SerialScope = "usb:0001:0002" };
        var original = Device(80) with { Id = endpointId, Name = "Original device", Identity = evidence, UpdatedAt = at,
            LegacyIds = losingAlias ? [Id] : [] };
        var replacement = Device() with { Id = endpointId, Name = "Replacement device", Kind = "keyboard",
            Identity = evidence with { UniqueSerial = "replacement-device" } };
        var settings = new PeripheralBatterySettings { Order = [Id], CardVisibility = new() { [Id] = true } };
        string replacementId;
        try
        {
            var provider = new FakeProvider { DiscoveryResult = original, Result = original };
            await using (var service = new PeripheralBatteryService([provider], cache))
            {
                service.Configure(settings, false, false, false); service.SetTaskbarDemand([Id]); service.Start();
                await Until(() => provider.ReadCalls == 1 && !service.Snapshot.Refreshing);
                provider.DiscoveryResult = replacement; provider.Notify();
                await Until(() => provider.DiscoveryCalls == 2 && !service.Snapshot.Refreshing);
                var old = service.Snapshot.Devices.Single(d => d.Id == Id);
                var current = service.Snapshot.Devices.Single(d => d.Id != Id);
                replacementId = current.Id;
                Assert.NotEqual(endpointId, replacementId); Assert.NotEqual(Id, replacementId);
                Assert.Equal("disconnected", old.State); Assert.True(old.Stale); Assert.False(old.CanRead); Assert.True(old.ShowInCard);
                Assert.Equal(80, Assert.Single(old.Batteries).Percentage); Assert.Equal(at, old.UpdatedAt);
                Assert.Equal("Replacement device", current.Name); Assert.Equal("keyboard", current.Kind);
                Assert.Empty(current.Batteries); Assert.Null(current.UpdatedAt); Assert.False(current.ShowInCard);
                Assert.Equal(Id, service.ResolveId(Id)); Assert.Equal(1, provider.ReadCalls);
                Assert.Equal(Id, service.ResolveId(endpointId));
                if (losingAlias) Assert.Equal(Id, service.Snapshot.Aliases[endpointId]);
                Assert.Equal(new[] { Id }, service.NormalizeSettings(settings).Order);
                provider.Result = replacement with { UpdatedAt = at.AddSeconds(1), Batteries = [new() { Percentage = 22 }] };
                service.SetTaskbarDemand([Id, replacementId]);
                await Until(() => provider.ReadCalls == 2 && !service.Snapshot.Refreshing);
                Assert.Equal(new[] { endpointId }, provider.ReadIds);
                Assert.Equal(22, Assert.Single(service.Snapshot.Devices.Single(d => d.Id == replacementId).Batteries).Percentage);
                provider.Notify();
                await Until(() => provider.DiscoveryCalls == 3 && !service.Snapshot.Refreshing);
                Assert.Equal(replacementId, service.Snapshot.Devices.Single(d => d.Name == replacement.Name).Id);
                Assert.Equal("disconnected", service.Snapshot.Devices.Single(d => d.Id == Id).State);
                Assert.True(service.Snapshot.Devices.Single(d => d.Id == Id).Stale);
                Assert.DoesNotContain(Id, service.Snapshot.Aliases.Keys);
                Assert.Equal(Id, service.ResolveId(endpointId));
                var oldDraft = service.NormalizeSettings(new() { Order = [endpointId], CardVisibility = new() { [endpointId] = false } });
                Assert.Equal(new[] { Id }, oldDraft.Order); Assert.False(oldDraft.CardVisibility[Id]);
            }
            var restoredProvider = new FakeProvider { DiscoveryResult = replacement,
                Result = replacement with { UpdatedAt = at.AddSeconds(2), Batteries = [new() { Percentage = 21 }] } };
            await using var restored = new PeripheralBatteryService([restoredProvider], cache);
            restored.Configure(settings, false, false, false); restored.SetTaskbarDemand([Id, replacementId]); restored.Start();
            await Until(() => restoredProvider.ReadCalls == 1 && !restored.Snapshot.Refreshing);
            Assert.Equal(new[] { endpointId }, restoredProvider.ReadIds);
            var historical = restored.Snapshot.Devices.Single(d => d.Id == Id);
            Assert.Equal("disconnected", historical.State); Assert.True(historical.Stale);
            Assert.Equal(80, Assert.Single(historical.Batteries).Percentage); Assert.Equal(at, historical.UpdatedAt);
            Assert.Equal(21, Assert.Single(restored.Snapshot.Devices.Single(d => d.Id == replacementId).Batteries).Percentage);
            Assert.Equal(Id, restored.ResolveId(Id)); Assert.DoesNotContain(Id, restored.Snapshot.Aliases.Keys);
            Assert.Equal(Id, restored.ResolveId(endpointId));
            if (losingAlias) Assert.Equal(Id, restored.Snapshot.Aliases[endpointId]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChargingFreshnessIsIndependentOfPercentageAndSurvivesCacheRestore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-battery-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(directory, "cache.json"); var at = DateTimeOffset.UtcNow;
        var sample = Device(40) with { Source = "hidpp20", UpdatedAt = at,
            Batteries = [new() { Percentage = 40, Charging = false }] };
        try
        {
            var provider = new FakeProvider { DiscoveryResult = sample, Result = sample };
            await using (var service = new PeripheralBatteryService([provider], cache))
            {
                service.SetTaskbarDemand([Id]); service.Start();
                await Until(() => provider.ReadCalls == 1 && !service.Snapshot.Refreshing);
                provider.Notify(sample with { UpdatedAt = at.AddSeconds(10), Batteries = [new() { Charging = true }] });
                provider.Notify(sample with { UpdatedAt = at.AddSeconds(5), Batteries = [new() { Charging = false }] });
                var reading = Assert.Single(Assert.Single(service.Snapshot.Devices).Batteries);
                Assert.Equal(40, reading.Percentage); Assert.True(reading.Charging);
                Assert.Equal(at, service.Snapshot.Devices[0].UpdatedAt);
            }
            using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(cache)))
            {
                var role = saved.RootElement.GetProperty("devices")[0].GetProperty("readings")[0];
                Assert.Equal(at, role.GetProperty("updatedAt").GetDateTimeOffset());
                Assert.Equal(at.AddSeconds(10), role.GetProperty("chargingUpdatedAt").GetDateTimeOffset());
            }
            var delayed = sample with { UpdatedAt = at.AddSeconds(7), Batteries = [new() { Percentage = 39, Charging = false }] };
            var restoredProvider = new FakeProvider { DiscoveryResult = Device(), Result = delayed };
            await using var restored = new PeripheralBatteryService([restoredProvider], cache);
            restored.SetTaskbarDemand([Id]); restored.Start();
            await Until(() => restoredProvider.ReadCalls == 1 && !restored.Snapshot.Refreshing);
            var retained = Assert.Single(Assert.Single(restored.Snapshot.Devices).Batteries);
            Assert.Equal(39, retained.Percentage); Assert.True(retained.Charging);
            Assert.Equal(at.AddSeconds(7), restored.Snapshot.Devices[0].UpdatedAt);
            restoredProvider.Notify(sample with { UpdatedAt = at.AddSeconds(11), Batteries = [new() { Charging = false }] });
            Assert.False(restored.Snapshot.Devices[0].Batteries[0].Charging);
            Assert.Equal(at.AddSeconds(7), restored.Snapshot.Devices[0].UpdatedAt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SameSourceUnknownChargingInvalidatesOnlyOlderChargingEvidence()
    {
        var at = DateTimeOffset.UtcNow;
        var sample = Device(40) with { Source = "vigil-watt:RazerBattery", UpdatedAt = at,
            Batteries = [new() { Percentage = 40, Charging = true }] };
        var provider = new FakeProvider { DiscoveryResult = sample, Result = sample };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => provider.ReadCalls == 1 && !service.Snapshot.Refreshing);
        provider.Notify(sample with { UpdatedAt = at.AddSeconds(1), Batteries = [new() { Percentage = 39 }] });
        Assert.Null(service.Snapshot.Devices[0].Batteries[0].Charging);
        provider.Notify(sample with { UpdatedAt = at.AddSeconds(10), Batteries = [new() { Charging = true }] });
        provider.Notify(sample with { UpdatedAt = at.AddSeconds(5), Batteries = [new() { Percentage = 38 }] });
        Assert.True(service.Snapshot.Devices[0].Batteries[0].Charging);
        Assert.Equal(38, service.Snapshot.Devices[0].Batteries[0].Percentage);
        provider.Notify(sample with { Source = "windows-pnp", UpdatedAt = at.AddSeconds(11),
            Batteries = [new() { Percentage = 37 }] });
        Assert.True(service.Snapshot.Devices[0].Batteries[0].Charging);
        provider.Notify(sample with { UpdatedAt = at.AddSeconds(12), Batteries = [new() { Percentage = 36 }] });
        Assert.Null(service.Snapshot.Devices[0].Batteries[0].Charging);
        provider.Notify(sample with { UpdatedAt = at.AddSeconds(11), Batteries = [new() { Charging = false }] });
        Assert.Null(service.Snapshot.Devices[0].Batteries[0].Charging);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationWhileLaterProviderDiscoversSurvivesEarlierDiscoveryResult(bool omitted)
    {
        var at = DateTimeOffset.UtcNow;
        var sample = Device(70) with { UpdatedAt = at };
        var first = new FakeProvider { DiscoveryResult = sample, Result = sample };
        var second = new FakeProvider { Present = false };
        await using var service = new PeripheralBatteryService([first, second], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await Until(() => first.ReadCalls == 1 && !service.Snapshot.Refreshing);
        second.FinishDiscovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        second.DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first.DiscoveryResults = omitted ? [] : [Device() with { State = "disconnected", Stale = true, CanRead = false }];
        first.Notify();
        await second.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Notify(sample with { UpdatedAt = at.AddSeconds(2), Batteries = [new() { Percentage = 68 }] });
        second.FinishDiscovery.SetResult();
        await Until(() => second.DiscoveryCalls == 2 && !service.Snapshot.Refreshing);
        var retained = Assert.Single(service.Snapshot.Devices);
        Assert.Equal("online", retained.State); Assert.True(retained.CanRead); Assert.False(retained.Stale);
        Assert.Equal(68, Assert.Single(retained.Batteries).Percentage);
        Assert.Equal(at.AddSeconds(2), retained.UpdatedAt);
        Assert.Equal(1, first.ReadCalls);
    }

    [Fact]
    public async Task OlderReadStillUpdatesOtherRolesWithoutReplacingNotificationConnectionState()
    {
        var at = DateTimeOffset.UtcNow;
        var initial = Device() with { UpdatedAt = at, Batteries =
            [new() { Role = "left", Percentage = 50 }, new() { Role = "right", Percentage = 70 }] };
        var provider = new FakeProvider { DiscoveryResult = initial, FinishRead = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Result = initial with { UpdatedAt = at.AddSeconds(1), State = "disconnected", CanRead = false, Stale = true,
                Batteries = [new() { Role = "left", Percentage = 49 }, new() { Role = "right", Percentage = 67 }] } };
        await using var service = new PeripheralBatteryService([provider], null);
        service.SetTaskbarDemand([Id]); service.Start();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Notify(initial with { UpdatedAt = at.AddSeconds(2), Batteries = [new() { Role = "left", Percentage = 48 }] });
        provider.FinishRead.SetResult();
        await Until(() => !service.Snapshot.Refreshing);
        var retained = Assert.Single(service.Snapshot.Devices);
        Assert.Equal(48, retained.Batteries.Single(b => b.Role == "left").Percentage);
        Assert.Equal(67, retained.Batteries.Single(b => b.Role == "right").Percentage);
        Assert.Equal(at.AddSeconds(2), retained.UpdatedAt);
        Assert.Equal("online", retained.State); Assert.True(retained.CanRead); Assert.False(retained.Stale);
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

    private sealed class FakeProvider : IPeripheralBatteryProvider, IPeripheralBatteryNotifications, IPeripheralBatteryRetirements
    {
        public IReadOnlySet<string> RetiredDeviceIds { get; set; } = new HashSet<string>();
        public TaskCompletionSource? FinishRead;
        public TaskCompletionSource? FinishDiscovery;
        public TaskCompletionSource DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlySet<string> ReadIds { get; private set; } = new HashSet<string>();
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
        public PeripheralDeviceSnapshot[]? DiscoveryResults, Results;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Notify() => DevicesChanged?.Invoke();
        public async Task<IReadOnlyList<PeripheralDeviceSnapshot>> DiscoverAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DiscoveryCalls);
            DiscoveryEntered.TrySetResult();
            if (FinishDiscovery is not null) await FinishDiscovery.Task.WaitAsync(cancellationToken);
            if (FailDiscovery) throw new IOException("test discovery failure");
            return Present ? DiscoveryResults ?? [DiscoveryResult ?? Device()] : [];
        }
        public async Task<IReadOnlyList<PeripheralDeviceSnapshot>> ReadAsync(IReadOnlySet<string> deviceIds,
            PeripheralBatterySettings settings, CancellationToken cancellationToken)
        {
            ReadIds = deviceIds.ToHashSet();
            Interlocked.Increment(ref ReadCalls); Entered.TrySetResult();
            if (FinishRead is not null) await FinishRead.Task.WaitAsync(cancellationToken);
            if (BlockRead)
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { ReadCancelled = true; throw; }
            if (FailRead) throw new IOException("test read failure");
            return Results ?? [Result];
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
