using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TaskBarWidgets.Media.Peripherals;

// HID metadata and vendor I/O can block inside a driver. Only this owned helper
// may be terminated on timeout; the desktop host and users' applications aren't touched.
internal sealed class NativePeripheralBatteryProvider(string applicationRoot) : IPeripheralBatteryProvider, IPeripheralBatteryIdle
{
    private const int MaximumLineCharacters = 1024 * 1024;
    private readonly string _executable = Path.Combine(applicationRoot, "TaskBarWidgets.PeripheralProbe.exe");
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, string> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeripheralDeviceSnapshot> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initializations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _quarantine = new(StringComparer.Ordinal);
    private string? _inProgress;
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Task? _drainErrors;
    private int _requestId;
    private bool _disposed;
    private bool _stopping;
    private volatile bool _vendorExtensions;
    private DateTimeOffset _lastUse;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public event Action? DevicesChanged { add { } remove { } }

    internal void Configure(PeripheralBatterySettings settings) => _vendorExtensions = settings.VendorExtensionsEnabled;

    public Task<IReadOnlyList<PeripheralDeviceSnapshot>> DiscoverAsync(CancellationToken cancellationToken) =>
        QueryAsync("discover", [], _vendorExtensions, cancellationToken);

    public Task<IReadOnlyList<PeripheralDeviceSnapshot>> ReadAsync(IReadOnlySet<string> deviceIds,
        PeripheralBatterySettings settings, CancellationToken cancellationToken) =>
        QueryAsync("read", deviceIds, settings.VendorExtensionsEnabled, cancellationToken);

