using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using VigilWatt.Core.Devices;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins;
using VigilWatt.Core.Plugins.Razer;

namespace TaskBarWidgets.PeripheralHost;

internal static class SelfTest
{
    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException(code);
    }

    public static async Task<int> RunAsync()
    {
        try
        {
            RazerReplay();
            IdentityReplay();
            ParseReplay();
            await RoutingReplayAsync().ConfigureAwait(false);
            await VerifiedLogitechRestartReplayAsync().ConfigureAwait(false);
            await CompanionReplayAsync().ConfigureAwait(false);
            await EofReplayAsync().ConfigureAwait(false);
            // An independent review may supply additional checks without changing
            // this test source or adding a test framework/runtime dependency.
            AuditRegressionTests.Run();
            Console.WriteLine("{\"selfTest\":\"passed\",\"hardwareAccess\":false}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("self_test_failed:" + error.GetType().Name + ":" + error.Message);
            return 1;
        }
    }

    private static byte[] RazerResponse(byte command, byte tx, byte value)
    {
        var payload = RazerCodec.BuildRequest(command, tx);
        var response = new byte[91];
        payload.CopyTo(response, 1);
        response[1] = 2;
        response[10] = value;
        response[89] = RazerCodec.Checksum(response.AsSpan(3, 86));
        return response;
    }

    private static void RazerReplay()
    {
        Require(RazerCodec.BuildRequest(0x80, 0x1F)[88] == 0x85, "razer_request_vector");
        var zero = RazerResponse(0x80, 0x1F, 0);
        Require(RazerCodec.TryParseBattery(zero, out int raw, 0x1F) && raw == 0, "razer_valid_zero");
        var full = RazerResponse(0x80, 0xFF, 255);
        Require(RazerCodec.TryParseBattery(full, out raw, 0xFF) && RazerCodec.ToPercent(0x00E3, raw) == 100, "razer_full");
        Require(!RazerCodec.TryParseBattery(full, out _, 0x1F), "razer_transaction_correlation");
        foreach (int offset in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 89, 90 })
        {
            var corrupt = (byte[])zero.Clone();
            corrupt[offset] ^= 0x40;
            Require(!RazerCodec.TryParseBattery(corrupt, out _), "razer_reject_header_" + offset);
        }
        foreach (byte status in new byte[] { 0, 1, 3, 4, 5 })
        {
            var busy = (byte[])zero.Clone(); busy[1] = status;
            Require(!RazerCodec.TryParseBattery(busy, out _), "razer_status");
        }
        Require(!RazerCodec.TryParseBattery(zero[..90], out _) && !RazerCodec.TryParseBattery([.. zero, 0], out _), "razer_exact_length");
        Require(!RazerCodec.TryParseCharging(RazerResponse(0x80, 0x1F, 1), out _), "razer_command_echo");
        Require(RazerCodec.TryParseCharging(RazerResponse(0x84, 0x1F, 7), out var charge) && charge == ChargingState.Unknown, "razer_unknown_charge");
        Require(RazerViperSeCodec.TryParseBattery(zero, out int percent) && percent == 0, "se_separate_codec");
        Require(!RazerViperSeCodec.TryParseBattery(full, out _), "se_no_v4_transaction_fallback");
        var plugin = new RazerPlugin();
        foreach (ushort pid in new ushort[] { 0x00E2, 0x00E3, 0x00E5, 0x00E6 })
        {
            Require(plugin.HostIsCandidate(RazerInfo(pid, 0x000C, 1, 91, "03")), "v4_correct_channel");
            Require(!plugin.HostIsCandidate(RazerInfo(pid, 0xFF13, 1, 91, "03")), "v4_reject_other_page");
            Require(!plugin.HostIsCandidate(RazerInfo(pid, 0x000C, 2, 91, "03")), "v4_reject_other_usage");
            Require(!plugin.HostIsCandidate(RazerInfo(pid, 0x000C, 1, 91, "00")), "v4_reject_other_interface");
            Require(!plugin.HostIsCandidate(RazerInfo(pid, 0x000C, 1, 62, "03")), "v4_reject_other_length");
        }
        Require(plugin.HostIsCandidate(RazerInfo(0x00DF, 1, 2, 91, "00")), "se_exact_channel");
        Require(!plugin.HostIsCandidate(RazerInfo(0x0555, 0xFF00, 1, 91, "03")), "blackshark_native_fallback");
        Require(ChargingStateMap.FromChargingFlag(false, 100) == ChargingState.Discharging, "full_unplugged_is_not_charging");
    }

    private static HidDeviceInfo RazerInfo(ushort pid, ushort page, ushort usage, ushort length, string mi) =>
        new() { DevicePath = $"\\\\?\\hid#vid_1532&pid_{pid:x4}&mi_{mi}#7&abcd&0&0000#{{guid}}",
            VendorId = 0x1532, ProductId = pid, UsagePage = page, Usage = usage, FeatureReportLength = length };

    private static void IdentityReplay()
    {
        const string a = "hid#vid_373b&pid_1015&mi_03#7&aaaa&0&0000#{guid}";
        const string b = "hid#vid_373b&pid_1015&mi_03#7&bbbb&0&0000#{guid}";
        Require(PluginUtils.StableDeviceHash(a) != PluginUtils.StableDeviceHash(b), "full_path_not_lossy_instance");
        Require(PluginUtils.StableDeviceHash(a) == PluginUtils.StableDeviceHash(a.ToUpperInvariant()), "path_case_alias");
        var plugin = new ModelGroupedFixture();
        plugin.AttachAsync([], new DeviceClaimRegistry()).GetAwaiter().GetResult();
        var type = typeof(BatteryPluginBase).GetNestedType("ProbeOutcome", BindingFlags.NonPublic)!;
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 3);
        var outcomes = Array.CreateInstance(type, 2);
        foreach (var pair in new[] { (0, a), (1, b) })
        {
            var info = new HidDeviceInfo { DevicePath = pair.Item2, Product = "Same model" };
            var handle = new HidDeviceHandle(info, new SafeFileHandle(new IntPtr(-1), ownsHandle: false));
            outcomes.SetValue(constructor.Invoke([info, handle, ("fixture", (int?)50, ChargingState.Unknown)]), pair.Item1);
        }
        var statuses = new List<DeviceStatus>();
        typeof(BatteryPluginBase).GetMethod("CommitProbes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(plugin, [outcomes, statuses]);
        Require(statuses.Count == 2 && statuses[0].Id != statuses[1].Id, "commit_bypasses_group_id_override");
        plugin.Detach();

        var unit = new byte[18]; unit[2] = 1; unit[3] = 0x12; unit[4] = 0x34;
        unit[5] = 0x56; unit[6] = 0x78; unit[9] = 0xAB; unit[14] = 0xCD;
        Require(HidppIdentity.Parse(unit) == "12345678|model=ab00000000cd", "hidpp_target_unit_model");
        Require(HidppIdentity.Scope == "logitech:hidpp-unit-model", "native_hidpp_scope_contract");
        Require(HidppIdentity.Parse(new byte[18]) == "" && HidppIdentity.Parse(unit[..14]) == "", "hidpp_no_placeholder_identity");
        var target1 = new DeviceStatus { Id = "slot", Name = "same", Source = DeviceSource.Hid24G, HostUnitId = "unit1", HostUnitScope = HidppIdentity.Scope };
        var target2 = new DeviceStatus { Id = "slot", Name = "same", Source = DeviceSource.Hid24G, HostUnitId = "unit2", HostUnitScope = HidppIdentity.Scope };
        Require(HostEngine.IdentityFor(a, target1) != HostEngine.IdentityFor(a, target2), "slot_repair_changes_target_identity");
        Require(Metadata.TrustworthySerial("00000000") == "" && Metadata.TrustworthySerial("Serial Number") == "", "reject_placeholder_serial");
    }

    private static void ParseReplay()
    {
        const string benign = "{\"version\":1,\"requestId\":9,\"operation\":\"read\",\"deviceIds\":[],\"vendorExtensionsEnabled\":false,\"attemptedInitializations\":[],\"excludedIdentities\":[]}";
        var parsed = Request.Parse(Encoding.UTF8.GetBytes(benign), out int id);
        Require(id == 9 && parsed.DeviceIds.Length == 0, "native_extra_fields_compatible");
        foreach (string invalid in new[]
        {
            "{\"version\":2,\"requestId\":9,\"operation\":\"discover\"}",
            "{\"version\":1,\"requestId\":9,\"requestId\":10,\"operation\":\"discover\"}",
            "{\"version\":1,\"requestId\":9,\"operation\":\"read\"}",
            "{\"version\":1,\"requestId\":9,\"operation\":\"write\"}",
            "{\"version\":1,\"requestId\":9,\"operation\":\"read\",\"deviceIds\":[null]}",
        })
        {
            bool failed = false;
            try { Request.Parse(Encoding.UTF8.GetBytes(invalid), out _); }
            catch (Exception) { failed = true; }
            Require(failed, "reject_invalid_request");
        }
        Require(JsonSerializer.SerializeToUtf8Bytes(Response.Failure(9, "invalid_request"), Wire.Json).Length < Wire.MaximumLineBytes, "bounded_error_response");
    }

    private static async Task RoutingReplayAsync()
    {
        FixturePlugin.Activity.Clear(); FixturePlugin.Attached = FixturePlugin.Detached = 0;
        var paths = new[] { "hid#receiver-a", "hid#receiver-b" };
        int scans = 0;
        var engine = new HostEngine(() => { scans++; return paths.Select(p => new HidDeviceInfo
            { DevicePath = p, VendorId = 0x046D, ProductId = 0xC548, Product = "Receiver", SerialNumber = "RECEIVER123" }).ToArray(); },
            () => [new FixturePlugin()], _ => "11111111-2222-3333-4444-555555555555");
        var discovered = await engine.ExecuteAsync(new Request(1, "discover", [], [], []));
        Require(discovered.Devices.Length == 4, "independent_endpoint_children");
        Require(discovered.Devices.All(d => d.IdentityEvidence.UniqueSerial == "" && !d.IdentityEvidence.ContainerIsDevice), "receiver_never_target_evidence");
        Require(discovered.Devices.All(d => d.NameQuality == 50 && d.KindQuality == 50 && d.Compatibility == "implemented"), "wire_quality_scale");
        Require(discovered.Devices.All(d => d.Batteries[0].Charging == null), "full_enum_does_not_imply_charging");
        string selected = discovered.Devices[0].Identity;
        FixturePlugin.Activity.Clear();
        var read = await engine.ExecuteAsync(new Request(2, "read", [selected], [], []));
        Require(read.Devices.Length == 1 && read.Devices[0].Identity == selected && FixturePlugin.Activity.Count == 1 && scans == 1, "read_only_demanded_endpoint_and_slot");
        Require(FixturePlugin.Attached == FixturePlugin.Detached, "all_plugins_detached_before_response");
        // A new helper knows no devices. Restart discovery must recover only the
        // selected endpoint and its selected child, even if peers are present.
        var restarted = new HostEngine(() => paths.Select(p => new HidDeviceInfo
            { DevicePath = p, VendorId = 0x046D, ProductId = 0xC548 }).ToArray(),
            () => [new FixturePlugin()], _ => "");
        FixturePlugin.Activity.Clear();
        var restart = await restarted.ExecuteAsync(new Request(20, "discover", [selected], [], [])
            { QueryIds = discovered.Devices.Select(d => d.QueryId).ToArray() });
        Require(restart.Devices.Length == 1 && restart.Devices[0].Identity == selected
            && FixturePlugin.Activity.Count == 1 && FixturePlugin.Activity[0] == paths[0] + ":1", "cold_restart_selected_path_and_slot_only");
        FixturePlugin.Activity.Clear();
        var missingQueries = await restarted.ExecuteAsync(new Request(21, "discover", [selected], [], []));
        Require(missingQueries.Error == "missing_query_ids" && FixturePlugin.Activity.Count == 0, "cold_restart_missing_queries_fails_closed");
        FixturePlugin.ChargingOnly = true;
        FixturePlugin.SampleTime = FixturePlugin.SampleTime.AddMinutes(1);
        var partial = await engine.ExecuteAsync(new Request(22, "read", [selected], [], []));
        Require(partial.Devices[0].Batteries[0] is { Percentage: null, Charging: true }
            && partial.Devices[0].UpdatedAt == FixturePlugin.SampleTime && partial.Devices[0].UpdatedAt > read.Devices[0].UpdatedAt
            && partial.Devices[0].Stale,
            "partial_charge_preserves_query_time_without_percentage");
        FixturePlugin.ChargingOnly = false;
        FixturePlugin.Activity.Clear();
        var idle = await engine.ExecuteAsync(new Request(3, "read", [], [], []));
        Require(idle.Devices.Length == 0 && FixturePlugin.Activity.Count == 0 && scans == 1, "empty_demand_no_hardware");
        var quarantine = await engine.ExecuteAsync(new Request(4, "discover", [], [paths[0]], []));
        Require(quarantine.Devices.Length == 2 && quarantine.Diagnostics.Any(d => d.Reason == "quarantined"), "excluded_path_quarantine");
        var quarantineIdentity = await engine.ExecuteAsync(new Request(5, "read", [selected], [], [selected]));
        Require(quarantineIdentity.Devices.Length == 0, "excluded_raw_identity_quarantine");
        Require(VigilWatt.Core.Settings.AppSettings.Load().PluginEnabled.Count == 0 && DeviceBlocklist.AllowedIds == null, "memory_authorization_revoked");
        Require(Metadata.IsDirectTarget(RazerInfo(0x00E2, 12, 1, 91, "03"), "Razer")
            && !Metadata.IsDirectTarget(RazerInfo(0x00E3, 12, 1, 91, "03"), "Razer"), "body_receiver_identity_scope");
        IoPolicy.Begin(new Request(90, "read", [], [paths[0]], []));
        bool blocked = false;
        try { IoPolicy.BeforeOpen(paths[0]); }
        catch (UnauthorizedAccessException) { blocked = true; }
        Require(blocked, "quarantine_precedes_any_hid_open");
        IoPolicy.End();
    }

    private static async Task VerifiedLogitechRestartReplayAsync()
    {
        var paths = new[] { "hid#verified-logitech-a", "hid#verified-logitech-b" };
        HostEngine Engine() => new(() => paths.Select(path => new HidDeviceInfo
            { DevicePath = path, VendorId = 0x046D, ProductId = 0xC548 }).ToArray(),
            () => [new FixturePlugin()], _ => "");
        FixturePlugin.VerifiedUnits = true;
        try
        {
            var all = await Engine().ExecuteAsync(new Request(50, "discover", [], [], []));
            Require(all.Devices.Length == 4 && all.Devices.All(d =>
                d.IdentityEvidence.UnitScope == HidppIdentity.Scope && d.IdentityEvidence.UnitId.Contains("|model=")),
                "restart_fixture_uses_verified_native_hidpp_unit_scope");
            var selected = all.Devices[0];
            Require(selected.Identity.StartsWith(HostEngine.IdentityPrefix(paths[0]) + "slot-1:unit-", StringComparison.Ordinal)
                && selected.QueryId == "logitech:" + PluginUtils.StableDeviceHash(paths[0]) + ":1",
                "verified_logitech_public_identity_retains_internal_slot");
            FixturePlugin.Activity.Clear();
            var restarted = Engine();
            var response = await restarted.ExecuteAsync(new Request(51, "discover", [selected.Identity], [], [])
                { QueryIds = [selected.QueryId] });
            Require(response.Devices.Length == 1 && response.Devices[0].Identity == selected.Identity
                && FixturePlugin.Activity.SequenceEqual([paths[0] + ":1"]), "verified_unit_restart_probes_only_selected_slot");
            FixturePlugin.Activity.Clear();
            var read = await restarted.ExecuteAsync(new Request(52, "read", [selected.Identity], [], []));
            Require(read.Devices.Length == 1 && read.Devices[0].Identity == selected.Identity
                && FixturePlugin.Activity.SequenceEqual([paths[0] + ":1"]), "verified_unit_restart_retains_read_route");
            FixturePlugin.Activity.Clear();
            var wrongPath = await Engine().ExecuteAsync(new Request(53, "discover", [selected.Identity], [], [])
                { QueryIds = [all.Devices[2].QueryId] });
            Require(wrongPath.Devices.Length == 0 && FixturePlugin.Activity.Count == 0,
                "verified_unit_restart_rejects_other_receiver_query_key");
        }
        finally { FixturePlugin.VerifiedUnits = false; }
    }

    private static async Task EofReplayAsync()
    {
        using var lifetime = new CancellationTokenSource();
        var queue = Channel.CreateBounded<byte[]>(1);
        await Program.PumpAsync(new MemoryStream(), queue.Writer, lifetime).ConfigureAwait(false);
        Require(lifetime.IsCancellationRequested, "stdin_eof_cancels_inflight");
        var stalled = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        try { await stalled.Task.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "eof_does_not_wait_for_driver");
        using var oversizedLifetime = new CancellationTokenSource();
        var oversizedQueue = Channel.CreateBounded<byte[]>(1);
        await Program.PumpAsync(new MemoryStream(new byte[Wire.MaximumLineBytes]), oversizedQueue.Writer, oversizedLifetime);
        Require(oversizedLifetime.IsCancellationRequested, "oversized_input_ends_ownership");
    }

    private static async Task CompanionReplayAsync()
    {
        const string primary = "hid#vid_0b05&pid_1ace&mi_02&col02#8&abc&0&0001#{guid}";
        const string control = "hid#vid_0b05&pid_1ace&mi_02&col01#8&abc&0&0000#{guid}";
        HidDeviceInfo[] all = [
            new() { DevicePath = primary, VendorId = 0x0B05, ProductId = 0x1ACE, UsagePage = 0xFF00,
                Usage = 1, InputReportLength = 64, OutputReportLength = 64, OutputReportId = 2 },
            new() { DevicePath = control, VendorId = 0x0B05, ProductId = 0x1ACE, UsagePage = 0xFF00,
                Usage = 1, InputReportLength = 64, OutputReportLength = 64, OutputReportId = 1 },
        ];
        string selected = HostEngine.IdentityPrefix(primary) + "slot-1";
        bool companionAuthorized = false;
        FixturePlugin.BeforeAttach = () => { IoPolicy.BeforeOpen(control); companionAuthorized = true; };
        try
        {
            var engine = new HostEngine(() => all, () => [new FixturePlugin()], _ => "");
            var response = await engine.ExecuteAsync(new Request(40, "discover", [selected], [], [])
                { QueryIds = ["fixture:" + PluginUtils.StableDeviceHash(primary) + ":1"] });
            Require(companionAuthorized && response.Devices.Length == 1 && response.Devices[0].Identity == selected,
                "restart_retains_same_receiver_companion_metadata");
            companionAuthorized = false;
            var quarantined = await engine.ExecuteAsync(new Request(41, "discover", [selected], [control], [])
                { QueryIds = ["fixture:" + PluginUtils.StableDeviceHash(primary) + ":1"] });
            Require(!companionAuthorized && quarantined.Devices.All(d => d.State != "online"),
                "companion_cannot_bypass_quarantine");
        }
        finally { FixturePlugin.BeforeAttach = null; }
    }

    private sealed class ModelGroupedFixture : BatteryPluginBase
    {
        public override string Name => "Grouped";
        public override string DisplayName => "Fixture";
        public override string DisplayNameEn => "Fixture";
        public override DeviceKind Kind => DeviceKind.Mouse;
        protected override string IdFor(string path) => "grouped:same-model";
        protected override string BuildDeviceId(HidDeviceHandle handle) => "grouped:same-model";
        protected override bool IsCandidate(HidDeviceInfo info) => true;
        protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice device) => null;
        protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle) => throw new InvalidOperationException("unexpected_hardware_access");
    }

    private sealed class FixturePlugin : BatteryPluginBase
    {
        public static readonly List<string> Activity = [];
        public static int Attached, Detached;
        public static bool ChargingOnly;
        public static bool VerifiedUnits;
        public static DateTimeOffset SampleTime = DateTimeOffset.UnixEpoch.AddHours(1);
        public static Action? BeforeAttach;
        public override string Name => VerifiedUnits ? "Logitech" : "Fixture";
        public override string DisplayName => "Fixture";
        public override string DisplayNameEn => "Fixture";
        public override DeviceKind Kind => DeviceKind.Keyboard;
        protected override bool IsCandidate(HidDeviceInfo info) => true;
        protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice device) => null;
        protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle) => throw new InvalidOperationException("unexpected_hardware_access");
        public override Task<IReadOnlyList<DeviceStatus>> AttachAsync(IReadOnlyList<HidDeviceInfo> devices, DeviceClaimRegistry claims)
        {
            Attached++;
            BeforeAttach?.Invoke();
            var statuses = new List<DeviceStatus>();
            foreach (var info in devices)
                foreach (int slot in new[] { 1, 2 })
                {
                    string id = Name.ToLowerInvariant() + ":" + PluginUtils.StableDeviceHash(info.DevicePath) + ":" + slot;
                    if (DeviceBlocklist.IsBlocked(id)) continue;
                    Activity.Add(info.DevicePath + ":" + slot);
                    statuses.Add(new DeviceStatus { Id = id, Name = "Confirmed target", Source = DeviceSource.Hid24G,
                        Kind = DeviceKind.Keyboard, BatteryPercent = ChargingOnly ? null : 100,
                        Charging = ChargingOnly ? ChargingState.Charging : ChargingState.Full,
                        Protocol = "fixture", LastUpdated = SampleTime,
                        HostUnitId = VerifiedUnits ? PluginUtils.StableDeviceHash(info.DevicePath)[..6].ToLowerInvariant()
                            + slot.ToString("x2") + "|model=ab00000000cd" : slot.ToString(),
                        HostUnitScope = VerifiedUnits ? HidppIdentity.Scope : "endpoint", HostIdentityVerified = true,
                        HostSlot = slot.ToString(), HostNameQuality = 50 });
                }
            return Task.FromResult<IReadOnlyList<DeviceStatus>>(statuses);
        }
        public override void Detach() { Detached++; }
    }
}
