using System.Globalization;

namespace TaskBarWidgets.Media.Updates;

public sealed record UpdateRequirements(string DotnetDesktop, string WindowsAppRuntime, int WindowsBuild);

public sealed record UpdateSnapshot
{
    public long Revision { get; init; }
    public string Phase { get; init; } = "idle";
    public string CurrentVersion { get; init; } = "";
    public string? LatestVersion { get; init; }
    public string? Notes { get; init; }
    public string ReleaseUrl { get; init; } = UpdateCoordinator.ReleasesUrl;
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public bool AutomaticEnabled { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public bool CanDownload { get; init; }
    public bool CanInstall { get; init; }
}

public sealed record PreparedUpdate(string Version, string StageRoot, string PackagePath,
    string PackageSha256, string FileManifestSha256, UpdateRequirements Requirements, string CacheRoot);

internal sealed record UpdateMetadata(int ProtocolVersion, string Version, string Architecture,
    string AssetName, long Size, string Sha256, string FileManifestSha256, UpdateRequirements Requirements);

internal sealed record UpdateCandidate(UpdateMetadata Metadata, string PackageUrl, string ReleaseUrl, string Notes);
internal sealed record AppFile(string Path, long Bytes, string Sha256);
internal sealed record AppFileManifest(int ProtocolVersion, string Version, string Architecture, AppFile[] Files);

internal static class UpdateVersion
{
    internal static bool TryParse(string? value, out DateTime timestamp)
    {
        timestamp = default;
        if (value is null || value.Length != 11 || value[6] != '-' ||
            value.Where((_, i) => i != 6).Any(c => c is < '0' or > '9')) return false;
        return DateTime.TryParseExact("20" + value, "yyyyMMdd-HHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out timestamp);
    }
}
