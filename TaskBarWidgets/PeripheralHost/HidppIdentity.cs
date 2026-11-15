namespace TaskBarWidgets.PeripheralHost;

internal static class HidppIdentity
{
    public const string Scope = "logitech:hidpp-unit-model";
    // 0x0003 GetDeviceInfo, input includes the two-byte HID++ feature header.
    // Unit + model distinguishes the target. A receiver slot is only an address.
    public static string Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 15 || response[2] is 0 or > 16) return "";
        string unit = Convert.ToHexString(response.Slice(3, 4)).ToLowerInvariant();
        string model = Convert.ToHexString(response.Slice(9, 6)).ToLowerInvariant();
        return unit is "00000000" or "ffffffff" || model is "000000000000" or "ffffffffffff"
            ? "" : unit + "|model=" + model;
    }
}
