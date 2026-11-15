using System.Text.Json;

namespace TaskBarWidgets.PeripheralHost;

internal sealed record Request(int RequestId, string Operation, string[] DeviceIds, string[] ExcludedPaths, string[] ExcludedIdentities)
{
    public string[] QueryIds { get; init; } = [];
    public static Request Parse(ReadOnlyMemory<byte> line, out int correlation)
    {
        correlation = -1;
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("invalid_request");
        if (root.TryGetProperty("requestId", out var id) && id.TryGetInt32(out int number)) correlation = number;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name)) throw new InvalidDataException("duplicate_property");
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int v) || v != 1)
            throw new InvalidDataException("unsupported_version");
        if (!root.TryGetProperty("requestId", out id) || !id.TryGetInt32(out _))
            throw new InvalidDataException("invalid_request_id");
        if (!root.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String
            || operation.GetString() is not ("discover" or "read")) throw new InvalidDataException("unsupported_operation");
        string[] Strings(string key, int limit, int length, bool required = false)
        {
            if (!root.TryGetProperty(key, out var array))
            {
                if (required) throw new InvalidDataException("missing_device_ids");
                return [];
            }
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > limit)
                throw new InvalidDataException("invalid_array");
            var values = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value
                    || value.Length == 0 || value.Length > length || value.Any(char.IsControl))
                    throw new InvalidDataException("invalid_string");
                values.Add(value);
            }
            return values.Distinct(StringComparer.Ordinal).ToArray();
        }
        string op = operation.GetString()!;
        return new Request(correlation, op, Strings("deviceIds", 256, 1024, op == "read"),
            Strings("excludedPaths", 4096, 4096), Strings("excludedIdentities", 4096, 4096))
            { QueryIds = Strings("queryIds", 256, 1024) };
    }
}

internal sealed record Battery(int? Percentage, bool? Charging, bool Estimated = false, string Role = "main", string? LevelText = null);
internal sealed record IdentityEvidence(string ContainerId, string BluetoothAddress, string UniqueSerial,
    string SerialScope, string UnitId, string UnitScope, string EndpointPath, bool ContainerIsDevice);
internal sealed record Device(string Identity, string Name, string Kind, string Connection, string State,
    Battery[] Batteries, DateTimeOffset? UpdatedAt, bool Stale, bool CanRead, string Source,
    string Compatibility, IdentityEvidence IdentityEvidence, int NameQuality, int KindQuality)
{
    public string QueryId { get; init; } = "";
}
internal sealed record Diagnostic(string Path, int Vid, int Pid, int UsagePage, int Usage,
    int Feature, int Input, int Output, string Protocol, string State, string Reason);
internal sealed record Response(int RequestId, Device[] Devices, string[] ClaimedPaths, Diagnostic[] Diagnostics, string Error = "")
{
    public int Version => 1;
    public static Response Failure(int id, string code) => new(id, [], [], [], code);
}

internal static class Wire
{
    public const int MaximumLineBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly object OutputGate = new();

    public static void Write(object value, int requestId)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length + 1 > MaximumLineBytes)
            bytes = JsonSerializer.SerializeToUtf8Bytes(Response.Failure(requestId, "response_too_large"), Json);
        lock (OutputGate)
        {
            var output = Console.OpenStandardOutput();
            output.Write(bytes);
            output.WriteByte((byte)'\n');
            output.Flush();
        }
    }
}
