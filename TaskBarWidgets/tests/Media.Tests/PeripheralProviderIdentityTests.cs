using TaskBarWidgets.Media.Peripherals;
using Xunit;
using Endpoint = TaskBarWidgets.Media.Peripherals.PeripheralIdentityResolver.Endpoint;
using Observation = TaskBarWidgets.Media.Peripherals.PeripheralIdentityResolver.Observation;

namespace TaskBarWidgets.Media.Tests;

public sealed class PeripheralProviderIdentityTests
{
    private static Observation Native(string rawIdentity, PeripheralIdentityEvidence evidence, params string[] legacy) =>
        new(new Endpoint(1, PeripheralIdentity.FromKey(rawIdentity)), new PeripheralDeviceSnapshot
        {
            Id = PeripheralIdentity.FromKey(rawIdentity), Name = "Same product name", Identity = evidence,
            LegacyIds = legacy.Select(PeripheralIdentity.FromKey).ToArray()
        });

    private static Dictionary<Endpoint, string> Resolve(Observation[] devices, params string[] selected) =>
        new PeripheralIdentityResolver().ResolveBatch(devices, new Dictionary<string, PeripheralIdentityEvidence[]>(),
            new Dictionary<Endpoint, string>(), new PeripheralBatterySettings(), selected.ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void AzothSerialProofMergesEntitiesWithoutReplacingEitherTransportRoute()
    {
        const string oldRaw = "hid:v1|serial=6275732d73657269616c|vid=0b05|pid=1a85|mi=1|usage=ff00:0001|channel=rog_azoth_x";
        var evidence = new PeripheralIdentityEvidence
        {
            UniqueSerial = "bus-serial", SerialScope = "usb:0b05:1a85", ContainerIsDevice = true,
            ContainerId = "receiver-container", EndpointPath = "receiver-hid-endpoint"
        };
        var receiver = Native(oldRaw, evidence);
        var wired = Native(oldRaw.Replace("pid=1a85", "pid=1a83", StringComparison.Ordinal),
            evidence with { ContainerId = "wired-container", EndpointPath = "wired-hid-endpoint" }, oldRaw);
        Assert.NotEqual(receiver.Endpoint, wired.Endpoint);
        var routes = Resolve([wired, receiver], receiver.Endpoint.Id);
        Assert.Equal(2, routes.Count);
        Assert.Equal(receiver.Endpoint.Id, routes[receiver.Endpoint]);
        Assert.Equal(receiver.Endpoint.Id, routes[wired.Endpoint]);
        // Provider endpoint IDs remain the keys used to query the two transports.
        Assert.Contains(wired.Endpoint, routes.Keys);
        Assert.Contains(receiver.Endpoint, routes.Keys);
    }

    [Fact]
    public void ReusedSerialOnDifferentProductScopesDoesNotCorrelateDevices()
    {
        var left = new PeripheralIdentityEvidence
        {
            UniqueSerial = "same-serial", SerialScope = "usb:1532:0001",
            EndpointPath = "first-endpoint", ContainerId = "first-container", ContainerIsDevice = true
        };
        var right = left with { SerialScope = "usb:1532:0002", EndpointPath = "second-endpoint", ContainerId = "second-container" };
        Assert.Equal(0, PeripheralIdentityResolver.Proof(left, right));
        var routes = Resolve([Native("pid=0001", left), Native("pid=0002", right)]);
        Assert.Equal(2, routes.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AzothDifferentValidatedSerialsCannotMergeThroughHistoricalAlias()
    {
        var receiver = Native("receiver-raw", new()
        {
            UniqueSerial = "receiver-one", SerialScope = "usb:0b05:1a85", EndpointPath = "receiver-one-endpoint"
        });
        var wired = Native("wired-raw", new()
        {
            UniqueSerial = "wired-two", SerialScope = "usb:0b05:1a85", EndpointPath = "wired-two-endpoint"
        }, "receiver-raw");
        var routes = Resolve([receiver, wired]);
        Assert.Equal(2, routes.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("hidpp20")]
    [InlineData("rog_omni_keyboard")]
    public void SharedReceiverContainerIsNotTargetIdentity(string protocol)
    {
        var evidence = new PeripheralIdentityEvidence { ContainerId = "shared-receiver", ContainerIsDevice = false };
        var routes = Resolve([Native(protocol + "|first", evidence with { EndpointPath = "first-interface" }),
            Native(protocol + "|second", evidence with { EndpointPath = "second-interface" })]);
        Assert.Equal(2, routes.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void HidppChildrenKeepExactParentPrefixAfterEndpointRoutingFallback()
    {
        const string parent = "hid:v1|serial=73657269616c|vid=046d|pid=c547|mi=2|usage=ff00:0002|channel=hidpp20|endpoint=receiver-collection";
        const string unit = "01020304|model=408200000000";
        Assert.True(NativePeripheralBatteryProvider.IsHidppChild(parent, parent + "|unit=" + unit));
        Assert.False(NativePeripheralBatteryProvider.IsHidppChild(parent, parent.Replace("receiver-collection", "another-collection", StringComparison.Ordinal) + "|unit=" + unit));
        Assert.False(NativePeripheralBatteryProvider.IsHidppChild(parent, parent + "|unit=00000000|model=408200000000"));
        Assert.False(NativePeripheralBatteryProvider.IsHidppChild(parent, parent + "|unit=" + unit + "|extra"));
    }
}
