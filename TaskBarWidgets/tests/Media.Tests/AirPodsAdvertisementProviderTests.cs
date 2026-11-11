using TaskBarWidgets.Media.Peripherals;
using Windows.Devices.Bluetooth;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class AirPodsAdvertisementProviderTests
{
    private sealed class Catalog : IWindowsPeripheralCatalog
    {
        public event Action? Changed;
        public IReadOnlyList<WindowsPeripheralNode> Snapshot { get; set; } = Nodes();
        public Task<IReadOnlyList<WindowsPeripheralNode>> GetAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task StopAsync() { Changed = null; return Task.CompletedTask; }
        public void Disconnect() { Snapshot = Snapshot.Select(n => n with { Connected = false }).ToArray(); Changed?.Invoke(); }
    }
    private static WindowsPeripheralNode[] Nodes(string address = "102030405060", string vendor = "0001004C", string model = "2024") =>
    [
        new("paired-" + address, "AirPods Pro - Find My", Guid.Empty, address, "", [], "earbuds", true, true, false, true, null, false),
        new("pnp-" + address, "Audio", Guid.Empty, address, $@"BTHENUM\A_VID&{vendor}_PID&{model}\node", [], "earbuds", true, true, false, true, null)
    ];
    private static AirPodsSighting Sighting(ulong address = 1, ushort model = 0x2024, int count = 2, DateTimeOffset? at = null) =>
        new(address, BluetoothAddressType.Random, -65, count, new(model,
            [new() { Role = "left", Percentage = 0, Estimated = true },
                new() { Role = "right", Percentage = 90, Estimated = true }, new() { Role = "case" }]), at ?? DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("0001004C", "2024", true)]
    [InlineData("0002004C", "2024", false)]
    [InlineData("0001046D", "2024", false)]
    [InlineData("0001004C", "FFFF", false)]
    public async Task UsesPairedClassicVendorAndModelRatherThanDisplayName(string vendor, string model, bool supported)
    {
        await using var provider = new AirPodsAdvertisementProvider(new Catalog { Snapshot = Nodes(vendor: vendor, model: model) });
        var devices = await provider.DiscoverAsync(default);
        Assert.Equal(supported ? 1 : 0, devices.Count);
        if (supported) Assert.Equal("AirPods Pro", devices[0].Name);
    }

    [Fact]
    public async Task KeepsPairedIdentityAcrossAddressRotationAndRetainsLastReadOnAmbiguity()
    {
        IReadOnlyList<AirPodsSighting> ads = [Sighting()];
        await using var provider = new AirPodsAdvertisementProvider(new Catalog(), (_, _) => Task.FromResult(ads));
        var target = Assert.Single(await provider.DiscoverAsync(default));
        var ids = new HashSet<string> { target.Id };
        var first = Assert.Single(await provider.ReadAsync(ids, new(), default));
        Assert.Equal(0, first.Batteries[0].Percentage);
        Assert.Null(first.Batteries[2].Percentage);
        ads = [Sighting(address: 2)];
        var rotated = Assert.Single(await provider.ReadAsync(ids, new(), default));
        Assert.Equal(first.Id, rotated.Id);
        Assert.False(rotated.Stale);
        ads = [Sighting(address: 2), Sighting(address: 3)];
        var ambiguous = Assert.Single(await provider.ReadAsync(ids, new(), default));
        Assert.Equal("unavailable", ambiguous.State);
        Assert.True(ambiguous.Stale);
        Assert.Equal(rotated.UpdatedAt, ambiguous.UpdatedAt);
        Assert.Equal(rotated.Batteries, ambiguous.Batteries);
    }

    [Theory]
    [InlineData("wrong-model")]
    [InlineData("one-frame")]
    [InlineData("stale")]
    public async Task RejectsUnrelatedOrInsufficientAdvertisements(string reason)
    {
        var ad = Sighting(model: reason == "wrong-model" ? (ushort)0x200a : (ushort)0x2024,
            count: reason == "one-frame" ? 1 : 2, at: reason == "stale" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null);
        await using var provider = new AirPodsAdvertisementProvider(new Catalog(), (_, _) => Task.FromResult<IReadOnlyList<AirPodsSighting>>([ad]));
        var id = Assert.Single(await provider.DiscoverAsync(default)).Id;
        var result = Assert.Single(await provider.ReadAsync(new HashSet<string> { id }, new(), default));
        Assert.Empty(result.Batteries);
        Assert.Null(result.UpdatedAt);
    }

    [Fact]
    public async Task DoesNotMatchWhenTwoPairedDevicesShareModel()
    {
        var scans = 0;
        await using var provider = new AirPodsAdvertisementProvider(new Catalog { Snapshot = [.. Nodes(), .. Nodes("112233445566")] },
            (_, _) => { scans++; return Task.FromResult<IReadOnlyList<AirPodsSighting>>([Sighting()]); });
        var ids = (await provider.DiscoverAsync(default)).Select(d => d.Id).ToHashSet();
        Assert.Equal(2, ids.Count);
        var result = await provider.ReadAsync(ids, new(), default);
        Assert.Equal(0, scans);
        Assert.All(result, d => Assert.Null(d.UpdatedAt));
    }

    [Fact]
    public async Task DisconnectDuringScanCannotBeReversedByNearbyAdvertisement()
    {
        var catalog = new Catalog();
        await using var provider = new AirPodsAdvertisementProvider(catalog, (_, _) =>
        { catalog.Disconnect(); return Task.FromResult<IReadOnlyList<AirPodsSighting>>([Sighting()]); });
        var id = Assert.Single(await provider.DiscoverAsync(default)).Id;
        var result = Assert.Single(await provider.ReadAsync(new HashSet<string> { id }, new(), default));
        Assert.Equal("disconnected", result.State);
        Assert.Null(result.UpdatedAt);
    }

    [Fact]
    public async Task DisablingOptionCancelsInFlightScan()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new AirPodsAdvertisementProvider(new Catalog(), async (_, token) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; });
        var id = Assert.Single(await provider.DiscoverAsync(default)).Id;
        var reading = provider.ReadAsync(new HashSet<string> { id }, new(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Configure(new() { AirPodsAdvertisementEnabled = false });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Empty(await provider.DiscoverAsync(default));
    }
}
