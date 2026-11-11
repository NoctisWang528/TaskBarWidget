using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TaskBarWidgets.Media.Peripherals;

public sealed record PeripheralBatteryReading
{
    [JsonPropertyName("role")] public string Role { get; init; } = "main";
    [JsonPropertyName("percentage")] public int? Percentage { get; init; }
    [JsonPropertyName("levelText")] public string? LevelText { get; init; }
    [JsonPropertyName("estimated")] public bool Estimated { get; init; }
    [JsonPropertyName("charging")] public bool? Charging { get; init; }
}

public sealed record PeripheralDeviceSnapshot
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("brand")] public string Brand { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "unknown";
    [JsonPropertyName("connection")] public string Connection { get; init; } = "unknown";
    [JsonPropertyName("state")] public string State { get; init; } = "unavailable";
    [JsonPropertyName("batteries")] public PeripheralBatteryReading[] Batteries { get; init; } = [];
    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; init; }
    [JsonPropertyName("stale")] public bool Stale { get; init; }
    [JsonPropertyName("showInCard")] public bool ShowInCard { get; init; }
    [JsonPropertyName("canRead")] public bool CanRead { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("compatibility")] public string Compatibility { get; init; } = "identityOnly";
    // Proven protocol/USB identities used only to retain existing local selections.
    [JsonIgnore] internal string[] LegacyIds { get; init; } = [];
}

public sealed record PeripheralBatterySnapshot
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("revision")] public long Revision { get; init; }
    [JsonPropertyName("mode")] public string Mode { get; init; } = "compact";
    [JsonPropertyName("hideDisconnected")] public bool HideDisconnected { get; init; }
    [JsonPropertyName("refreshing")] public bool Refreshing { get; init; }
    [JsonPropertyName("error")] public string Error { get; init; } = "";
    [JsonPropertyName("devices")] public PeripheralDeviceSnapshot[] Devices { get; init; } = [];
}

public sealed class PeripheralBatterySettings
{
    public string Mode { get; set; } = "compact";
    public bool HideDisconnected { get; set; }
    public bool HeadsetControlEnabled { get; set; }
    public bool VendorExtensionsEnabled { get; set; }
    public bool AirPodsAdvertisementEnabled { get; set; } = true;
    public List<string> Order { get; set; } = [];
    public Dictionary<string, bool> CardVisibility { get; set; } = new(StringComparer.Ordinal);

    public PeripheralBatterySettings Clone() => new()
    {
        Mode = Mode, HideDisconnected = HideDisconnected,
        HeadsetControlEnabled = HeadsetControlEnabled, VendorExtensionsEnabled = VendorExtensionsEnabled,
        AirPodsAdvertisementEnabled = AirPodsAdvertisementEnabled,
        Order = [.. Order ?? []], CardVisibility = new(CardVisibility ?? [], StringComparer.Ordinal)
    };

    public void Normalize()
    {
        Mode = Mode == "detailed" ? "detailed" : "compact";
        Order = (Order ?? []).Where(PeripheralIdentity.IsValid).Distinct(StringComparer.Ordinal).Take(256).ToList();
        CardVisibility = (CardVisibility ?? []).Where(p => PeripheralIdentity.IsValid(p.Key)).Take(256)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }
}

public static class PeripheralIdentity
{
    internal static string Brand(string name, string identity = "", string source = "")
    {
        bool Name(string value) => name.Contains(value, StringComparison.OrdinalIgnoreCase);
        bool Vendor(string vid) => identity.Contains("|vid=" + vid + "|", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("VID_" + vid, StringComparison.OrdinalIgnoreCase);
        if (Vendor("046d") || source == "hidpp20" || Name("Logitech") || Name("罗技")) return "logitech";
        if (Vendor("1532") || Name("Razer") || Name("雷蛇")) return "razer";
        if (source.StartsWith("rog", StringComparison.OrdinalIgnoreCase) || Name("ROG") || Name("夜魔")) return "asusrog";
        if (Name("Xiaomi") || Name("小米") || Name("Redmi")) return "xiaomi";
        if (source == "apple-ble-proximity" || Name("AirPods")) return "apple";
        return "";
    }

    public static string FromKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim().ToLowerInvariant())))[..32].ToLowerInvariant();
    public static bool IsValid(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal interface IPeripheralBatteryProvider : IAsyncDisposable
{
    event Action? DevicesChanged;
    Task<IReadOnlyList<PeripheralDeviceSnapshot>> DiscoverAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PeripheralDeviceSnapshot>> ReadAsync(IReadOnlySet<string> deviceIds,
        PeripheralBatterySettings settings, CancellationToken cancellationToken);
}

internal interface IPeripheralBatteryIdle
{
    Task SuspendAsync();
}

internal interface IPeripheralBatteryNotifications
{
    event Action<PeripheralDeviceSnapshot>? ReadingChanged;
    Task SetDemandAsync(IReadOnlySet<string> deviceIds, CancellationToken cancellationToken);
}
