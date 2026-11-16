using System.Text.Json;
using TaskBarWidgets.Media.Peripherals;
using Xunit;
using Endpoint = TaskBarWidgets.Media.Peripherals.PeripheralIdentityResolver.Endpoint;
using Observation = TaskBarWidgets.Media.Peripherals.PeripheralIdentityResolver.Observation;

namespace TaskBarWidgets.Media.Tests;

public sealed class PeripheralIdentityResolverTests
{
    private static string Id(string key) => PeripheralIdentity.FromKey(key);
    private static Observation EndpointDevice(int provider, string key, PeripheralIdentityEvidence? evidence = null,
        params string[] legacy) => new(new(provider, Id(key)), new()
        { Id = Id(key), Name = "Identical product name", Identity = evidence, LegacyIds = legacy });

    private static Dictionary<Endpoint, string> Resolve(PeripheralIdentityResolver resolver, Observation[] endpoints,
        PeripheralBatterySettings? settings = null, string[]? selected = null,
        Dictionary<string, PeripheralIdentityEvidence[]>? history = null,
        Dictionary<Endpoint, string>? previous = null) => resolver.ResolveBatch(endpoints, history ?? [], previous ?? [],
            settings ?? new(), (selected ?? []).ToHashSet(StringComparer.Ordinal));

    [Theory]
    [InlineData("endpoint")]
    [InlineData("serial")]
    [InlineData("unit")]
    [InlineData("bluetooth")]
    [InlineData("container")]
    public void ProvenEvidenceMergesAndKeepsSelectedTaskbarIdentity(string kind)
    {
        PeripheralIdentityEvidence evidence = kind switch
        {
            "endpoint" => new PeripheralIdentityEvidence { EndpointPath = "hid\\vid_0001&pid_0002\\instance" },
            "serial" => new() { UniqueSerial = "serial-one", SerialScope = "usb:0001:0002" },
            "unit" => new() { UnitId = "unit|model", UnitScope = "logitech:hidpp-unit-model" },
            "bluetooth" => new() { BluetoothAddress = "AA:BB:CC:DD:EE:FF" },
            _ => new() { ContainerId = "device-container", ContainerIsDevice = true }
        };
        foreach (var reverse in new[] { false, true })
        {
            var resolver = new PeripheralIdentityResolver();
            var endpoints = new[] { EndpointDevice(0, "windows", evidence), EndpointDevice(1, "native", evidence) };
            var routes = Resolve(resolver, reverse ? endpoints.Reverse().ToArray() : endpoints, selected: [Id("native")]);
            Assert.All(routes.Values, id => Assert.Equal(Id("native"), id));
            Assert.Equal(Id("native"), resolver.ResolveId(Id("windows")));
            Assert.All(routes.Values, id => Assert.True(PeripheralIdentity.IsValid(id)));
        }
    }

    [Fact]
    public void DockPairSerialSeparatesReceiverAndReplacementWhileSurvivingAnotherDock()
    {
        var receiver = new PeripheralIdentityEvidence { EndpointPath = @"hid\dock-one", ContainerId = "dock-one", ContainerIsDevice = false };
        var paired = receiver with { UnitId = "pid=00e3;serial=4142434431323334", UnitScope = "razer:paired-pid-serial" };
        var moved = paired with { EndpointPath = @"hid\dock-two", ContainerId = "dock-two" };
        var replaced = paired with { UnitId = "pid=00e3;serial=5152535435363738" };
        var routes = Resolve(new(), [EndpointDevice(0, "receiver", receiver), EndpointDevice(0, "paired", paired),
            EndpointDevice(1, "moved", moved), EndpointDevice(1, "replacement", replaced)]);
        Assert.Equal(3, routes.Values.Distinct().Count());
        Assert.Equal(routes[new(0, Id("paired"))], routes[new(1, Id("moved"))]);
        Assert.NotEqual(routes[new(0, Id("receiver"))], routes[new(0, Id("paired"))]);
        Assert.NotEqual(routes[new(1, Id("replacement"))], routes[new(0, Id("paired"))]);
    }

    [Fact]
    public void CompleteBatchUsesBridgeEvidenceBeforeChoosingTheSurvivor()
    {
        var serial = new PeripheralIdentityEvidence { UniqueSerial = "unique", SerialScope = "usb:0001:0002" };
        var bluetooth = new PeripheralIdentityEvidence { BluetoothAddress = "112233445566" };
        var routes = Resolve(new(), [EndpointDevice(0, "usb", serial), EndpointDevice(1, "bt", bluetooth),
            EndpointDevice(2, "bridge", serial with { BluetoothAddress = bluetooth.BluetoothAddress })],
            new() { Order = [Id("bt"), Id("bridge"), Id("usb")] });
        Assert.Single(routes.Values.Distinct());
        Assert.All(routes.Values, id => Assert.Equal(Id("bt"), id));
    }

