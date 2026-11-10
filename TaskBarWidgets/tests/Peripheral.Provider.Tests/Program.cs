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
Console.WriteLine("Peripheral provider process contract passed: identity filtering, restart, demand cancellation, timeout, initialization journal, quarantine, extension discovery and broken-pipe suspension recovery.");

static JsonElement Parse(string line) { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }
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
