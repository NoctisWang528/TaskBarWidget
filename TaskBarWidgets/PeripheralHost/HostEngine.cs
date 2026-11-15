using VigilWatt.Core.Devices;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins;
using VigilWatt.Core.Settings;

namespace TaskBarWidgets.PeripheralHost;

internal sealed class HostEngine(
    Func<IReadOnlyList<HidDeviceInfo>> scan,
    Func<BatteryPluginBase[]> createPlugins,
    Func<string, string> container)
{
    private sealed class Endpoint(HidDeviceInfo info, int pluginIndex)
    {
        public HidDeviceInfo Info = info;
        public int PluginIndex = pluginIndex;
        public bool Verified;
        public Dictionary<string, KnownDevice> Devices = new(StringComparer.Ordinal);
    }
    private sealed record KnownDevice(string InternalId, Device Snapshot, string Slot = "");
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<HidDeviceInfo> _present = [];

    internal static string IdentityPrefix(string path) => "vigil:" + PluginUtils.StableDeviceHash(path).ToLowerInvariant() + ":";
    internal static string IdentityFor(string path, DeviceStatus? status) => IdentityPrefix(path)
        + (string.IsNullOrEmpty(status?.HostUnitId) ? "0"
            : status.HostUnitScope == "endpoint" ? "slot-" + status.HostUnitId
            : (string.IsNullOrEmpty(status.HostSlot) ? "" : "slot-" + status.HostSlot + ":")
                + "unit-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(status.HostUnitScope + ":" + status.HostUnitId))).ToLowerInvariant());

    public async Task<Response> ExecuteAsync(Request request, Action<object>? progress = null)
    {
        IoPolicy.Begin(request);
        var diagnostics = new List<Diagnostic>();
        var result = new List<Device>();
        bool Excluded(string path) => request.ExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            || request.ExcludedIdentities.Any(id => id.Equals(path, StringComparison.OrdinalIgnoreCase)
                || id.StartsWith(IdentityPrefix(path), StringComparison.OrdinalIgnoreCase));
        var selected = request.DeviceIds.ToHashSet(StringComparer.Ordinal);
        bool boundedDiscovery = request.Operation == "discover" && selected.Count > 0;
        bool SelectedPath(string path) => selected.Any(id => id.StartsWith(IdentityPrefix(path), StringComparison.Ordinal));
        if (boundedDiscovery && request.QueryIds.Length == 0)
            return Response.Failure(request.RequestId, "missing_query_ids");
        if (request.Operation == "discover")
        {
            var allPresent = scan().Where(i => i.DevicePath.Length <= 4096)
                .DistinctBy(i => i.DevicePath, StringComparer.OrdinalIgnoreCase).Take(512).ToArray();
            _present = allPresent;
            var present = allPresent.Where(i => !boundedDiscovery || SelectedPath(i.DevicePath)).ToArray();
            var paths = present.Select(i => i.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _endpoints.Keys.Where(p => (!boundedDiscovery || SelectedPath(p)) && !paths.Contains(p)).ToArray()) _endpoints.Remove(path);
            var matchers = createPlugins();
            foreach (var info in present)
            {
                if (Excluded(info.DevicePath))
                {
                    diagnostics.Add(Diagnose(info, "", "unavailable", "quarantined"));
                    continue;
                }
                int index = Array.FindIndex(matchers, p => p.HostIsCandidate(info));
                if (index < 0)
                {
                    diagnostics.Add(Diagnose(info, "", "unsupported", "protocol_not_supported"));
                    continue;
                }
                if (!_endpoints.TryGetValue(info.DevicePath, out var endpoint))
                    _endpoints[info.DevicePath] = endpoint = new Endpoint(info, index);
                endpoint.Info = info;
                endpoint.PluginIndex = index;
            }
        }

        foreach (var endpoint in _endpoints.Values.ToArray())
        {
            var info = endpoint.Info;
            if (Excluded(info.DevicePath)) continue;
            if (boundedDiscovery && !SelectedPath(info.DevicePath)) continue;
            var demanded = endpoint.Devices.Values.Where(d => selected.Contains(d.Snapshot.Identity)).ToArray();
            if (request.Operation == "read" && demanded.Length == 0) continue;
            if (request.Operation == "read" && demanded.All(d => !d.Snapshot.CanRead))
            {
                result.AddRange(demanded.Select(d => d.Snapshot with { Stale = true, State = "unavailable" }));
                continue;
            }
            progress?.Invoke(new { version = 1, requestId = request.RequestId, @event = "progress",
                identity = demanded.FirstOrDefault()?.Snapshot.Identity ?? IdentityFor(info.DevicePath, null) });

            var plugin = createPlugins()[endpoint.PluginIndex];
            var claims = new DeviceClaimRegistry();
            var settings = AppSettings.Load().PluginEnabled;
            var snapshots = new Dictionary<string, DeviceStatus>(StringComparer.Ordinal);
            string reason = "no_valid_response";
            try
            {
                IoPolicy.Authorize(info, _present);
                settings.Clear();
                settings[plugin.Name] = true;
                // Logitech's inherited base gate is only used by Centurion. HID++ has a
                // distinct gate for every endpoint+slot, including the direct FF slot.
                DeviceBlocklist.AllowedIds = boundedDiscovery
                    ? RestartQueryIds(request, plugin.Name, info.DevicePath)
                    : request.Operation == "discover" ? null
                    : demanded.Select(d => d.InternalId).ToHashSet(StringComparer.Ordinal);
                if (boundedDiscovery && DeviceBlocklist.AllowedIds!.Count == 0)
                {
                    diagnostics.Add(Diagnose(info, plugin.Name.ToLowerInvariant(), "unavailable", "query_not_authorized"));
                    continue;
                }
                IBatteryPlugin protocol = plugin;
                foreach (var status in await protocol.AttachAsync([info], claims).ConfigureAwait(false))
                    snapshots[status.Id] = status;
                foreach (var status in protocol.Poll()) snapshots[status.Id] = status;
                if (snapshots.Count > 0) endpoint.Verified = snapshots.Values.Any(s => s.HostIdentityVerified);
                reason = snapshots.Values.Any(s => s.BatteryPercent.HasValue) ? "battery_read"
                    : snapshots.Count > 0 ? "status_without_battery" : "no_valid_response";
            }
            catch (Exception)
            {
                // Protocol errors are machine codes; raw device strings/exceptions never
                // enter stdout, stderr, a log file or the parent's diagnostic message.
                reason = "probe_failed";
            }
            finally
            {
                // Gate writes before releasing all handles. This host does not start any
                // passive pump, watcher, timer or child process.
                settings.Clear();
                try { plugin.Detach(); }
                catch { Environment.Exit(3); } // no success response while ownership is uncertain
                IoPolicy.End();
                DeviceBlocklist.AllowedIds = null;
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var status in snapshots.Values)
            {
                string identity = IdentityFor(info.DevicePath, status);
                if ((request.Operation == "read" || boundedDiscovery) && !selected.Contains(identity)) continue;
                var snapshot = ToDevice(info, plugin, status, identity);
                // Charging-only reports carry their query time, with no percentage.
                // The parent tracks charging freshness separately and retains the
                // previous percentage and its original sample time.
                if (request.Operation == "discover" && !string.IsNullOrEmpty(status.HostSlot))
                    foreach (var key in endpoint.Devices.Where(d => d.Key != identity && d.Value.Slot == status.HostSlot)
                                 .Select(d => d.Key).ToArray()) endpoint.Devices.Remove(key);
                endpoint.Devices[identity] = new KnownDevice(status.Id, snapshot, status.HostSlot ?? "");
                result.Add(snapshot);
                reported.Add(identity);
            }
            if (snapshots.Count > 0 && endpoint.Devices.Count > 1)
                endpoint.Devices.Remove(IdentityFor(info.DevicePath, null)); // remove an unproven receiver placeholder
            if (endpoint.Devices.Count == 0)
            {
                string identity = IdentityFor(info.DevicePath, null);
                var snapshot = ToDevice(info, plugin, null, identity);
                endpoint.Devices[identity] = new KnownDevice(
                    $"{plugin.Name.ToLowerInvariant()}:{PluginUtils.StableDeviceHash(info.DevicePath)}", snapshot);
            }
            foreach (var known in endpoint.Devices.Values.Where(k => !reported.Contains(k.Snapshot.Identity)
                         && (request.Operation == "discover" && !boundedDiscovery || selected.Contains(k.Snapshot.Identity))))
                result.Add(known.Snapshot with { Stale = true, State = "unavailable" });
            diagnostics.Add(Diagnose(info, plugin.Name.ToLowerInvariant(),
                snapshots.Count > 0 ? "identified" : "unavailable", reason));
        }
        // Claims mean a protocol actually validated this endpoint, never merely a VID/name match.
        var claimed = _endpoints.Values.Where(e => e.Verified && !Excluded(e.Info.DevicePath)
                && (!boundedDiscovery || SelectedPath(e.Info.DevicePath)))
            .Select(e => e.Info.DevicePath).ToArray();
        return new Response(request.RequestId, result.Take(256).ToArray(), claimed, diagnostics.Take(512).ToArray());
    }

    private Device ToDevice(HidDeviceInfo info, BatteryPluginBase plugin, DeviceStatus? status, string identity)
    {
        bool valid = status?.BatteryPercent is >= 0 and <= 100 && status.HostIdentityVerified;
        bool child = status?.HostUnitScope == "endpoint";
        bool knownDirect = Metadata.IsDirectTarget(info, plugin.Name);
        string serial = knownDirect && status != null && status.Source != DeviceSource.BluetoothClassic
            ? Metadata.ReadTargetSerial(info) : "";
        var evidence = new IdentityEvidence(container(info.DevicePath), "", serial,
            serial.Length > 0 ? $"usb:{info.VendorId:x4}:{info.ProductId:x4}" : "",
            status?.HostUnitId ?? "", status?.HostUnitScope ?? "",
            info.DevicePath, knownDirect && !child && status != null);
        string name = Metadata.CleanText(status?.Name, 256);
        int nameQuality = status?.HostNameQuality ?? 0;
        if (name.Length == 0) { name = Metadata.CleanText(info.Product, 256); nameQuality = 20; }
        if (name.Length == 0) { name = plugin.DisplayNameEn; nameQuality = 0; }
        var kind = status?.Kind ?? plugin.Kind;
        bool? charging = status?.Charging switch
        {
            ChargingState.Charging => true,
            ChargingState.Discharging => false,
            _ => null,
        };
        // Voltage/ADC curves and coarse protocol levels are estimates, not exact percentages.
        bool estimated = status?.HostEstimated ?? false;
        bool hasReading = valid || charging.HasValue && status?.IsOnline == true && status.HostIdentityVerified;
        string protocol = status?.Protocol is { Length: > 0 } value ? value : plugin.Name.ToLowerInvariant();
        bool receiverOnly = status == null && plugin is VigilWatt.Core.Plugins.Logitech.LogitechPlugin;
        return new Device(identity, name, kind.ToString().ToLowerInvariant(),
            status?.Source == DeviceSource.BluetoothClassic ? "bluetooth" : knownDirect ? "usb" : "usb-2.4g",
            hasReading && status!.IsOnline ? "online" : "unavailable",
            hasReading ? [new Battery(valid ? status!.BatteryPercent : null, charging, estimated)] : [],
            hasReading ? status?.LastUpdated.ToUniversalTime() : null, !valid || status?.IsOnline != true, !receiverOnly && status?.HostIdentityVerified != false,
            "vigil-watt:" + protocol, hasReading ? "implemented" : "identityOnly", evidence,
            nameQuality, kind == DeviceKind.Unknown ? 0 : status?.HostKindQuality ?? 0)
            { QueryId = status?.Id ?? $"{plugin.Name.ToLowerInvariant()}:{PluginUtils.StableDeviceHash(info.DevicePath)}" };
    }

    private static HashSet<string> RestartQueryIds(Request request, string pluginName, string path)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        string prefix = IdentityPrefix(path);
        string internalPrefix = pluginName.ToLowerInvariant() + ":" + PluginUtils.StableDeviceHash(path);
        foreach (string identity in request.DeviceIds.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)))
        {
            string suffix = identity[prefix.Length..];
            string query = suffix.StartsWith("slot-", StringComparison.Ordinal)
                ? internalPrefix + ":" + suffix[5..].Split(':')[0] : internalPrefix;
            if (request.QueryIds.Contains(query, StringComparer.Ordinal)) allowed.Add(query);
        }
        return allowed;
    }

    private static Diagnostic Diagnose(HidDeviceInfo info, string protocol, string state, string reason) =>
        new(info.DevicePath, info.VendorId, info.ProductId, info.UsagePage, info.Usage,
            info.FeatureReportLength, info.InputReportLength, info.OutputReportLength, protocol, state, reason);
}
