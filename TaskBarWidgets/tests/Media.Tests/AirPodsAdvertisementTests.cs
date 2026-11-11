using TaskBarWidgets.Media.Peripherals;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class AirPodsAdvertisementTests
{
    // Synthetic paired-mode frames; no captured identities or encrypted data.
    private static byte[] Packet(ushort model = 0x2024, byte status = 0x20,
        byte pods = 0x82, byte chargeAndCase = 0x05)
    {
        var data = new byte[27];
        data[0] = 0x07;
        data[1] = 0x19;
        data[2] = 0x01;
        data[3] = (byte)model;
        data[4] = (byte)(model >> 8);
        data[5] = status;
        data[6] = pods;
        data[7] = chargeAndCase;
        return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(19)]
    [InlineData(26)]
    [InlineData(28)]
    [InlineData(29)]
    public void RequiresExactLength(int length)
    {
        var data = new byte[length];
        Packet().AsSpan(0, Math.Min(length, 27)).CopyTo(data);
        Assert.False(AirPodsAdvertisement.TryParse(data, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, 0x10)]
    [InlineData(1, 0x11)]
    [InlineData(2, 0x00)]
    [InlineData(2, 0x06)]
    [InlineData(2, 0xFF)]
    public void RejectsOtherTypesLengthsAndPairingLayouts(int offset, byte value)
    {
        var data = Packet();
        data[offset] = value;
        Assert.False(AirPodsAdvertisement.TryParse(data, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0xFFFF)]
    [InlineData(0x2420)] // Wire-order bytes are not the numeric PID.
    [InlineData(0x2005)] // BeatsX
    [InlineData(0x200B)] // Powerbeats Pro
    [InlineData(0x2012)] // Beats Fit Pro
    public void RejectsUnknownModelsAndBeats(ushort model)
    {
        Assert.False(AirPodsAdvertisement.IsSupportedModel(model));
        Assert.False(AirPodsAdvertisement.TryParse(Packet(model), out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0x2002)]
    [InlineData(0x200F)]
    [InlineData(0x2013)]
    [InlineData(0x2019)]
    [InlineData(0x201B)]
    [InlineData(0x2030)]
    [InlineData(0x2036)]
    [InlineData(0x200E)]
    [InlineData(0x2014)]
    [InlineData(0x2024)]
    [InlineData(0x2027)]
    public void RecognizesKnownEarbudsInLittleEndianOrder(ushort model)
    {
        Assert.True(AirPodsAdvertisement.IsSupportedModel(model));
        Assert.True(AirPodsAdvertisement.TryParse(Packet(model), out var result));
        Assert.Equal(model, result!.Model);
        Assert.Equal(new[] { "left", "right", "case" }, result.Batteries.Select(b => b.Role));
        Assert.All(result.Batteries, b => Assert.True(b.Estimated));
    }

    [Theory]
    [InlineData(0x20, 20, 80)]
    [InlineData(0x00, 80, 20)]
    [InlineData(0x60, 20, 80)] // Other status bits do not change side mapping.
    [InlineData(0x40, 80, 20)]
    public void MapsAsymmetricLevelsUsingBit20(byte status, int left, int right)
    {
        Assert.True(AirPodsAdvertisement.TryParse(Packet(status: status), out var result));
        Assert.Equal(left, result!.Batteries[0].Percentage);
        Assert.Equal(right, result.Batteries[1].Percentage);
        Assert.Equal(50, result.Batteries[2].Percentage);
    }

    [Theory]
    [InlineData(0x20, 0x15, true, false, false)]
    [InlineData(0x00, 0x15, false, true, false)]
    [InlineData(0x20, 0x25, false, true, false)]
    [InlineData(0x00, 0x25, true, false, false)]
    [InlineData(0x20, 0x45, false, false, true)]
    [InlineData(0x20, 0x75, true, true, true)]
    [InlineData(0x20, 0x85, false, false, false)] // Reserved bit is not charging.
    public void ChargingFollowsItsBatterySlot(byte status, byte flags, bool left, bool right, bool box)
    {
        Assert.True(AirPodsAdvertisement.TryParse(Packet(status: status, chargeAndCase: flags), out var result));
        Assert.Equal(left, result!.Batteries[0].Charging);
        Assert.Equal(right, result.Batteries[1].Charging);
        Assert.Equal(box, result.Batteries[2].Charging);
    }

    [Theory]
    [InlineData(0xB)]
    [InlineData(0xC)]
    [InlineData(0xD)]
    [InlineData(0xE)]
    [InlineData(0xF)]
    public void MissingAndInvalidValuesPreserveRolesWithoutInventingZeroOrCharging(int invalid)
    {
        var data = Packet(pods: (byte)(invalid << 4), chargeAndCase: (byte)(0x70 | invalid));
        Assert.True(AirPodsAdvertisement.TryParse(data, out var result));
        Assert.Equal(new[] { "left", "right", "case" }, result!.Batteries.Select(b => b.Role));
        Assert.Equal(0, result.Batteries[0].Percentage);
        Assert.True(result.Batteries[0].Charging);
        Assert.True(result.Batteries[0].Estimated);
        Assert.Null(result.Batteries[1].Percentage);
        Assert.Null(result.Batteries[1].Charging);
        Assert.False(result.Batteries[1].Estimated);
        Assert.Null(result.Batteries[2].Percentage);
        Assert.Null(result.Batteries[2].Charging);
        Assert.False(result.Batteries[2].Estimated);
    }

    [Fact]
    public void ZeroAndHundredAreValidAndCaseAloneCanSupplyBattery()
    {
        Assert.True(AirPodsAdvertisement.TryParse(Packet(pods: 0xA0, chargeAndCase: 0), out var result));
        Assert.Equal(new int?[] { 0, 100, 0 }, result!.Batteries.Select(b => b.Percentage));
        Assert.True(AirPodsAdvertisement.TryParse(Packet(pods: 0xFF, chargeAndCase: 0x4A), out result));
        Assert.Equal(new int?[] { null, null, 100 }, result!.Batteries.Select(b => b.Percentage));
        Assert.True(result.Batteries[2].Charging);
    }

    [Fact]
    public void Pro2UsbCObservedHeaderHasTwoEstimatedLevelsAndUnknownCase()
    {
        // Observed public fields only; the remaining bytes are synthetic zeros.
        var data = new byte[27];
        Convert.FromHexString("07190124202B998F1000").CopyTo(data, 0);
        Assert.True(AirPodsAdvertisement.TryParse(data, out var result));
        Assert.Equal((ushort)0x2024, result!.Model);
        Assert.Equal(new int?[] { 90, 90, null }, result.Batteries.Select(b => b.Percentage));
        Assert.False(result.Batteries[0].Charging);
        Assert.False(result.Batteries[1].Charging);
        Assert.Null(result.Batteries[2].Charging);
        Assert.True(result.Batteries[0].Estimated);
        Assert.True(result.Batteries[1].Estimated);
        Assert.False(result.Batteries[2].Estimated);
    }

    [Theory]
    [InlineData(0xFF, 0x7F)]
    [InlineData(0xBC, 0x0E)]
    public void NoUsableBatteryStillPreservesCandidateAndMissingRoles(byte pods, byte box)
    {
        Assert.True(AirPodsAdvertisement.TryParse(Packet(pods: pods, chargeAndCase: box), out var result));
        Assert.Equal(new[] { "left", "right", "case" }, result!.Batteries.Select(b => b.Role));
        Assert.All(result.Batteries, b =>
        {
            Assert.Null(b.Percentage);
            Assert.Null(b.Charging);
            Assert.False(b.Estimated);
        });
    }

    [Theory]
    [InlineData(0x200A, 0xF8, 0x10, 80, true)]
    [InlineData(0x201F, 0x8F, 0x20, 80, true)]
    [InlineData(0x202D, 0xF0, 0x20, 0, false)]
    [InlineData(0x200A, 0xAF, 0x10, 100, false)]
    [InlineData(0x200A, 0x07, 0x80, 70, false)] // Observed Max battery/status fields.
    [InlineData(0x200A, 0x80, 0x10, 0, true)] // A real zero must not fall back to 80%.
    public void MaxHasOnlyMainAndUsesChargingFromTheValidSlot(ushort model, byte pods, byte flags,
        int percentage, bool charging)
    {
        Assert.True(AirPodsAdvertisement.IsSupportedModel(model));
        Assert.True(AirPodsAdvertisement.TryParse(Packet(model, pods: pods, chargeAndCase: flags), out var result));
        var battery = Assert.Single(result!.Batteries);
        Assert.Equal("main", battery.Role);
        Assert.Equal(percentage, battery.Percentage);
        Assert.Equal(charging, battery.Charging);
        Assert.True(battery.Estimated);
    }

    [Fact]
    public void MaxDoesNotUseCaseAsHeadphoneBattery()
    {
        Assert.True(AirPodsAdvertisement.TryParse(Packet(0x200A, pods: 0xFF, chargeAndCase: 0x4A), out var result));
        var battery = Assert.Single(result!.Batteries);
        Assert.Equal("main", battery.Role);
        Assert.Null(battery.Percentage);
        Assert.Null(battery.Charging);
        Assert.False(battery.Estimated);
    }
}
