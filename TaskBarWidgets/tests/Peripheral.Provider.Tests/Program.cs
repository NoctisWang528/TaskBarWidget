using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TaskBarWidgets.Media.Peripherals;

if (args.Length == 3 && args[0] == "--device-report")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    await using var service = new PeripheralBatteryService(
        [new NativePeripheralBatteryProvider(Path.GetFullPath(args[1])), new WindowsPeripheralBatteryProvider()], null);
    var finished = new TaskCompletionSource<PeripheralBatterySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    service.Changed += () =>
    {
        var currentSnapshot = service.Snapshot;
        if (currentSnapshot.Revision > 0 && !currentSnapshot.Refreshing) finished.TrySetResult(currentSnapshot);
    };
    service.SetSettingsActive(true);
    service.Start();
    var snapshot = await finished.Task.WaitAsync(timeout.Token);
    await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    foreach (var device in snapshot.Devices)
        Console.WriteLine($"{device.Name}: {device.Connection} {device.State} {string.Join(", ", device.Batteries.Select(b => $"{b.Role}={b.Percentage}"))}");
    if (snapshot.Error.Length > 0) throw new InvalidOperationException(snapshot.Error);
    return;
}

if (args.Length == 1 && args[0] == "--windows-properties")
{
    foreach (var property in WindowsPeripheralCatalog.Properties)
    {
        try { _ = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(null, [property], Windows.Devices.Enumeration.DeviceInformationKind.Device); Console.WriteLine($"OK {property}"); }
        catch (Exception ex) { Console.WriteLine($"FAILED {property}: {ex.Message}"); }
    }
    return;
}

if (args.Length == 2 && args[0] == "--windows-report")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var catalog = new WindowsPeripheralCatalog();
    await using var windows = new WindowsPeripheralBatteryProvider(catalog);
    var nodes = await catalog.GetAsync(timeout.Token);
    var found = await windows.DiscoverAsync(timeout.Token);
    var windowsIds = found.Where(d => d.CanRead).Select(d => d.Id).ToHashSet();
    await windows.SetDemandAsync(windowsIds, timeout.Token);
    var readings = await windows.ReadAsync(windowsIds, new(), timeout.Token);
    var endpointProperties = new List<object>();
    foreach (var node in nodes.Where(n => n.Bluetooth && !n.Pnp && n.Address is not null))
    {
        var info = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(node.Path,
            WindowsPeripheralCatalog.Properties, Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);
        endpointProperties.Add(new { node.Name, Properties = info.Properties.Where(p => p.Key.Contains("Category") || p.Key.Contains("Cod.")).ToDictionary() });
    }
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { nodes, found, readings, endpointProperties }, new JsonSerializerOptions { WriteIndented = true }));
    foreach (var device in readings) Console.WriteLine($"{device.Name}: {device.State} {string.Join(", ", device.Batteries.Select(b => $"{b.Role}={b.Percentage}"))}");
    return;
}

if (args.Length == 3 && args[0] == "--hardware-report")
{
    await HardwareReport.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
    return;
}
if (args.Length != 1) throw new ArgumentException("Pass the isolated fixture directory.");
var directory = Path.GetFullPath(args[0]);
var transcript = Path.Combine(directory, "requests.jsonl");
await VerifyDockChildrenAsync(directory);
await VerifyRogNamesAsync(directory);
File.WriteAllText(transcript, "");
File.WriteAllText(Path.Combine(directory, "mode.txt"), "normal");
await using var provider = new NativePeripheralBatteryProvider(directory);
provider.Configure(new() { VendorExtensionsEnabled = true });
var devices = await provider.DiscoverAsync(CancellationToken.None);
Require(devices.Count == 1 && devices[0].Name == "Synthetic mouse", "Only identified children of the requested receiver may be exposed.");
var ids = devices.Select(d => d.Id).ToHashSet();
var reading = await provider.ReadAsync(ids, new(), CancellationToken.None);
Require(reading.Single().Batteries.Single().Percentage == 42, "A discovered child must be readable.");
var legacyId = PeripheralIdentity.FromKey("fixture:legacy-child");
var legacyReading = await provider.ReadAsync(new HashSet<string> { legacyId }, new(), CancellationToken.None);
Require(legacyReading.Single().Id == legacyId && legacyReading.Single().Batteries.Single().Percentage == 42,
    "A proven legacy identity must query the current device while preserving its configured ID.");
