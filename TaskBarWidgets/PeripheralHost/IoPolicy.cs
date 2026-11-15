using VigilWatt.Core.Hid;
using VigilWatt.Core.Plugins.Asus;

namespace TaskBarWidgets.PeripheralHost;

internal static class IoPolicy
{
    private static string[] _excluded = [];
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> Opened = new(StringComparer.OrdinalIgnoreCase);

    public static void Begin(Request request)
    {
        _excluded = [.. request.ExcludedPaths, .. request.ExcludedIdentities];
        Allowed.Clear();
        Opened.Clear();
    }

    public static bool IsExcluded(string path) => _excluded.Any(value =>
        value.Equals(path, StringComparison.OrdinalIgnoreCase)
        || value.StartsWith(HostEngine.IdentityPrefix(path), StringComparison.OrdinalIgnoreCase));

    public static void Authorize(HidDeviceInfo endpoint, IReadOnlyList<HidDeviceInfo> present)
    {
        Allowed.Clear();
        if (!IsExcluded(endpoint.DevicePath)) Allowed.Add(endpoint.DevicePath);
        // Scope II requires one read-only paired-model query through the companion
        // control collection on THIS receiver. It is never a battery poll of peers.
        if (endpoint.VendorId != 0x0B05 || endpoint.ProductId != 0x1ACE) return;
        string? key = AsusKeyboardCodec.ReceiverKey(endpoint.DevicePath);
        if (key == null) return;
        var companions = present.Where(i => i.VendorId == 0x0B05 && i.ProductId == 0x1ACE
            && i.UsagePage is >= 0xFF00 and <= 0xFF02 && i.Usage == 1
            && i.InputReportLength == 64 && i.OutputReportLength == 64 && i.OutputReportId == 1
            && !IsExcluded(i.DevicePath)
            && string.Equals(AsusKeyboardCodec.ReceiverKey(i.DevicePath), key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (companions.Length == 1) Allowed.Add(companions[0].DevicePath);
    }

    public static void BeforeOpen(string path)
    {
        if (IsExcluded(path) || !Allowed.Contains(path)) throw new UnauthorizedAccessException("endpoint_not_authorized");
        Opened.Add(path);
    }

    public static void End() => Allowed.Clear();
}
