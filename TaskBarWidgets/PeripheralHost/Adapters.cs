// Host-only adapters: no settings, history, capture or log files are opened.
namespace VigilWatt.Core.Settings
{
    public sealed class AppSettings
    {
        private static readonly AppSettings Current = new();
        public Dictionary<string, bool> PluginEnabled { get; } = new(StringComparer.Ordinal);
        public static AppSettings Load() => Current;
    }
}

namespace VigilWatt.Core.Devices
{
    public static class DeviceBlocklist
    {
        // Only the current request's endpoint-local child slots may be queried.
        public static HashSet<string>? AllowedIds { get; set; }
        public static bool IsBlocked(string id) => AllowedIds != null && !AllowedIds.Contains(id);
    }
}

namespace VigilWatt.Core.Utils
{
    public static class Log
    {
        public static bool IsEnabled(LogLevel level) => false;
        public static void Debug(string message) { }
        public static void Info(string message) { }
        public static void Warn(string message) { }
        public static void Error(string message, Exception? ex = null) { }
        public static bool InfoOnChange(string key, string state, string message) => false;
        public static bool InfoOnChange(string key, string message) => false;
        public static bool Once(LogLevel level, string key, string message) => false;
        public static void ResetOnce(string key) { }
        public static void InfoThrottled(string key, string message, TimeSpan interval) { }
        public static string Hex(ReadOnlySpan<byte> bytes) => "";
    }
}