var beforeRestart = ReadTranscript(transcript).Length;
await provider.SuspendAsync();
reading = await provider.ReadAsync(ids, new(), CancellationToken.None);
Require(reading.Single().Batteries.Single().Percentage == 42, "A restarted worker must rediscover before reading cached identities.");
var restart = ReadTranscript(transcript).Skip(beforeRestart).Select(Parse).ToArray();
Require(restart[0].GetProperty("operation").GetString() == "discover" && restart[1].GetProperty("operation").GetString() == "read", "Restart discovery ordering.");

File.WriteAllText(Path.Combine(directory, "mode.txt"), "stall");
using (var cancellation = new CancellationTokenSource())
{
    var beforeWaiting = ReadTranscript(transcript).Length;
    var waiting = provider.ReadAsync(ids, new(), cancellation.Token);
    await WaitUntil(() => ReadTranscript(transcript).Length > beforeWaiting);
    await Task.Delay(100);
    var elapsed = Stopwatch.StartNew(); cancellation.Cancel();
    try { await waiting; throw new InvalidOperationException("Cancelled query unexpectedly succeeded."); }
    catch (OperationCanceledException) { }
    Require(elapsed.Elapsed < TimeSpan.FromSeconds(4), "Cancelling a blocked query must promptly release the owned worker.");
}

// A true protocol timeout must preserve both the once-only initialization journal
// and the offending interface exclusion across worker replacement.
var timeoutStarted = Stopwatch.StartNew();
try { await provider.ReadAsync(ids, new(), CancellationToken.None); throw new InvalidOperationException("Stalled query succeeded."); }
catch (TimeoutException) { }
Require(timeoutStarted.Elapsed < TimeSpan.FromSeconds(24), "HID++ timeout budget is bounded.");
File.WriteAllText(Path.Combine(directory, "mode.txt"), "normal");
await provider.DiscoverAsync(CancellationToken.None);
var requests = ReadTranscript(transcript).Select(Parse).ToArray();
Require(requests[0].GetProperty("vendorExtensionsEnabled").GetBoolean(), "Discovery must honor enabled vendor extensions.");
var lastDiscover = requests.Last(r => r.GetProperty("operation").GetString() == "discover");
Require(lastDiscover.GetProperty("attemptedInitializations").EnumerateArray().Any(), "Initialization journal survives worker cancellation and timeout.");
Require(lastDiscover.GetProperty("excludedIdentities").EnumerateArray().Any(v => v.GetString() == "fixture:blocked-interface"), "Timeout quarantines the interface identified by progress.");
await provider.SuspendAsync();