    [Fact]
    public void UnconfiguredSurvivorUsesOrdinalId()
    {
        var evidence = new PeripheralIdentityEvidence { BluetoothAddress = "112233445566" };
        var ids = new[] { Id("a"), Id("b") }.Order(StringComparer.Ordinal).ToArray();
        var routes = Resolve(new(), [EndpointDevice(0, "a", evidence), EndpointDevice(1, "b", evidence)]);
        Assert.All(routes.Values, id => Assert.Equal(ids[0], id));
    }

    [Fact]
    public void NamesAndProductIdsAreNeverIdentityEvidence()
    {
        var routes = Resolve(new(), [EndpointDevice(0, "vid=0001|pid=0002|a"), EndpointDevice(1, "vid=0001|pid=0002|b")]);
        Assert.Equal(2, routes.Values.Distinct().Count());
    }

    [Fact]
    public void EndpointIdBelongsToItsProvider()
    {
        var routes = Resolve(new(), [EndpointDevice(0, "same-endpoint-id"), EndpointDevice(1, "same-endpoint-id")]);
        Assert.Equal(2, routes.Values.Distinct().Count());
        Assert.All(routes.Values, id => Assert.True(PeripheralIdentity.IsValid(id)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReceiverContainersAreNotDeviceEvidence(bool leftDevice, bool rightDevice)
    {
        var left = new PeripheralIdentityEvidence { ContainerId = "receiver", ContainerIsDevice = leftDevice };
        var routes = Resolve(new(), [EndpointDevice(0, "a", left), EndpointDevice(1, "b", left with { ContainerIsDevice = rightDevice })]);
        Assert.Equal(2, routes.Values.Distinct().Count());
    }

    [Fact]
    public void SamePathNeverCollapsesReceiverChildrenOrTheReceiverItself()
    {
        var receiver = new PeripheralIdentityEvidence { EndpointPath = "hid\\receiver", ContainerId = "receiver" };
        var unit = receiver with { UnitId = "unit-a|model-a", UnitScope = "logitech:hidpp-unit-model" };
        var routes = Resolve(new(), [EndpointDevice(0, "receiver", receiver), EndpointDevice(0, "child-a", unit),
            EndpointDevice(1, "child-b", unit with { UnitId = "unit-b|model-b" }),
            EndpointDevice(2, "other-scope", unit with { UnitScope = "other-scope" })]);
        Assert.Equal(4, routes.Values.Distinct().Count());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public void EndpointScopedUnitsNeitherMatchNorConflictAcrossReceivers(string otherSlot)
    {
        var a = new PeripheralIdentityEvidence { UnitScope = "endpoint", UnitId = "1", EndpointPath = @"hid\receiver-a" };
        var b = a with { EndpointPath = @"hid\receiver-b", UnitId = otherSlot };
        Assert.Equal(0, PeripheralIdentityResolver.Proof(a, b));
        Assert.False(PeripheralIdentityResolver.Conflicts(a, b));
        var routes = Resolve(new(), [EndpointDevice(0, "a", a), EndpointDevice(1, "b", b)]);
        Assert.Equal(2, routes.Values.Distinct().Count());
    }

    [Fact]
    public void EndpointScopedUnitsMatchNormalizedPathAndRealUnitsMatchAcrossPaths()
    {
        var a = new PeripheralIdentityEvidence { UnitScope = "endpoint", UnitId = "1",
            EndpointPath = @"\\?\hid#vid_046d&pid_c548&mi_01#instance#{4d1e55b2-f16f-11cf-88cb-001111000030}" };
        var sameEndpoint = a with { EndpointPath = @"HID\VID_046D&PID_C548&MI_01\instance" };
        Assert.True(PeripheralIdentityResolver.Proof(a, sameEndpoint) > 0);
        Assert.True(PeripheralIdentityResolver.Conflicts(a, sameEndpoint with { UnitId = "2" }));
        var unit = a with { UnitScope = "logitech:hidpp-unit-model", UnitId = "12345678|model=001122334455" };
        var routes = Resolve(new(), [EndpointDevice(0, "native", unit),
            EndpointDevice(1, "upstream", unit with { EndpointPath = @"hid\another-receiver" })]);
        Assert.Single(routes.Values.Distinct());
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("unit")]
    [InlineData("bluetooth")]
    public void ConflictingStrongEvidenceOverridesAnEqualDeviceContainer(string conflict)
    {
        var left = new PeripheralIdentityEvidence { ContainerId = "device", ContainerIsDevice = true,
            UniqueSerial = "a", SerialScope = "scope", UnitId = "a", UnitScope = "scope", BluetoothAddress = "112233445566" };
        var right = conflict switch
        {
            "serial" => left with { UniqueSerial = "b" },
            "unit" => left with { UnitId = "b" },
            _ => left with { BluetoothAddress = "223344556677" }
        };
        var routes = Resolve(new(), [EndpointDevice(0, "a", left), EndpointDevice(1, "b", right)]);
        Assert.Equal(2, routes.Values.Distinct().Count());
    }

    [Fact]
    public void ABridgeCannotHideAConflictBetweenOtherMembers()
    {
        var a = new PeripheralIdentityEvidence { UniqueSerial = "a", SerialScope = "usb", BluetoothAddress = "112233445566" };
        var bridge = a with { UniqueSerial = "" };
        var c = a with { UniqueSerial = "c" };
        var routes = Resolve(new(), [EndpointDevice(0, "a", a), EndpointDevice(1, "bridge", bridge), EndpointDevice(2, "c", c)]);
        Assert.NotEqual(routes[new(0, Id("a"))], routes[new(2, Id("c"))]);
    }

    [Fact]
    public void SerialsAndUnitsRequireNonemptyMatchingScopes()
    {
        var serial = new PeripheralIdentityEvidence { UniqueSerial = "1234", UnitId = "5678" };
        Assert.Equal(0, PeripheralIdentityResolver.Proof(serial, serial));
        Assert.Equal(0, PeripheralIdentityResolver.Proof(serial with { SerialScope = "vendor-a", UnitScope = "vendor-a" },
            serial with { SerialScope = "vendor-b", UnitScope = "vendor-b" }));
    }

    [Fact]
    public void EndpointNormalizationPreservesInterfaceCollectionAndInstanceSegments()
    {
        const string path = @"\\?\hid#vid_046d&pid_c548&mi_01&col03#7&instance&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var expected = @"hid\vid_046d&pid_c548&mi_01&col03\7&instance&0&0000";
        Assert.Equal(expected, PeripheralIdentityResolver.NormalizeEndpointPath(path));
        Assert.True(PeripheralIdentityResolver.Proof(new() { EndpointPath = path }, new() { EndpointPath = expected.ToUpperInvariant() }) > 0);
        Assert.Equal(0, PeripheralIdentityResolver.Proof(new() { EndpointPath = path },
            new() { EndpointPath = expected.Replace("col03", "col04") }));
        Assert.Equal(0, PeripheralIdentityResolver.Proof(new() { EndpointPath = path },
            new() { EndpointPath = expected.Replace("instance", "different") }));
    }

    [Fact]
    public void PersistedAliasesAreFlattenedValidatedAndBounded()
    {
        var resolver = new PeripheralIdentityResolver();
        resolver.LoadAliases(new Dictionary<string, string>
        {
            [Id("a")] = Id("b"), [Id("b")] = Id("c"), [Id("self")] = Id("self"),
            [Id("cycle1")] = Id("cycle2"), [Id("cycle2")] = Id("cycle1"), [Id("invalid")] = "not-an-id"
        });
        Assert.Equal(Id("c"), resolver.ResolveId(Id("a")));
        Assert.Equal(Id("c"), resolver.ResolveId(Id("b")));
        Assert.Equal(Id("cycle1"), resolver.ResolveId(Id("cycle1")));
        Assert.Equal(2, resolver.Aliases.Count);
        resolver.LoadAliases(Enumerable.Range(0, 3000).ToDictionary(n => Id("alias" + n), _ => Id("target")));
        Assert.Equal(PeripheralIdentityResolver.MaximumAliases, resolver.Aliases.Count);
    }

    [Fact]
    public void ProvenAliasesStillLinkEndpointsAfterRestart()
    {
        var resolver = new PeripheralIdentityResolver();
        var a = EndpointDevice(0, "a", new() { BluetoothAddress = "112233445566" });
        var b = EndpointDevice(1, "b", a.Device.Identity);
        Resolve(resolver, [a, b], selected: [Id("a")]);
        var restored = new PeripheralIdentityResolver(); restored.LoadAliases(resolver.Aliases);
        var routes = Resolve(restored, [a with { Device = a.Device with { Identity = null } },
            b with { Device = b.Device with { Identity = null } }]);
        Assert.All(routes.Values, id => Assert.Equal(Id("a"), id));
    }

    [Fact]
    public void ReusedLosingAliasKeepsItsHistoricalMeaningAndRoutesTheReplacementSeparately()
    {
        var evidence = new PeripheralIdentityEvidence { UniqueSerial = "a", SerialScope = "usb:0001:0002" };
        var a = EndpointDevice(0, "a", evidence); var b = EndpointDevice(1, "b", evidence);
        var resolver = new PeripheralIdentityResolver();
        var previous = Resolve(resolver, [a, b], selected: [Id("a")]);
        var changed = b with { Device = b.Device with { Identity = evidence with { UniqueSerial = "b" } } };
        var history = new Dictionary<string, PeripheralIdentityEvidence[]> { [Id("a")] = [evidence] };
        var routes = Resolve(resolver, [a, changed], selected: [Id("a")], history: history, previous: previous);
        var replacementId = routes[b.Endpoint];
        Assert.Equal(Id("a"), routes[a.Endpoint]);
        Assert.NotEqual(Id("a"), replacementId); Assert.NotEqual(Id("b"), replacementId);
        Assert.Equal(Id("a"), resolver.ResolveId(Id("b")));
        Assert.Equal(Id("a"), resolver.Aliases[Id("b")]);
        history[replacementId] = [changed.Device.Identity!];
        Assert.Equal(replacementId, Resolve(resolver, [a, changed], history: history, previous: routes)[b.Endpoint]);
        var restored = new PeripheralIdentityResolver(); restored.LoadAliases(resolver.Aliases);
        var restarted = Resolve(restored, [a, changed], history: history);
        Assert.Equal(replacementId, restarted[b.Endpoint]);
        Assert.Equal(Id("a"), restored.ResolveId(Id("b")));
        var oldDraft = restored.NormalizeSettings(new() { Order = [Id("b")], CardVisibility = new() { [Id("b")] = false } });
        Assert.Equal(new[] { Id("a") }, oldDraft.Order);
        Assert.False(oldDraft.CardVisibility[Id("a")]);
    }

    [Fact]
    public void ReusedCanonicalEndpointCannotTakeAConflictingHistoricalEntityId()
    {
        var remembered = new PeripheralIdentityEvidence { UniqueSerial = "original", SerialScope = "usb:0001:0002" };
        var replacement = EndpointDevice(0, "canonical", remembered with { UniqueSerial = "replacement" });
        var resolver = new PeripheralIdentityResolver();
        var history = new Dictionary<string, PeripheralIdentityEvidence[]> { [Id("canonical")] = [remembered] };
        var settings = new PeripheralBatterySettings { Order = [Id("canonical")], CardVisibility = new() { [Id("canonical")] = true } };
        var routes = Resolve(resolver, [replacement], settings, [Id("canonical")], history);
        var replacementId = routes[replacement.Endpoint];
        Assert.NotEqual(Id("canonical"), replacementId);
        Assert.Equal(Id("canonical"), resolver.ResolveId(Id("canonical")));
        Assert.True(PeripheralIdentity.IsValid(replacementId));
        history[replacementId] = [replacement.Device.Identity!];
        Assert.Equal(replacementId, Resolve(resolver, [replacement], settings, [Id("canonical")], history, routes)[replacement.Endpoint]);
        var another = replacement with { Device = replacement.Device with { Identity = remembered with { UniqueSerial = "third-device" } } };
        Assert.DoesNotContain(Resolve(resolver, [another], settings, [Id("canonical")], history, routes)[another.Endpoint], history.Keys);
    }

    [Fact]
    public void SettingsNormalizationKeepsSurvivorExplicitChoiceThenFalseAndEarliestOrder()
    {
        var resolver = new PeripheralIdentityResolver();
        var survivor = Id("survivor"); var a = Id("a"); var b = Id("b");
        resolver.LoadAliases(new Dictionary<string, string> { [a] = survivor, [b] = survivor });
        var settings = new PeripheralBatterySettings { Order = [a, Id("unrelated"), survivor, b],
            CardVisibility = new() { [a] = false, [b] = true, [survivor] = true } };
        var normalized = resolver.NormalizeSettings(settings);
        Assert.Equal(new[] { survivor, Id("unrelated") }, normalized.Order);
        Assert.True(normalized.CardVisibility[survivor]);
        settings.CardVisibility.Remove(survivor);
        Assert.False(resolver.NormalizeSettings(settings).CardVisibility[survivor]);
        settings.CardVisibility[survivor] = true;
        Assert.True(resolver.NormalizeSettings(settings).CardVisibility[survivor]);
        Assert.Equal(4, settings.Order.Count);
        Assert.False(settings.CardVisibility[a]);
    }

    [Fact]
    public void EvidenceAndDescriptionQualityNeverEnterPublicSnapshotJson()
    {
        var snapshot = new PeripheralBatterySnapshot { Devices = [new() { Id = Id("a"), Identity = new()
            { UniqueSerial = "private-serial", EndpointPath = "private-path" }, NameQuality = 50, KindQuality = 40 }] };
        var json = JsonSerializer.Serialize(snapshot);
        Assert.Contains("\"version\":2", json);
        Assert.Contains("\"aliases\":{}", json);
        Assert.DoesNotContain("private-serial", json);
        Assert.DoesNotContain("private-path", json);
        Assert.DoesNotContain("Quality", json);
    }
}
