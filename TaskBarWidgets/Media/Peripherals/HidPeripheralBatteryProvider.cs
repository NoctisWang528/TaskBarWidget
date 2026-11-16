using System.IO;
using System.Text.Json;

namespace TaskBarWidgets.Media.Peripherals;

// The two helpers share an ownership boundary. A failed/retiring helper must
// release its handles before an interface is assigned to another implementation.
internal sealed class HidPeripheralBatteryProvider(string applicationRoot) : IPeripheralBatteryProvider, IPeripheralBatteryIdle, IPeripheralBatteryRetirements
{
    private readonly NativePeripheralBatteryProvider _vigil = new(applicationRoot, vigilWatt: true);
    private readonly NativePeripheralBatteryProvider _native = new(applicationRoot);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string> _vigilIds = new(StringComparer.Ordinal);
    private HashSet<string> _nativeIds = new(StringComparer.Ordinal);
    private string[] _ownedPaths = [];
    internal string[] OwnedPaths => Volatile.Read(ref _ownedPaths);
    private JsonElement[] _diagnostics = [];
    public event Action? DevicesChanged;
    public IReadOnlySet<string> RetiredDeviceIds => _native.RetiredDeviceIds;

    internal JsonElement[] Diagnostics => Volatile.Read(ref _diagnostics);
    internal void Configure(PeripheralBatterySettings settings) => _native.Configure(settings);

    public async Task<IReadOnlyList<PeripheralDeviceSnapshot>> DiscoverAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _native.SuspendAsync().ConfigureAwait(false);
            await _vigil.SuspendAsync().ConfigureAwait(false);
            if (_native.HasLiveProcess || _vigil.HasLiveProcess)
                throw new IOException("外设采集组件仍在退出，稍后重试。");
            _vigil.ExcludePaths([]);
            IReadOnlyList<PeripheralDeviceSnapshot> upstream = [];
            string? failure = null;
            try { upstream = await _vigil.DiscoverAsync(token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (_vigil.HasLiveProcess) throw;
                failure = ex.GetType().Name;
            }
            _native.ExcludePaths(failure is null ? _vigil.ClaimedPaths : []);
            var fallback = await _native.DiscoverAsync(token).ConfigureAwait(false);
            // A helper restarted by its idle timeout must not rediscover interfaces
            // already assigned to fallback until the next complete discovery pass.
            _vigil.ExcludePaths(fallback.Select(d => d.Identity?.EndpointPath ?? "").Where(p => p.Length > 0));
            var fallbackPaths = fallback.Select(d => PeripheralIdentityResolver.NormalizeEndpointPath(d.Identity?.EndpointPath)).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
            var verifiedPaths = _vigil.ClaimedPaths.Select(PeripheralIdentityResolver.NormalizeEndpointPath).ToHashSet(StringComparer.Ordinal);
            upstream = upstream.Where(d => verifiedPaths.Contains(PeripheralIdentityResolver.NormalizeEndpointPath(d.Identity?.EndpointPath))
                || !fallbackPaths.Contains(PeripheralIdentityResolver.NormalizeEndpointPath(d.Identity?.EndpointPath))).ToArray();
            Volatile.Write(ref _ownedPaths, _vigil.ClaimedPaths.Concat(fallback.Select(d => d.Identity?.EndpointPath ?? "")).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            _vigilIds = upstream.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            _nativeIds = fallback.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            CaptureDiagnostics(failure);
            return upstream.Concat(fallback).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PeripheralDeviceSnapshot>> ReadAsync(IReadOnlySet<string> ids,
        PeripheralBatterySettings settings, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = new List<PeripheralDeviceSnapshot>();
            var upstream = ids.Where(_vigilIds.Contains).ToHashSet(StringComparer.Ordinal);
            var fallback = ids.Where(_nativeIds.Contains).ToHashSet(StringComparer.Ordinal);
            string? failure = null;
            if (upstream.Count != 0)
            {
                try { result.AddRange(await _vigil.ReadAsync(upstream, settings, token).ConfigureAwait(false)); }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                { failure = ex.GetType().Name; DevicesChanged?.Invoke(); }
                // Claims remain excluded on a failed read, including while a
                // driver is slow to release a terminated process's handles.
                _native.ExcludePaths(_vigil.ClaimedPaths);
            }
            else await _vigil.SuspendAsync().ConfigureAwait(false);
            if (fallback.Count != 0)
                result.AddRange(await _native.ReadAsync(fallback, settings, token).ConfigureAwait(false));
            CaptureDiagnostics(failure);
            return result;
        }
        finally { _gate.Release(); }
    }

    private void CaptureDiagnostics(string? failure)
    {
        var entries = _vigil.Diagnostics.Concat(_native.Diagnostics).Take(512).ToList();
        if (failure is not null) entries.Add(JsonSerializer.SerializeToElement(new { protocol = "vigil-watt", state = "failed", reason = failure }));
        Volatile.Write(ref _diagnostics, entries.ToArray());
    }

    public async Task SuspendAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _vigil.SuspendAsync().ConfigureAwait(false); await _native.SuspendAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _vigil.DisposeAsync().ConfigureAwait(false); await _native.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
