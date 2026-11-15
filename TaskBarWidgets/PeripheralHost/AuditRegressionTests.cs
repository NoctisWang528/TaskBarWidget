using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Asus;
using VigilWatt.Core.Plugins.Atk;
using VigilWatt.Core.Plugins.Delux;
using VigilWatt.Core.Plugins.Logitech;

namespace TaskBarWidgets.PeripheralHost;

// Pure synthetic checks, invoked only by the host's explicit self-test entry point.
internal static class AuditRegressionTests
{
    internal static void Run()
    {
        DeluxDockReadings();
        AsusOptionalCharging();
        AtkIndependentEndpoints();
        HidppFrames();
    }

    private static void Check(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException("audit_" + code);
    }

    private static void DeluxDockReadings()
    {
        var now = DateTimeOffset.UnixEpoch;
        string prefix = "audit:delux:" + Guid.NewGuid().ToString("N");
        byte[] Frame(byte flag, byte percent) => [3, 0x30, 0x40, flag, percent];
        var reliable = DeluxPlugin.FilterStatus(prefix + ":a", Frame(1, 41), now);
        Check(reliable is { Percent: 41, Charging: ChargingState.Discharging }, "delux_baseline");
        _ = new DeluxPlugin(); // Request-scoped recreation must not reset endpoint filter state.
        var dock = DeluxPlugin.FilterStatus(prefix.ToUpperInvariant() + ":A", Frame(3, 99), now.AddSeconds(1));
        Check(dock is { Percent: null, Charging: ChargingState.Charging }, "delux_dock_decoration");
        var other = DeluxPlugin.FilterStatus(prefix + ":b", Frame(3, 97), now.AddSeconds(1));
        Check(other.HasValue && other.Value.Percent == null
            && other.Value.Charging == ChargingState.Charging, "delux_unknown_and_isolated");
        var settling = DeluxPlugin.FilterStatus(prefix + ":a", Frame(1, 100), now.AddSeconds(2));
        Check(settling is { Percent: null, Charging: ChargingState.Discharging }, "delux_undock_settle");
        var settled = DeluxPlugin.FilterStatus(prefix + ":a", Frame(1, 53), now.AddSeconds(10));
        Check(settled is { Percent: 53 }, "delux_settle_elapsed");
        var full = DeluxPlugin.FilterStatus(prefix + ":b", Frame(2, 100), now.AddSeconds(11));
        Check(full is { Percent: 100, Charging: ChargingState.Full }, "delux_explicit_full");
        Check(DeluxPlugin.FilterStatus(prefix + ":a", Frame(0x7F, 99), now) == null,
            "delux_unknown_flag");
    }

    private static void AsusOptionalCharging()
    {
        int calls = 0;
        var missing = AsusPlugin.ReadHeadsetStatus(() => null, _ => { calls++; return ChargingState.Charging; });
        Check(missing == null && calls == 0, "asus_no_power_no_charging_query");
        var invalid = AsusPlugin.ReadHeadsetStatus(() => 101, _ => { calls++; return ChargingState.Charging; });
        Check(invalid == null && calls == 0, "asus_invalid_power");
        var reading = AsusPlugin.ReadHeadsetStatus(() => 73, percent =>
        {
            Check(percent == 73, "asus_charging_power_argument");
            calls++;
            return ChargingState.Charging;
        });
        Check(reading is { Percent: 73, Charging: ChargingState.Charging } && calls == 1,
            "asus_probe_queries_charging_once");
        var unavailable = AsusPlugin.ReadHeadsetStatus(() => 73, _ => null);
        Check(unavailable is { Percent: 73, Charging: ChargingState.Unknown }, "asus_optional_invalid");
        Check(AsusPlugin.ReadHeadsetStatus(() => 0, _ => ChargingState.Discharging)
            is { Percent: 0, Charging: ChargingState.Discharging }, "asus_zero_percent");
    }

    private static HidDeviceInfo AtkInfo(string path, ushort pid, string product,
        ushort input = 17, ushort output = 17, ushort page = 0xFF02) => new()
    {
        DevicePath = path, VendorId = 0x373B, ProductId = pid, Product = product,
        UsagePage = page, Usage = 1, InputReportLength = input, OutputReportLength = output,
    };

