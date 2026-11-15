using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskBarWidgets.Media.Peripherals;

internal static class PeripheralDiagnostics
{
    internal static string Export(Func<byte[], string> entities, IEnumerable<JsonElement> protocols)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        string Token(string value) => "redacted-" + Convert.ToHexString(HMACSHA256.HashData(salt,
            Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..16].ToLowerInvariant();
        // Keep a small schema rather than copying arbitrary future helper fields
        // into a report users may share. Identifiers are correlated within it only.
        var entries = protocols.Take(512).Select(entry =>
        {
            var result = new Dictionary<string, object?>();
            if (entry.ValueKind != JsonValueKind.Object) return result;
            foreach (var field in entry.EnumerateObject())
            {
                if (field.Name is "vid" or "pid" or "usagePage" or "usage" or "feature" or "input" or "output")
                {
                    if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var number)) result[field.Name] = number;
                }
                else if (field.Name is "path" or "endpointPath" or "serial" or "address" or "containerId")
                    result[field.Name] = Token(field.Value.ToString());
                else if (field.Name is "protocol" or "state" or "reason")
                {
                    var text = field.Value.ToString();
                    result[field.Name] = Regex.IsMatch(text, "^[A-Za-z0-9 _:.()/-]{0,120}$", RegexOptions.CultureInvariant)
                        && !text.Contains('\\') && !text.Contains(":/") ? text : Token(text);
                }
            }
            return result;
        }).ToArray();
        using var document = JsonDocument.Parse(entities(salt));
        return JsonSerializer.Serialize(new { version = 1, generatedAt = DateTimeOffset.UtcNow,
            upstream = "evren-oct/vigil-watt@4df830043296760f5d5c273f4cdc0ea9456c4bc7",
            entities = document.RootElement, protocols = entries }, new JsonSerializerOptions { WriteIndented = true });
    }
}
