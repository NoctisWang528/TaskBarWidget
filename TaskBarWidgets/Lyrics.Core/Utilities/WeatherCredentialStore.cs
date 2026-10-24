using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarLyrics.Core.Utilities;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WeatherCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TaskBarWidgets/weather-appkey/v1");
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly object Gate = new();

    public string Path { get; }

    public WeatherCredentialStore(string? path = null) =>
        Path = path ?? AppDataPaths.WeatherCredentialPath;

    public bool Exists => File.Exists(Path);

    public bool TryRead(out string? appKey)
    {
        appKey = null;
        lock (Gate)
        {
            if (!File.Exists(Path)) return false;
            try
            {
                var envelope = JsonSerializer.Deserialize<CredentialEnvelope>(File.ReadAllText(Path), JsonOptions);
                if (envelope is null || envelope.Version != 1 || envelope.ProtectedData.Length == 0) return false;
                var encoded = envelope.ProtectedData.Trim();
                encoded += new string('=', (4 - encoded.Length % 4) % 4);
                var plaintext = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(encoded), Entropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                appKey = Decode(plaintext);
                CryptographicOperations.ZeroMemory(plaintext);
                return !string.IsNullOrWhiteSpace(appKey);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                              JsonException or FormatException or CryptographicException)
            {
                Log.Warn("天气密钥无法解密，需要重新输入。");
                return false;
            }
        }
    }

    public void Write(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey)) throw new ArgumentException("Weather key cannot be empty.", nameof(appKey));
        // The native weather module uses Windows UTF-16 strings with DPAPI.
        var plaintext = Encoding.Unicode.GetBytes(appKey);
        try
        {
            var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
                plaintext, Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var envelope = new CredentialEnvelope
            {
                Version = 1,
                ProtectedData = Convert.ToBase64String(protectedBytes)
            };
            DataLayout.WriteJson(Path, envelope);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public bool Clear()
    {
        lock (Gate)
        {
            if (!File.Exists(Path)) return true;
            try
            {
                File.Delete(Path);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Error("无法清除天气密钥：" + error.Message);
                return false;
            }
        }
    }

    private sealed class CredentialEnvelope
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("protectedData")]
        public string ProtectedData { get; set; } = "";
    }

    private static string? Decode(byte[] bytes)
    {
        // Current native and managed writers use UTF-16LE. Prefer a valid UTF-8
        // interpretation first so an early managed UTF-8 value is not misread.
        string legacy;
        try { legacy = StrictUtf8.GetString(bytes).TrimEnd('\0'); }
        catch (DecoderFallbackException) { legacy = ""; }
        if (Valid(legacy)) return legacy;

        if (bytes.Length > 0 && bytes.Length % 2 == 0)
        {
            var wide = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            if (Valid(wide)) return wide;
        }
        return null;
    }

    private static bool Valid(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 &&
        value.All(character => !char.IsControl(character));
}
