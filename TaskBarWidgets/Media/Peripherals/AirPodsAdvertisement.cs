namespace TaskBarWidgets.Media.Peripherals;

// Independently implemented from the public proximity-advertisement layout:
// https://github.com/SpriteOvO/AirPodsDesktop/blob/main/Source/Core/AppleCP.h
// https://github.com/SpriteOvO/AirPodsDesktop/blob/main/Source/Core/AppleCP.cpp
// https://github.com/librepods-org/librepods/blob/main/linux/ble/blemanager.cpp
// https://docs.rs/cidermill-apple-ble/latest/cidermill_apple_ble/struct.Advertisement.html
// Max 2 PID: https://github.com/FIocker/aap-head-tracker#compatibility
// Decoding a model/battery is not proof that the advertiser is a paired device.
internal sealed record AirPodsAdvertisement(ushort Model, PeripheralBatteryReading[] Batteries)
{
    internal static bool IsSupportedModel(ushort model) => model is
        0x2002 or 0x200F or 0x2013 or // AirPods 1, 2, 3
        0x2019 or 0x201B or 0x2030 or 0x2036 or // AirPods 4 / ANC, 5 variants
        0x200E or 0x2014 or 0x2024 or 0x2027 or // Pro 1, Pro 2 / USB-C, Pro 3
        0x200A or 0x201F or 0x202D; // Max / USB-C, Max 2

    // Input excludes company ID 0x004C. Only the 27-byte paired-mode layout
    // is supported; pairing-mode packets have different offsets. A valid frame
    // with no known levels still succeeds so callers can count ambiguous nearby
    // advertisers; callers must require a percentage before publishing a reading.
    // False always leaves result null.
    internal static bool TryParse(ReadOnlySpan<byte> data, out AirPodsAdvertisement? result)
    {
        result = null;
        if (data.Length != 27 || data[0] != 0x07 || data[1] != 0x19 || data[2] != 0x01)
            return false;

        var model = (ushort)(data[3] | (data[4] << 8));
        if (!IsSupportedModel(model)) return false;

        int low = data[6] & 0x0F, high = data[6] >> 4;
        bool lowCharging = (data[7] & 0x10) != 0, highCharging = (data[7] & 0x20) != 0;
        PeripheralBatteryReading[] batteries;
        if (model is 0x200A or 0x201F or 0x202D)
        {
            // A single-unit headset can report in either earbud slot. Prefer
            // the low slot when present, retaining that slot's charging bit.
            batteries = [low <= 10 ? Reading("main", low, lowCharging) : Reading("main", high, highCharging)];
        }
        else
        {
            bool lowIsLeft = (data[5] & 0x20) != 0;
            batteries = [
                Reading("left", lowIsLeft ? low : high, lowIsLeft ? lowCharging : highCharging),
                Reading("right", lowIsLeft ? high : low, lowIsLeft ? highCharging : lowCharging),
                Reading("case", data[7] & 0x0F, (data[7] & 0x40) != 0)
            ];
        }

        result = new(model, batteries);
        return true;
    }

    private static PeripheralBatteryReading Reading(string role, int nibble, bool charging) => new()
    {
        Role = role,
        Percentage = nibble <= 10 ? nibble * 10 : null,
        Charging = nibble <= 10 ? charging : null,
        Estimated = nibble <= 10
    };
}
