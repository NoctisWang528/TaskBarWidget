using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TaskBarWidgets.Media.Peripherals;

internal sealed class HeadsetControlProvider : IPeripheralBatteryProvider
{
    internal const string PinnedSha256 = "047f0ae26a06d86f6ec5a543166df20ccd47ee07148c915221aa08b97bc190dd";
    private readonly WindowsPeripheralLifetime _lifetime = new();
    private readonly IWindowsPeripheralCatalog _catalog;
    private readonly string _executable;
    private readonly Func<HeadsetControlDevice, CancellationToken, Task<string>> _query;
    private readonly Func<bool> _available;
    private readonly Dictionary<string, PeripheralDeviceSnapshot> _known = new(StringComparer.Ordinal);
    private volatile bool _enabled;
    private volatile bool _disposed;
    public event Action? DevicesChanged;

    /// <summary>installRoot is the directory containing TaskBarWidgets.exe, not its Media subdirectory.</summary>
    public HeadsetControlProvider(string? installRoot = null)
        : this(installRoot, new WindowsPeripheralCatalog(), null, null) { }

    internal HeadsetControlProvider(string? installRoot, IWindowsPeripheralCatalog catalog,
        Func<HeadsetControlDevice, CancellationToken, Task<string>>? query, Func<bool>? available)
    {
        var basePath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        installRoot ??= Path.GetFileName(basePath).Equals("Media", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(basePath)! : basePath;
        _executable = Path.Combine(Path.GetFullPath(installRoot), "Dependencies", "HeadsetControl", "headsetcontrol.exe");
        _catalog = catalog;
        _query = query ?? Query;
        _available = available ?? (() => File.Exists(_executable));
        _catalog.Changed += Changed;
    }

    /// <summary>Call before discovery and whenever settings change. Discovery never runs the CLI.</summary>
    public void Configure(PeripheralBatterySettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var enabled = settings.HeadsetControlEnabled;
        if (_enabled == enabled) return;
        _enabled = enabled;
        Changed();
    }

    private void Changed() { if (!_disposed) { try { DevicesChanged?.Invoke(); } catch { } } }

    public Task<IReadOnlyList<PeripheralDeviceSnapshot>> DiscoverAsync(CancellationToken cancellationToken) =>
        _lifetime.Run<IReadOnlyList<PeripheralDeviceSnapshot>>(async token =>
        {
            if (!_enabled || !_available()) return [];
            var nodes = await _catalog.GetAsync(token).ConfigureAwait(false);
            if (!_enabled) return [];
            return Discover(HeadsetControlDevices.Map(nodes), nodes);
        }, cancellationToken);

    private PeripheralDeviceSnapshot[] Discover(Dictionary<string, HeadsetControlDevice> map, IReadOnlyList<WindowsPeripheralNode> nodes)
    {
        var present = map.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var id in _known.Keys.Where(id => !present.Contains(id)).ToArray())
            _known[id] = _known[id] with { State = "disconnected", Stale = _known[id].UpdatedAt.HasValue };
        foreach (var (id, device) in map)
        {
            var previous = _known.GetValueOrDefault(id) ?? new() { Id = id };
            WindowsPeripheralHistory.Remember(_known, previous with
            {
                Name = device.Name, Kind = "headset", Connection = "usb", State = "online", CanRead = true,
                Source = "headsetcontrol-4.1.0", Compatibility = "extension", Stale = previous.Stale
            }, present);
        }
        var result = _known.Values.Where(d => present.Contains(d.Id)).ToList();
        var groups = WindowsPeripheralBatteryProvider.Groups(nodes);
        foreach (var id in _known.Keys.Where(id => !map.ContainsKey(id)).ToArray())
        {
            var exists = groups.TryGetValue(id, out var group);
            if (exists && group!.Any(n => n.BluetoothLE || WindowsPeripheralIdentity.Percentage(n.Battery).HasValue)) continue;
            var previous = _known[id];
            _known[id] = previous with
            {
                State = exists ? "unavailable" : "disconnected", CanRead = false, Stale = previous.UpdatedAt.HasValue
            };
            result.Add(_known[id]);
        }
        return WindowsPeripheralHistory.CurrentFirst(result, present);
    }