// A broken input pipe and unflushed buffered text must not hang suspension.
File.WriteAllText(Path.Combine(directory, "mode.txt"), "close-input");
File.Delete(Path.Combine(directory, "input-closed"));
await provider.ReadAsync(ids, new(), CancellationToken.None);
await WaitUntil(() => File.Exists(Path.Combine(directory, "input-closed")));
var processField = typeof(NativePeripheralBatteryProvider).GetField("_process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
var broken = (Process)processField.GetValue(provider)!;
try
{
    broken.StandardInput.AutoFlush = false;
    await broken.StandardInput.WriteAsync("buffered fixture text");
    await provider.SuspendAsync();
    Require(processField.GetValue(provider) is null, "Broken-input shutdown must reap only the owned worker.");
}
finally
{
    try { if (!broken.HasExited) { broken.Kill(); await broken.WaitForExitAsync(); } }
    catch (InvalidOperationException) { } // The successful shutdown already disposed its process handle.
}
File.WriteAllText(Path.Combine(directory, "mode.txt"), "normal");
reading = await provider.ReadAsync(ids, new(), CancellationToken.None);
Require(reading.Single().Batteries.Single().Percentage == 42, "Demand recovers after the retained worker exits.");
await provider.SuspendAsync();
Console.WriteLine("Peripheral provider process contract passed: dock child filtering/replacement, Omni name retention/refresh/retirement, identity filtering, restart, demand cancellation, timeout, initialization journal, quarantine, extension discovery and broken-pipe suspension recovery.");

var managedDirectory = Path.Combine(directory, "Media", "PeripheralHost");
Directory.CreateDirectory(managedDirectory);
File.Copy(Path.Combine(directory, "TaskBarWidgets.PeripheralProbe.exe"),
    Path.Combine(managedDirectory, "TaskBarWidgets.PeripheralHost.exe"), true);
var managedTranscript = Path.Combine(managedDirectory, "requests.jsonl");
File.WriteAllText(managedTranscript, "");
await using (var managed = new NativePeripheralBatteryProvider(directory, vigilWatt: true))
{
    var found = await managed.DiscoverAsync(default);
    Require(found.Count == 2, "Full discovery inventories both fixture devices.");
    var selected = found.Where(d => d.Name == "Synthetic first").Select(d => d.Id).ToHashSet();
    Require(selected.Count == 1, "Select exactly one device for restart.");
    await managed.SuspendAsync();
    var offset = ReadTranscript(managedTranscript).Length;
    var selectedReadings = await managed.ReadAsync(selected, new(), default);
    Require(selectedReadings.Count == 1 && selectedReadings[0].Batteries.Single().Percentage == 42,
        "Selected managed device remains readable after restart.");
    foreach (var request in ReadTranscript(managedTranscript).Skip(offset).Select(Parse))
    {
        Require(request.GetProperty("deviceIds").EnumerateArray().Select(v => v.GetString()).SequenceEqual(["vigil:fixture:first"]),
            "Restart discovery and reads must retain selected identities.");
        Require(request.GetProperty("queryIds").EnumerateArray().Select(v => v.GetString()).SequenceEqual(["query:first"]),
            "Restart discovery must retain the selected protocol child boundary.");
    }
}
Console.WriteLine("Managed peripheral restart demand boundary passed.");

static JsonElement Parse(string line) { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }
static async Task VerifyDockChildrenAsync(string directory)
{
    var mode = Path.Combine(directory, "mode.txt");
    File.WriteAllText(mode, "dock-normal");
    await using var provider = new NativePeripheralBatteryProvider(directory);
    var found = await provider.DiscoverAsync(default);
    Require(found.Count == 1 && found[0].Name == "Razer Basilisk V4 Pro",
        "A Razer dock must expose only its own proven paired mouse, never the receiver or unrelated/malformed children.");
    var id = found[0].Id;
    var read = await provider.ReadAsync(new HashSet<string> { id }, new(), default);
    Require(read.Single().Id == id && read.Single().Batteries.Single().Percentage == 73, "A proven Razer dock child must remain readable.");
    File.WriteAllText(mode, "dock-sleep");
    found = await provider.DiscoverAsync(default);
    Require(found.Single().Id == id && found[0].Stale && found[0].State == "unavailable",
        "A present dock with no fresh child reply preserves only its previously proven mouse as stale.");
    File.WriteAllText(mode, "dock-changed");
    found = await provider.DiscoverAsync(default);
    var replacement = found.Single(d => !d.Stale);
    Require(replacement.Id != id && found.Single(d => d.Id == id).Stale,
        "A different paired serial creates a different identity and cannot inherit the previous mouse's state.");
    File.WriteAllText(mode, "dock-absent");
    found = await provider.DiscoverAsync(default);
    Require(found.Count == 0, "An absent dock must not keep its children present.");
}
static async Task VerifyRogNamesAsync(string directory)
{
    var mode = Path.Combine(directory, "mode.txt");
    File.WriteAllText(mode, "rog-named");
    await using var provider = new NativePeripheralBatteryProvider(directory);
    var discovered = await provider.DiscoverAsync(default);
    Require(discovered.Single().Name == "ROG Omni keyboard", "An unread receiver must not invent its paired keyboard model.");
    Require(provider.RetiredDeviceIds.SetEquals(new[] { PeripheralIdentity.FromKey(
        "hid:v1|serial=41424344|vid=0b05|pid=1ace|mi=2|usage=ff01:0001|channel=rog-report3") }),
        "Retirement replies may retire only an exact legacy Omni endpoint, not unrelated hardware or the live keyboard.");
    var ids = discovered.Select(d => d.Id).ToHashSet();
    var read = await provider.ReadAsync(ids, new(), default);
    Require(read.Single().Name == "ROG AZOTH EXTREME", "The successful native reading supplies the confirmed paired model.");
    Require(read.Single().ModelIdentity == "6978" && read.Single().ModelIdentityConfirmed, "Only validated native paired-model proof reaches the snapshot.");
    discovered = await provider.DiscoverAsync(default);
    Require(discovered.Single().Name == "ROG AZOTH EXTREME", "ROG rediscovery must retain the proven name for the exact same identity and source.");
    Require(discovered.Single().ModelIdentity == "6978" && !discovered.Single().ModelIdentityConfirmed, "Rediscovery retains model context without claiming a fresh identity query.");
    await provider.SuspendAsync();
    File.WriteAllText(mode, "rog-unavailable");
    read = await provider.ReadAsync(ids, new(), default);
    Require(read.Single().Name == "ROG AZOTH EXTREME", "Worker restart and unavailable reads must retain the proven same-device name.");
    File.WriteAllText(mode, "rog-changed");
    read = await provider.ReadAsync(ids, new(), default);
    Require(read.Single().Name == "ROG AZOTH", "A newly confirmed paired model must replace the old display name.");
    discovered = await provider.DiscoverAsync(default);
    Require(discovered.Single().Name == "ROG AZOTH", "Rediscovery must retain the latest confirmed model, not the original model.");
    File.WriteAllText(mode, "rog-unknown-model");
    read = await provider.ReadAsync(ids, new(), default);
    Require(read.Single().Name == "ROG Omni keyboard", "A fresh valid read of an unmapped model must clear the previous model name.");
    discovered = await provider.DiscoverAsync(default);
    Require(discovered.Single().Name == "ROG Omni keyboard", "Rediscovery must not resurrect a superseded model name.");
    File.WriteAllText(mode, "rog-offline-confirmed");
    read = await provider.ReadAsync(ids, new(), default);
    Require(read.Single().Name == "ROG AZOTH EXTREME SPECIAL EDITION" && read.Single().ModelIdentity == "7409" &&
        read.Single().ModelIdentityConfirmed && read.Single().Batteries.Length == 0,
        "A coherent unavailable response can confirm a model without fabricating battery data.");
    File.WriteAllText(mode, "rog-invalid-model");
    read = await provider.ReadAsync(ids, new(), default);
    Require(read.Single().Name == "ROG AZOTH EXTREME SPECIAL EDITION" && !read.Single().ModelIdentityConfirmed,
        "An unrecognized paired PID cannot overwrite previously confirmed model metadata.");
    File.WriteAllText(mode, "rog-offline-unknown");
    read = await provider.ReadAsync(ids, new(), default);
    Require(read.Single().Name == "ROG Omni keyboard" && read.Single().ModelIdentity == "6918" && read.Single().ModelIdentityConfirmed,
        "A confirmed but unnamed new model clears the old name even when battery data is unavailable.");
    File.WriteAllText(mode, "rog-named");
    _ = await provider.ReadAsync(ids, new(), default);
    File.WriteAllText(mode, "rog-other-source");
    discovered = await provider.DiscoverAsync(default);
    Require(discovered.Single().Name == "ROG Omni keyboard", "Name retention must not cross provider sources even when identity text matches.");
}
static string[] ReadTranscript(string file)
{
    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.TrimEnd().EndsWith('}')).ToArray();
}
static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
static async Task WaitUntil(Func<bool> condition)
{
    var elapsed = Stopwatch.StartNew();
    while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Fixture request did not arrive."); await Task.Delay(10); }
}