    private static void AtkIndependentEndpoints()
    {
        var plugin = new AtkPlugin();
        var body = AtkInfo("audit:atk:body", 0x1293, "Body A");
        var receiver = AtkInfo("audit:atk:receiver", 0x1278, "Independent receiver B");
        Check(plugin.HostIsCandidate(body) && plugin.HostIsCandidate(receiver), "atk_body_receiver_independent");
        Check(plugin.HostIsCandidate(AtkInfo("audit:atk:dynamic", 0x7777, "Unknown model")),
            "atk_dynamic_candidate_without_scan");
        Check(!plugin.HostIsCandidate(AtkInfo("audit:atk:wrong-page", 0x7777, "", page: 0xFF03)),
            "atk_usage_gate");
        Check(!plugin.HostIsCandidate(AtkInfo("audit:atk:short", 0x7777, "", input: 16)),
            "atk_minimum_caps");
        Check(!plugin.HostIsCandidate(AtkInfo("audit:atk:receiver-interference", 0x1278, "", input: 20, output: 20))
            && !plugin.HostIsCandidate(AtkInfo("audit:atk:body-interference", 0x10C7, "", input: 20, output: 20)),
            "atk_strict_known_caps");
        Check(AtkPlugin.NameFor(body) == "Body A" && AtkPlugin.NameFor(receiver) == "Independent receiver B",
            "atk_no_body_name_transfer");
        Check(AtkPlugin.NameFor(AtkInfo("audit:atk:body2", 0x1293, "Body C")) == "Body C"
            && AtkPlugin.NameFor(body) == "Body A", "atk_same_model_names_separate");
    }

    private static void HidppFrames()
    {
        byte[] request = [5, 0x1B];
        byte[] shortReply = [0x10, 1, 5, 0x1B, 73, 0, 1];
        Check(HidppIO.TryMatchResponse(shortReply, 1, request, out bool error) && !error, "hidpp_short");
        var longReply = new byte[20];
        shortReply.CopyTo(longReply, 0);
        longReply[0] = 0x11;
        Check(HidppIO.TryMatchResponse(longReply, 1, request, out error) && !error, "hidpp_long");
        for (int length = 0; length < 20; length++)
            Check(!HidppIO.TryMatchResponse(longReply.AsSpan(0, length), 1, request, out _),
                "hidpp_truncated_long_" + length);
        for (int length = 0; length < 7; length++)
            Check(!HidppIO.TryMatchResponse(shortReply.AsSpan(0, length), 1, request, out _),
                "hidpp_truncated_short_" + length);
        byte[] oversizedLong = [.. longReply, 0];
        byte[] oversizedShort = [.. shortReply, 0];
        Check(!HidppIO.IsValidFrame(oversizedLong) && !HidppIO.IsValidFrame(oversizedShort)
            && !HidppIO.IsValidFrame([0x12, 1, 5, 0x1B, 73, 0, 1]),
            "hidpp_unsupported_framing");
        Check(!HidppIO.TryMatchResponse(shortReply, 2, request, out _)
            && !HidppIO.TryMatchResponse(shortReply, 0xFE, request, out _), "hidpp_exact_device");
        Check(!HidppIO.TryMatchResponse(shortReply, 1, [6, 0x1B], out _)
            && !HidppIO.TryMatchResponse(shortReply, 1, [5, 0x1C], out _), "hidpp_feature_and_software_id");
        byte[] notification = [0x10, 1, 5, 0x10, 73, 0, 1];
        Check(!HidppIO.TryMatchResponse(notification, 1, request, out _), "hidpp_notification_not_reply");
        byte[] registerReply = [0x10, 1, 0x81, 0, 73, 0, 0];
        Check(HidppIO.TryMatchResponse(registerReply, 1, [0x81, 0], out error) && !error,
            "hidpp_register_reply_before_notification");
        foreach (byte marker in new byte[] { 0x8F, 0xFF })
        {
            byte[] failure = [0x10, 1, marker, 5, 0x1B, 1, 0];
            Check(HidppIO.TryMatchResponse(failure, 1, request, out error) && error,
                "hidpp_correlated_error_" + marker);
            Check(!HidppIO.TryMatchResponse(failure, 2, request, out _)
                && !HidppIO.TryMatchResponse(failure, 1, [6, 0x1B], out _)
                && !HidppIO.TryMatchResponse(failure, 1, [5, 0x1C], out _), "hidpp_unrelated_error_" + marker);
            Check(!HidppIO.TryMatchResponse(failure.AsSpan(0, 6), 1, request, out _),
                "hidpp_truncated_error_" + marker);
            byte[] oversizedError = [.. failure, 0];
            Check(!HidppIO.TryMatchResponse(oversizedError, 1, request, out _),
                "hidpp_oversized_error_" + marker);
            failure[5] = 0;
            Check(!HidppIO.TryMatchResponse(failure, 1, request, out _), "hidpp_invalid_error_" + marker);
        }
    }
}