    public Task<IReadOnlyList<PeripheralDeviceSnapshot>> ReadAsync(IReadOnlySet<string> deviceIds,
        PeripheralBatterySettings settings, CancellationToken cancellationToken) =>
        _lifetime.Run<IReadOnlyList<PeripheralDeviceSnapshot>>(async token =>
        {
            if (!_enabled || !settings.HeadsetControlEnabled) return [];
            var nodes = await _catalog.GetAsync(token).ConfigureAwait(false);
            var map = HeadsetControlDevices.Map(nodes);
            Discover(map, nodes);
            foreach (var id in deviceIds)
            {
                token.ThrowIfCancellationRequested();
                if (!_enabled) break;
                if (!map.TryGetValue(id, out var device) || !_known.ContainsKey(id))
                {
                    if (_known.TryGetValue(id, out var old))
                        _known[id] = old with { State = "disconnected", Stale = old.UpdatedAt.HasValue, CanRead = false };
                    continue;
                }
                try
                {
                    var output = await _query(device, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    // The upstream CLI selects the first matching VID/PID. Validate topology again
                    // before attributing a result; an arrival/removal during the call invalidates it.
                    var after = HeadsetControlDevices.Map(await _catalog.GetAsync(token).ConfigureAwait(false));
                    if (!_enabled || !after.TryGetValue(id, out var current) || current.Pair != device.Pair ||
                        !current.Paths.SequenceEqual(device.Paths, StringComparer.Ordinal))
                    {
                        _known[id] = _known[id] with { State = "unavailable", Stale = _known[id].UpdatedAt.HasValue };
                        Changed();
                        continue;
                    }
                    var reading = HeadsetControlDevices.Reading(output, device.Pair, out var disconnected);
                    var previous = _known[id];
                    _known[id] = reading is not null ? previous with
                    {
                        Batteries = [reading], UpdatedAt = DateTimeOffset.UtcNow, State = "online", Stale = false
                    } : previous with { State = disconnected ? "disconnected" : "unavailable", Stale = previous.UpdatedAt.HasValue };
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    _known[id] = _known[id] with { State = "unavailable", Stale = _known[id].UpdatedAt.HasValue };
                }
                catch (Exception ex) when (WindowsPeripheralBatteryProvider.Recoverable(ex)
                    || ex is System.ComponentModel.Win32Exception or CryptographicException)
                {
                    _known[id] = _known[id] with { State = "unavailable", Stale = _known[id].UpdatedAt.HasValue };
                }
            }
            return deviceIds.Where(_known.ContainsKey).Select(id => _known[id]).ToArray();
        }, cancellationToken);

    internal static string[] Arguments(string pair) => ["--device", pair, "--battery", "--output", "standard"];

    private async Task<string> Query(HeadsetControlDevice device, CancellationToken token)
    {
        // Never search PATH, accept a configured executable, or execute --version on an unknown file.
        await using var file = new FileStream(_executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        if (!Convert.ToHexString(hash).Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("HeadsetControl 4.1.0 checksum mismatch.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_executable)!
        };
        foreach (var argument in Arguments(device.Pair)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        timeout.Token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("HeadsetControl could not start.");
        void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (System.ComponentModel.Win32Exception)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            catch (InvalidOperationException) { }
        }
        using var registration = timeout.Token.Register(Kill);
        var stdout = ReadBounded(process.StandardOutput, timeout.Token);
        var stderr = ReadBounded(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("HeadsetControl battery query failed.");
            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            timeout.Cancel();
            Kill();
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[2048];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + length > 65536) throw new IOException("HeadsetControl output exceeds limit.");
            output.Append(buffer, 0, length);
        }
        return output.ToString();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        DevicesChanged = null;
        _catalog.Changed -= Changed;
        return _lifetime.DisposeAsync(_catalog.StopAsync);
    }
}