    private async Task<IReadOnlyList<PeripheralDeviceSnapshot>> QueryAsync(string operation,
        IEnumerable<string> ids, bool vendorExtensions, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(_executable)) throw new FileNotFoundException("外设采集组件缺失。", _executable);
            var expired = _quarantine.Where(p => p.Value <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray();
            foreach (var identity in expired) _quarantine.Remove(identity);
            var started = StartProcess();
            if ((started || expired.Length != 0) && operation == "read")
                await ExecuteAsync("discover", [], vendorExtensions, linked.Token).ConfigureAwait(false);
            var result = await ExecuteAsync(operation, ids, vendorExtensions, linked.Token).ConfigureAwait(false);
            if (operation == "discover")
            {
                var receivers = result.Where(d => d.Source == "hidpp20" &&
                    _identities.TryGetValue(d.Id, out var key) && !key.Contains("|unit=", StringComparison.Ordinal)).ToArray();
                var receiverIds = receivers.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                var devices = result.Where(d => !receiverIds.Contains(d.Id)).ToDictionary(d => d.Id, StringComparer.Ordinal);
                foreach (var receiver in receivers)
                {
                    var parent = _identities[receiver.Id];
                    // A receiver proves presence, not whether every paired unit is
                    // awake. Keep its known units stale until they answer again.
                    foreach (var cached in _known.Values.Where(d => _identities.TryGetValue(d.Id, out var key) && IsHidppChild(parent, key)))
                        devices.TryAdd(cached.Id, cached with { State = "unavailable", Stale = true });
                    if (!receiver.CanRead) continue;
                    var units = await ExecuteAsync("identify", [receiver.Id], vendorExtensions, linked.Token).ConfigureAwait(false);
                    foreach (var unit in units.Where(d => d.Source == "hidpp20" &&
                        _identities.TryGetValue(d.Id, out var key) && IsHidppChild(parent, key))) devices[unit.Id] = unit;
                }
                result = devices.Values.ToArray();
            }
            _lastUse = DateTimeOffset.UtcNow;
            ScheduleIdle();
            return result;
        }
        catch (OperationCanceledException) when (!linked.IsCancellationRequested)
        {
            if (!string.IsNullOrEmpty(_inProgress) && _quarantine.Count < 256)
                _quarantine[_inProgress] = DateTimeOffset.UtcNow.AddMinutes(15);
            await StopProcessAsync().ConfigureAwait(false);
            throw new TimeoutException("外设采集超时，已回收采集进程。");
        }
        catch
        { await StopProcessAsync().ConfigureAwait(false); throw; }
        finally { _serial.Release(); }
    }

    private async Task<IReadOnlyList<PeripheralDeviceSnapshot>> ExecuteAsync(string operation,
        IEnumerable<string> ids, bool vendorExtensions, CancellationToken token)
    {
            _inProgress = null;
            var requestId = ++_requestId;
            var selectedIds = ids.Where(_identities.ContainsKey).ToArray();
            var selected = selectedIds.Select(id => _identities[id]).ToArray();
            var request = JsonSerializer.Serialize(new { version = 1, requestId, operation, deviceIds = selected,
                vendorExtensionsEnabled = vendorExtensions, attemptedInitializations = _initializations.ToArray(),
                excludedIdentities = _quarantine.Keys.ToArray() }, Json);
            // Progress messages make it possible to distinguish a bad interface;
            // they never extend the total budget indefinitely.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var budget = selectedIds.Sum(id => _known.GetValueOrDefault(id)?.Source == "hidpp20" ? 20 : 5);
            timeout.CancelAfter(TimeSpan.FromSeconds(operation == "discover" ? 15 : Math.Clamp(budget, 8, 60)));
            await _process!.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            ProbeReply? reply;
            while (true)
            {
                var line = await ReadBoundedLineAsync(_process.StandardOutput, timeout.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var wireVersion) || wireVersion != 1 ||
                    !root.TryGetProperty("requestId", out var correlation) || !correlation.TryGetInt32(out var correlatedId) || correlatedId != requestId)
                    throw new InvalidDataException("外设采集响应不匹配。");
                if (root.TryGetProperty("event", out var eventName))
                {
                    if (eventName.GetString() == "initialized" && root.TryGetProperty("identity", out var initialized))
                        RememberInitialization(initialized.GetString());
                    if (eventName.GetString() == "progress" && root.TryGetProperty("identity", out var progress))
                        _inProgress = progress.GetString() is { Length: > 0 and <= 4096 } identity ? identity : null;
                    continue;
                }
                reply = JsonSerializer.Deserialize<ProbeReply>(line, Json);
                if (reply is null || reply.Version != 1 || reply.RequestId != requestId)
                    throw new InvalidDataException("外设采集响应不匹配。");
                break;
            }
            foreach (var key in reply.Initialized ?? []) RememberInitialization(key);
            if (!string.IsNullOrWhiteSpace(reply.Error) && (reply.Devices?.Length ?? 0) == 0)
                throw new IOException("外设采集失败。" + reply.Error[..Math.Min(160, reply.Error.Length)]);
            var result = new List<PeripheralDeviceSnapshot>();
            foreach (var item in (reply.Devices ?? []).Take(256))
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Identity) || item.Identity.Length > 1024 || string.IsNullOrWhiteSpace(item.Name)) continue;
                if (operation == "identify" && (item.Source != "hidpp20" || !selected.Any(parent => IsHidppChild(parent, item.Identity)))) continue;
                if (operation == "read" && !selected.Contains(item.Identity, StringComparer.Ordinal)) continue;
                var id = PeripheralIdentity.FromKey(item.Identity);
                if (!_identities.ContainsKey(id) && _identities.Count >= 1024)
                    _identities.Remove(_identities.Keys.First(key => !_known.ContainsKey(key)));
                _identities[id] = item.Identity;
                var legacyIds = new List<string>(4);
                foreach (var key in (item.LegacyIdentities ?? []).Take(4))
                {
                    if (string.IsNullOrWhiteSpace(key) || key.Length > 1024) continue;
                    var alias = PeripheralIdentity.FromKey(key);
                    if (alias == id || legacyIds.Contains(alias)) continue;
                    if (_identities.TryGetValue(alias, out var target) ? target != item.Identity : _identities.Count >= 1024) continue;
                    _identities[alias] = item.Identity;
                    legacyIds.Add(alias);
                }
                if (operation == "read")
                    id = selectedIds.FirstOrDefault(selectedId => _identities.GetValueOrDefault(selectedId) == item.Identity) ?? id;
                var device = new PeripheralDeviceSnapshot { Id = id, Name = item.Name, Kind = item.Kind,
                    Brand = PeripheralIdentity.Brand(item.Name, item.Identity, item.Source),
                    LegacyIds = legacyIds.ToArray(),
                    Connection = item.Connection, State = item.State, CanRead = item.CanRead,
                    Batteries = item.Batteries ?? [], UpdatedAt = item.UpdatedAt,
                    Stale = item.State != "online" || item.Stale, Source = item.Source, Compatibility = item.Compatibility };
                result.Add(device);
                _known.Remove(id); _known[id] = device;
                if (_known.Count > 256) _known.Remove(_known.Keys.First());
            }
            if (operation == "discover" && _quarantine.Count != 0)
            {
                // Quarantined interfaces weren't inspected; their absence is not
                // evidence of a physical disconnect. A complete later discovery resolves it.
                var found = result.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                result.AddRange(_known.Values.Where(d => !found.Contains(d.Id)).Select(d => d with { State = "unavailable", Stale = true }));
            }
            return result;
    }

    private void RememberInitialization(string? identity)
    {
        if (identity is { Length: > 0 and <= 1024 } && _initializations.Count < 4096) _initializations.Add(identity);
    }

    internal static bool IsHidppChild(string parent, string identity)
    {
        var prefix = parent + "|unit=";
        if (!identity.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var unit = identity[prefix.Length..];
        return unit.Length == 27 && unit[8..15] == "|model=" && unit[..8] is not ("00000000" or "ffffffff") &&
            unit[15..] != "000000000000" && (unit[..8] + unit[15..]).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private bool StartProcess()
    {
        if (_stopping && _process is { HasExited: false }) throw new IOException("外设采集组件仍在退出，稍后重试。");
        if (_process is { HasExited: false }) return false;
        _process?.Dispose();
        _stopping = false;
        var start = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(_executable)! };
        start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _process = Process.Start(start) ?? throw new IOException("无法启动外设采集组件。");
        // Drain bounded chunks rather than collecting unbounded device diagnostics.
        _drainErrors = DrainAsync(_process.StandardError);
        return true;
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        if (process is null) return;
        _stopping = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (!process.HasExited)
            {
                // Close the pipe without a synchronous StreamWriter flush into a
                // stalled reader. A retiring worker blocks replacement until it exits.
                process.StandardInput.BaseStream.Dispose();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); }
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            if (_drainErrors is not null) await _drainErrors.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally
        {
            if (process.HasExited)
            { process.Dispose(); _process = null; _drainErrors = null; _stopping = false; }
        }
    }

    private System.Threading.Timer? _idleStop;
    private void ScheduleIdle()
    {
        _idleStop ??= new System.Threading.Timer(_ => _ = StopIfIdleAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _idleStop.Change(TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
    }

    private async Task StopIfIdleAsync()
    {
        if (!await _serial.WaitAsync(0).ConfigureAwait(false)) return;
        try { if (!_disposed && DateTimeOffset.UtcNow - _lastUse >= TimeSpan.FromSeconds(89)) await StopProcessAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        { Trace.WriteLine("Peripheral idle cleanup: " + error.GetType().Name); }
        finally { _serial.Release(); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { var buffer = new char[1024]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    internal static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) == 0) throw new EndOfStreamException();
            if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= MaximumLineCharacters) throw new InvalidDataException("外设采集响应过大。");
            line.Append(buffer[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _serial.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _idleStop?.Dispose(); await StopProcessAsync().ConfigureAwait(false); }
        finally { _serial.Release(); }
    }

    public async Task SuspendAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try { _idleStop?.Change(Timeout.Infinite, Timeout.Infinite); await StopProcessAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        { Trace.WriteLine("Peripheral demand cleanup: " + error.GetType().Name); }
        finally { _serial.Release(); }
    }

    private sealed record ProbeReply
    {
        public int Version { get; init; }
        public int RequestId { get; init; }
        public ProbeDevice[] Devices { get; init; } = [];
        public string[]? Initialized { get; init; }
        public string Error { get; init; } = "";
    }
    private sealed record ProbeDevice
    {
        public string Identity { get; init; } = "";
        public string[]? LegacyIdentities { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "unknown";
        public string Connection { get; init; } = "usb-2.4g";
        public string State { get; init; } = "unavailable";
        public PeripheralBatteryReading[] Batteries { get; init; } = [];
        public DateTimeOffset? UpdatedAt { get; init; }
        public bool Stale { get; init; }
        public bool CanRead { get; init; }
        public string Source { get; init; } = "hid";
        public string Compatibility { get; init; } = "implemented";
    }
}
