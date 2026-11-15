using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskBarWidgets.Media.Peripherals;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class PeripheralDiagnosticsTests
{
    [Fact]
    public void ExportCorrelatesIdentifiersOnlyWithinOneReportAndDropsUnknownFields()
    {
        const string path = @"\\?\hid#vid_1532&pid_00e6#PRIVATE-SERIAL";
        string Entities(byte[] salt) => JsonSerializer.Serialize(new { endpointPath = "redacted-" +
            Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16].ToLowerInvariant() });
        var entries = new[] { JsonSerializer.SerializeToElement(new { path, serial = "SECRET", vid = 0x1532,
            feature = 91, protocol = "razer-90b", reason = "no_valid_response", unrelatedSecret = "never-export" }) };
        var report = PeripheralDiagnostics.Export(Entities, entries);
        using var json = JsonDocument.Parse(report);
        var protocol = json.RootElement.GetProperty("protocols")[0];
        Assert.Equal(json.RootElement.GetProperty("entities").GetProperty("endpointPath").GetString(), protocol.GetProperty("path").GetString());
        Assert.Equal("no_valid_response", protocol.GetProperty("reason").GetString());
        Assert.Equal(91, protocol.GetProperty("feature").GetInt32());
        Assert.DoesNotContain("PRIVATE-SERIAL", report);
        Assert.DoesNotContain("SECRET", report);
        Assert.DoesNotContain("never-export", report);
        using var second = JsonDocument.Parse(PeripheralDiagnostics.Export(Entities, entries));
        Assert.NotEqual(protocol.GetProperty("path").GetString(), second.RootElement.GetProperty("protocols")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void MalformedOrUnexpectedHelperFieldsCannotExposeDevicePaths()
    {
        var entry = JsonSerializer.SerializeToElement(new { path = @"C:\private\device", vid = "1532",
            reason = @"Read failed C:\private\device", futureField = "private" });
        var report = PeripheralDiagnostics.Export(_ => "{}", [entry]);
        Assert.DoesNotContain("private", report);
        using var json = JsonDocument.Parse(report);
        Assert.False(json.RootElement.GetProperty("protocols")[0].TryGetProperty("vid", out _));
    }

    [Fact]
    public void OptionalHeadsetToolDoesNotQueryClaimedVendorTransport()
    {
        Assert.True(HeadsetControlProvider.TransportClaimed("1532:0555", [@"\\?\HID#VID_1532&PID_0555&MI_03#unit#{guid}"]));
        Assert.False(HeadsetControlProvider.TransportClaimed("1532:0556", [@"\\?\HID#VID_1532&PID_0555&MI_03#unit#{guid}"]));
        Assert.False(HeadsetControlProvider.TransportClaimed("", []));
    }
}
