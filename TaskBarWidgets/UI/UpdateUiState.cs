using System.Text.Json;

namespace TaskBarWidgets.UI;

internal sealed record UpdateUiState(long Revision = -1, string Phase = "idle", string CurrentVersion = "", string LatestVersion = "",
    string Notes = "", long BytesDownloaded = 0, long TotalBytes = 0, bool AutomaticEnabled = true,
    string LastCheckedAt = "", string Error = "", string Message = "", bool CanDownload = false, bool CanInstall = false)
{
    internal static UpdateUiState? Parse(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object) value = payload;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number) || number < 0) return null;
        string Text(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
        long Count(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var count) ? Math.Max(0, count) : 0;
        bool Flag(string key, bool fallback = false) => value.TryGetProperty(key, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : fallback;
        return new(number, Text("phase"), Text("currentVersion"), Text("latestVersion"), Text("notes"), Count("bytesDownloaded"), Count("totalBytes"),
            Flag("automaticEnabled", true), Text("lastCheckedAt"), Text("error"), Text("message"), Flag("canDownload"), Flag("canInstall"));
    }

    internal bool Busy => Phase is "checking" or "downloading" or "verifying" or "preparing" or "installing";
    internal string Status => !string.IsNullOrWhiteSpace(Error) ? Error : !string.IsNullOrWhiteSpace(Message) ? Message : Phase switch {
        "checking" => "正在检查更新…", "available" => "发现新版本，可下载更新。", "downloading" => "正在下载更新…",
        "verifying" => "正在校验更新…", "ready" => "更新已准备好，安装需要你的确认。", "preparing" => "正在准备安装…",
        "installing" => "正在安装更新…", "manualRequired" => "请前往项目主页手动更新。", "error" => "更新未完成，请重试。", _ => "尚未检查更新。" };
}
