using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskBarWidgets.Media.Updates;

/// <summary>Downloads and verifies official packages. It never executes downloaded files.</summary>
public sealed class UpdateCoordinator : IAsyncDisposable
{
    public const string ReleasesUrl = "https://github.com/NoctisWang528/TaskBarWidget/releases";
    private const string RepositoryUrl = "https://github.com/NoctisWang528/TaskBarWidget";
    private const string LatestApi = "https://api.github.com/repos/NoctisWang528/TaskBarWidget/releases/latest";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private readonly object _gate = new();
    private readonly object _notificationGate = new();
    private readonly HttpClient _http;
    private readonly Action<UpdateSnapshot> _onChanged;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private UpdateSnapshot _snapshot;
    private UpdateCandidate? _candidate;
    private PreparedUpdate? _prepared;
    private Task? _operation;
    private Task _initialization = Task.CompletedTask;
    private Task? _scheduler;
    private CancellationTokenSource? _operationCancellation;
    private bool _operationAutomatic;
    private bool _started;
    private bool _disposed;
    private bool _installationPrepared;
    private DateTimeOffset? _retryAfter;
    private DateTimeOffset? _nextAutomaticCheck;

    public string CacheRoot { get; }
    public UpdateSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public Task Initialization { get { lock (_gate) return _initialization; } }
    internal Task WaitForIdleAsync() { lock (_gate) return _operation ?? Task.CompletedTask; }

    public UpdateCoordinator(string installationRoot, string currentVersion, bool automaticEnabled,
        Action<UpdateSnapshot> onChanged, string? cacheRootOverride = null, HttpMessageHandler? httpMessageHandler = null,
        TimeProvider? timeProvider = null)
    {
        var installation = Path.GetFullPath(installationRoot).TrimEnd(Path.DirectorySeparatorChar);
        UpdatePackage.EnsureSafePath(installation);
        var installHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation.ToUpperInvariant()))).ToLowerInvariant();
        CacheRoot = Path.GetFullPath(cacheRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskBarWidgets", "Updates", installHash));
        UpdatePackage.EnsureSafePath(CacheRoot);
        if (CacheRoot.Equals(installation, StringComparison.OrdinalIgnoreCase) ||
            CacheRoot.StartsWith(installation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Update cache must be outside the installation directory.", nameof(cacheRootOverride));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _time = timeProvider ?? TimeProvider.System;
        _http = new HttpClient(httpMessageHandler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
            { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskBarWidgets-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _snapshot = new UpdateSnapshot { CurrentVersion = currentVersion, AutomaticEnabled = automaticEnabled };
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            BeginLocked(RestoreAsync, false);
            _initialization = _operation!;
            _scheduler = Task.Run(ScheduleAsync);
        }
    }

    public UpdateSnapshot Check(bool userInitiated = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_installationPrepared || _operation is { IsCompleted: false } || (!userInitiated && !_snapshot.AutomaticEnabled)) return _snapshot;
            BeginLocked(token => CheckAsync(!userInitiated, token), !userInitiated);
            return _snapshot;
        }
    }

    public UpdateSnapshot Download()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_installationPrepared || _operation is { IsCompleted: false } || _candidate is null || _prepared is not null) return _snapshot;
            BeginLocked(token => DownloadAsync(_candidate, token), false);
            return _snapshot;
        }
    }

    public UpdateSnapshot CancelDownload()
    {
        lock (_gate)
        {
            if (!_installationPrepared && _snapshot.Phase is "downloading" or "verifying") _operationCancellation?.Cancel();
            return _snapshot;
        }
    }

    public UpdateSnapshot SetAutomatic(bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!enabled && _operationAutomatic && !_installationPrepared) _operationCancellation?.Cancel();
        }
        Change(state => state with { AutomaticEnabled = enabled });
        return Snapshot;
    }

    /// <summary>Locks the ready package against further coordinator mutations, then revalidates it.</summary>
    public Task<PreparedUpdate> PrepareInstall()
    {
        // Match Change lock order when publishing the synchronous preparing state.
        lock (_notificationGate)
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_installationPrepared || _operation is { IsCompleted: false } || _prepared is null || _candidate is null || _snapshot.Phase != "ready")
                throw new InvalidOperationException("No verified update is ready for installation.");
            _installationPrepared = true;
            Change(state => state with { Phase = "preparing", CanDownload = false, CanInstall = false, Error = null });
            var prepared = _prepared;
            var metadata = _candidate.Metadata;
            var completion = new TaskCompletionSource<PreparedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginLocked(async token =>
            {
                try
                {
                    await ValidatePrepared(prepared, metadata, token);
                    completion.TrySetResult(prepared);
                }
                catch (Exception error)
                {
                    lock (_gate) { _installationPrepared = false; _prepared = null; }
                    completion.TrySetException(error);
                    throw;
                }
            }, false);
            return completion.Task;
        }
    }

    public void SetInstallStatus(string phase, string? error = null)
    {
        if (phase is not ("preparing" or "installing" or "ready" or "error")) throw new ArgumentException("Invalid install state.", nameof(phase));
        lock (_gate)
        {
            if (_disposed) return;
            if (phase is "ready" or "error") _installationPrepared = false;
        }
        Change(state => state with { Phase = phase, Error = error, Message = error, CanDownload = false, CanInstall = phase == "ready" && _prepared is not null });
    }

    private void BeginLocked(Func<CancellationToken, Task> operation, bool automatic)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _operationCancellation.Token;
        _operationAutomatic = automatic;
        _operation = Task.Run(async () =>
        {
            try { await operation(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Change(state => state with { Phase = _prepared is not null ? "ready" : _candidate is not null ? "available" : "idle",
                    LatestVersion = _candidate?.Metadata.Version ?? state.LatestVersion,
                    Notes = _candidate?.Notes ?? state.Notes, ReleaseUrl = _candidate?.ReleaseUrl ?? state.ReleaseUrl,
                    TotalBytes = _candidate?.Metadata.Size ?? state.TotalBytes, BytesDownloaded = _prepared is not null ? _candidate?.Metadata.Size ?? 0 : 0,
                    CanDownload = _candidate is not null && _prepared is null, CanInstall = _prepared is not null, Error = null, Message = "Update operation cancelled." });
            }
            catch (Exception error)
            {
                lock (_gate) _nextAutomaticCheck = _retryAfter ?? _time.GetUtcNow().AddMinutes(15);
                Change(state => state with { Phase = "error", Error = SafeError(error), Message = "The update could not be verified. Try again later.",
                    CanDownload = _candidate is not null && _prepared is null, CanInstall = false });
                try { await PersistAsync(CancellationToken.None); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });
    }

    private void Change(Func<UpdateSnapshot, UpdateSnapshot> change)
    {
        // Preserve callback order as well as snapshot revision order when a preference changes
        // concurrently with a download. The host may coalesce pending notifications.
        lock (_notificationGate)
        {
            UpdateSnapshot snapshot;
            lock (_gate)
            {
                if (_disposed) return;
                snapshot = _snapshot = change(_snapshot) with { Revision = checked(_snapshot.Revision + 1) };
            }
            try { _onChanged(snapshot); } catch { /* A disconnected UI must not break update verification. */ }
        }
    }

    private async Task ScheduleAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), _time, _lifetime.Token);
            while (!_lifetime.IsCancellationRequested)
            {
                lock (_gate)
                {
                    var due = _nextAutomaticCheck ?? _snapshot.LastCheckedAt?.Add(CheckInterval);
                    if (_snapshot.AutomaticEnabled && !_installationPrepared &&
                        _operation is not { IsCompleted: false } && (!due.HasValue || _time.GetUtcNow() >= due) &&
                        (!_retryAfter.HasValue || _time.GetUtcNow() >= _retryAfter))
                        BeginLocked(token => CheckAsync(true, token), true);
                }
                await Task.Delay(TimeSpan.FromMinutes(1), _time, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task CheckAsync(bool automatic, CancellationToken token)
    {
        Change(state => state with { Phase = "checking", Error = null, Message = null, CanDownload = false, CanInstall = false });
        if (!UpdateVersion.TryParse(Snapshot.CurrentVersion, out var current))
        {
            ManualRequired("This development version requires a manual update.");
            return;
        }
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        var bytes = await GetSmall(new Uri(LatestApi), 1024 * 1024, requestTimeout.Token);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        var release = document.RootElement;
        var checkedAt = _time.GetUtcNow();
        lock (_gate) { _nextAutomaticCheck = checkedAt.Add(CheckInterval); _retryAfter = null; }
        Change(state => state with { LastCheckedAt = checkedAt });
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() || release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
        { ManualRequired("Only stable official releases support automatic updates."); await PersistAsync(token); return; }
        var tag = GetString(release, "tag_name");
        var releaseUrl = GetString(release, "html_url");
        if (!UpdateVersion.TryParse(tag, out var latest) || releaseUrl != RepositoryUrl + "/releases/tag/" + tag)
        { ManualRequired("This release does not provide compatible update metadata."); await PersistAsync(token); return; }
        if (latest <= current)
        {
            lock (_gate) { _candidate = null; _prepared = null; }
            Change(state => state with { Phase = "idle", LatestVersion = tag, ReleaseUrl = releaseUrl!, Notes = null,
                BytesDownloaded = 0, TotalBytes = 0, CanDownload = false, CanInstall = false, Message = "You are using the latest version." });
            await PersistAsync(token);
            return;
        }
        var notes = GetString(release, "body") ?? "";
        if (notes.Length > 32768) notes = notes[..32768];
        Change(state => state with { LatestVersion = tag, Notes = notes, ReleaseUrl = releaseUrl! });
        var metadataVerified = false;
        try
        {
            var assets = ReadAssets(release, tag!);
            if (!assets.TryGetValue("update.json", out var metadataAsset) || !assets.TryGetValue("SHA256SUMS.txt", out var sumsAsset))
                throw new InvalidDataException("The release does not have updater metadata.");
            if (metadataAsset.Size > 65536 || sumsAsset.Size > 256 * 1024) throw new InvalidDataException("Updater metadata is too large.");
            var metadataBytes = await GetSmall(metadataAsset.Url, 65536, requestTimeout.Token, metadataAsset.Size);
            var metadata = UpdatePackage.ParseJson<UpdateMetadata>(metadataBytes);
            ValidateMetadata(metadata);
            if (metadata.Version != tag || !assets.TryGetValue(metadata.AssetName, out var package) || package.Size != metadata.Size)
                throw new InvalidDataException("Release asset metadata disagrees with the update manifest.");
            var sums = Encoding.UTF8.GetString(await GetSmall(sumsAsset.Url, 256 * 1024, requestTimeout.Token, sumsAsset.Size));
            var matching = new List<string>();
            foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var text = line.TrimEnd('\r');
                if (text.Length < 67 || text[64] != ' ' || (text[65] != ' ' && text[65] != '*') || !UpdatePackage.IsHash(text[..64]))
                    throw new InvalidDataException("Invalid release checksum manifest.");
                if (text[66..].Equals(metadata.AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    if (text[66..] != metadata.AssetName) throw new InvalidDataException("Checksum asset name casing mismatch.");
                    matching.Add(text[..64]);
                }
            }
            if (matching.Count != 1 || !matching[0].Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release checksums disagree with the update metadata.");
            metadataVerified = true;
            var candidate = new UpdateCandidate(metadata, package.Url.AbsoluteUri, releaseUrl!, notes);
            PreparedUpdate? reusable;
            lock (_gate)
            {
                reusable = _candidate?.Metadata == metadata ? _prepared : null;
                _candidate = candidate;
                _prepared = null;
            }
            if (reusable is not null)
            {
                Change(state => state with { Phase = "verifying", BytesDownloaded = metadata.Size, TotalBytes = metadata.Size });
                await ValidatePrepared(reusable, metadata, token);
                lock (_gate) _prepared = reusable;
                await PersistAsync(token);
                Change(state => state with { Phase = "ready", CanDownload = false, CanInstall = true });
                return;
            }
            Change(state => state with { Phase = "available", TotalBytes = metadata.Size, BytesDownloaded = 0, CanDownload = true, CanInstall = false });
            await PersistAsync(token);
            if (automatic && Snapshot.AutomaticEnabled) await DownloadAsync(candidate, token);
        }
        catch (Exception error) when (!metadataVerified && (error is InvalidDataException or JsonException))
        {
            ManualRequired("This release requires a manual download: " + SafeError(error));
            await PersistAsync(token);
        }
    }

    private void ManualRequired(string message)
    {
        lock (_gate) { _candidate = null; _prepared = null; }
        Change(state => state with { Phase = "manualRequired", Error = null, Message = message, CanDownload = false, CanInstall = false });
    }

    private static void ValidateMetadata(UpdateMetadata metadata)
    {
        if (metadata.ProtocolVersion != 1 || metadata.Architecture != "win-x64" || !UpdateVersion.TryParse(metadata.Version, out _) ||
            metadata.AssetName != $"TaskBarWidgets-{metadata.Version}-win-x64.zip" || metadata.Size <= 0 || metadata.Size > UpdatePackage.MaxZipBytes ||
            !UpdatePackage.IsHash(metadata.Sha256) || !UpdatePackage.IsHash(metadata.FileManifestSha256) || metadata.Requirements is null ||
            !Version.TryParse(metadata.Requirements.DotnetDesktop, out _) || !Version.TryParse(metadata.Requirements.WindowsAppRuntime, out _) ||
            metadata.Requirements.WindowsBuild <= 0)
            throw new InvalidDataException("Incompatible update metadata.");
    }

    private sealed record ReleaseAsset(Uri Url, long Size);
    private static Dictionary<string, ReleaseAsset> ReadAssets(JsonElement release, string tag)
    {
        var result = new Dictionary<string, ReleaseAsset>(StringComparer.OrdinalIgnoreCase);
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 128)
            throw new InvalidDataException("Missing release assets.");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (name is null || name.Contains('/') || name.Contains('\\') || !asset.TryGetProperty("size", out var sizeElement) ||
                !sizeElement.TryGetInt64(out var size) || size < 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.AbsoluteUri != RepositoryUrl + "/releases/download/" + tag + "/" + Uri.EscapeDataString(name) ||
                !result.TryAdd(name, new ReleaseAsset(uri, size)))
                throw new InvalidDataException("Untrusted or duplicate release asset metadata.");
        }
        return result;
    }

    private async Task DownloadAsync(UpdateCandidate candidate, CancellationToken token)
    {
        var metadata = candidate.Metadata;
        Change(state => state with { Phase = "downloading", BytesDownloaded = 0, TotalBytes = metadata.Size, Error = null, Message = null, CanDownload = false, CanInstall = false });
        var directory = UpdatePackage.ChildPath(CacheRoot, "packages/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var package = UpdatePackage.ChildPath(directory, metadata.AssetName);
        var partial = package + ".partial";
        var stage = UpdatePackage.ChildPath(directory, "stage");
        var success = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using (var response = await Send(new Uri(candidate.PackageUrl), timeout.Token))
            {
                if (response.Content.Headers.ContentLength is { } length && length != metadata.Size)
                    throw new InvalidDataException("The ZIP size does not match release metadata.");
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var nextProgress = _time.GetUtcNow();
                await UpdatePackage.CopyBounded(source, output, metadata.Size, timeout.Token, count =>
                {
                    var now = _time.GetUtcNow();
                    if (count != metadata.Size && now < nextProgress) return;
                    nextProgress = now.AddMilliseconds(200);
                    Change(state => state with { BytesDownloaded = count });
                });
                await output.FlushAsync(timeout.Token);
            }
            UpdatePackage.EnsureSafePath(package);
            File.Move(partial, package);
            Change(state => state with { Phase = "verifying" });
            await UpdatePackage.ExtractAndVerify(package, stage, metadata, token);
            var prepared = new PreparedUpdate(metadata.Version, stage, package, metadata.Sha256, metadata.FileManifestSha256, metadata.Requirements, CacheRoot);
            lock (_gate) _prepared = prepared;
            await PersistAsync(token);
            success = true;
            Change(state => state with { Phase = "ready", CanDownload = false, CanInstall = true, BytesDownloaded = metadata.Size });
        }
        finally
        {
            if (!success)
            {
                lock (_gate) _prepared = null;
                SafeDeleteDirectory(directory);
            }
        }
    }

    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<byte[]> GetSmall(Uri uri, int limit, CancellationToken token, long? exactSize = null)
    {
        using var response = await Send(uri, token);
        if (response.Content.Headers.ContentLength is { } length && (length > limit || exactSize.HasValue && length != exactSize))
            throw new InvalidDataException("Update metadata exceeds its size limit or disagrees with GitHub metadata.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Update metadata exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        if (exactSize.HasValue && output.Length != exactSize) throw new InvalidDataException("Update metadata size mismatch.");
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> Send(Uri uri, CancellationToken token)
    {
        lock (_gate)
            if (_retryAfter is { } retry && retry > _time.GetUtcNow()) throw new HttpRequestException("GitHub asked the updater to wait before retrying.");
        for (var attempt = 0; ; attempt++)
        {
            var current = uri;
            for (var redirect = 0; ; redirect++)
            {
                if (!IsOfficialDownloadUri(current)) throw new InvalidDataException("Untrusted update download host.");
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.RequestMessage?.RequestUri is { } actual && !IsOfficialDownloadUri(actual))
                { response.Dispose(); throw new InvalidDataException("Untrusted automatic redirect."); }
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location is null || redirect >= 5) throw new InvalidDataException("Invalid update redirect.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
                if (response.IsSuccessStatusCode) return response;
                var status = response.StatusCode;
                if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden || response.Headers.RetryAfter is not null)
                {
                    var next = response.Headers.RetryAfter?.Date ?? _time.GetUtcNow().Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(15));
                    if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out var reset) && reset is >= 0 and <= 253402300799)
                    {
                        var rateReset = DateTimeOffset.FromUnixTimeSeconds(reset);
                        if (rateReset > next) next = rateReset;
                    }
                    lock (_gate) _retryAfter = next > _time.GetUtcNow() ? next : _time.GetUtcNow().AddSeconds(1);
                    response.Dispose();
                    throw new HttpRequestException("GitHub rate limit: retry after " + next.ToString("O"), null, status);
                }
                response.Dispose();
                if ((int)status < 500 || attempt >= 2) throw new HttpRequestException("GitHub update request failed: " + (int)status, null, status);
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), _time, token);
                break;
            }
        }
    }

    internal static bool IsOfficialDownloadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private sealed record CachedState(DateTimeOffset? LastCheckedAt, DateTimeOffset? RetryAfter, DateTimeOffset? NextAutomaticCheck,
        UpdateCandidate? Candidate, PreparedUpdate? Prepared);

    private async Task PersistAsync(CancellationToken token)
    {
        await _persistGate.WaitAsync(token);
        string? temporary = null;
        try
        {
            CachedState state;
            lock (_gate) state = new(_snapshot.LastCheckedAt, _retryAfter, _nextAutomaticCheck, _candidate, _prepared);
            UpdatePackage.EnsureSafePath(CacheRoot);
            Directory.CreateDirectory(CacheRoot);
            var path = UpdatePackage.ChildPath(CacheRoot, "state.json");
            temporary = UpdatePackage.ChildPath(CacheRoot, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, UpdatePackage.Json, token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            UpdatePackage.EnsureSafePath(path);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (temporary is not null) { try { UpdatePackage.EnsureSafePath(temporary); File.Delete(temporary); } catch (IOException) { } }
            _persistGate.Release();
        }
    }

    private async Task RestoreAsync(CancellationToken token)
    {
        var path = UpdatePackage.ChildPath(CacheRoot, "state.json");
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("Oversized update cache state.");
            var state = UpdatePackage.ParseJson<CachedState>(await File.ReadAllBytesAsync(path, token));
            if (state is null) return;
            lock (_gate) { _retryAfter = state.RetryAfter; _nextAutomaticCheck = state.NextAutomaticCheck; }
            Change(snapshot => snapshot with { LastCheckedAt = state.LastCheckedAt });
            if (state.Candidate is not { } candidate || !UpdateVersion.TryParse(Snapshot.CurrentVersion, out var current) ||
                !UpdateVersion.TryParse(candidate.Metadata.Version, out var latest) || latest <= current) return;
            ValidateMetadata(candidate.Metadata);
            if (candidate.ReleaseUrl != RepositoryUrl + "/releases/tag/" + candidate.Metadata.Version ||
                candidate.PackageUrl != RepositoryUrl + "/releases/download/" + candidate.Metadata.Version + "/" + candidate.Metadata.AssetName)
                throw new InvalidDataException("Untrusted cached update URL.");
            lock (_gate) _candidate = candidate;
            Change(snapshot => snapshot with { Phase = "available", LatestVersion = candidate.Metadata.Version, Notes = candidate.Notes,
                ReleaseUrl = candidate.ReleaseUrl, TotalBytes = candidate.Metadata.Size, CanDownload = true });
            if (state.Prepared is not { } prepared) return;
            Change(snapshot => snapshot with { Phase = "verifying", CanDownload = false });
            await ValidatePrepared(prepared, candidate.Metadata, token);
            lock (_gate) _prepared = prepared;
            Change(snapshot => snapshot with { Phase = "ready", BytesDownloaded = candidate.Metadata.Size, CanInstall = true, CanDownload = false });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            lock (_gate) _prepared = null;
            Change(snapshot => snapshot with { Phase = "error", Error = "The previous update cache could not be verified.", CanInstall = false, CanDownload = _candidate is not null });
        }
    }

    private async Task ValidatePrepared(PreparedUpdate prepared, UpdateMetadata metadata, CancellationToken token)
    {
        var packageParent = Path.GetDirectoryName(prepared.PackagePath);
        var packagesRoot = UpdatePackage.ChildPath(CacheRoot, "packages");
        if (prepared.Version != metadata.Version || prepared.CacheRoot != CacheRoot || prepared.PackageSha256 != metadata.Sha256 ||
            prepared.FileManifestSha256 != metadata.FileManifestSha256 || prepared.Requirements != metadata.Requirements ||
            packageParent is null || Path.GetDirectoryName(packageParent) != packagesRoot ||
            !Guid.TryParseExact(Path.GetFileName(packageParent), "N", out _) ||
            prepared.PackagePath != Path.Combine(packageParent, metadata.AssetName) || prepared.StageRoot != Path.Combine(packageParent, "stage"))
            throw new InvalidDataException("Unsafe cached update paths.");
        UpdatePackage.EnsureSafePath(prepared.PackagePath);
        if (new FileInfo(prepared.PackagePath).Length != metadata.Size) throw new InvalidDataException("Cached ZIP size mismatch.");
        await UpdatePackage.VerifyHash(prepared.PackagePath, metadata.Sha256, token);
        await UpdatePackage.VerifyStage(prepared.StageRoot, metadata, token);
    }

    private void SafeDeleteDirectory(string directory)
    {
        try
        {
            var packagesRoot = UpdatePackage.ChildPath(CacheRoot, "packages");
            if (Path.GetDirectoryName(directory) != packagesRoot || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !Directory.Exists(directory)) return;
            UpdatePackage.EnsureSafePath(directory);
            // Refuse to recursively delete a directory containing a link, even after a failed extraction.
            var pending = new Stack<string>(); pending.Push(directory);
            while (pending.TryPop(out var item))
            {
                UpdatePackage.EnsureSafePath(item);
                foreach (var child in Directory.EnumerateFileSystemEntries(item))
                { UpdatePackage.EnsureSafePath(child); if (Directory.Exists(child)) pending.Push(child); }
            }
            Directory.Delete(directory, true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string SafeError(Exception error) => error switch
    {
        InvalidDataException => error.Message,
        HttpRequestException => error.Message,
        OperationCanceledException => "The update request timed out.",
        _ => "The update operation failed. Check disk space, permissions and connectivity.",
    };

    public async ValueTask DisposeAsync()
    {
        Task? operation;
        Task? scheduler;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _operationCancellation?.Cancel();
            operation = _operation;
            scheduler = _scheduler;
        }
        if (operation is not null) await operation;
        if (scheduler is not null) await scheduler;
        _operationCancellation?.Dispose();
        _http.Dispose();
        _lifetime.Dispose();
        _persistGate.Dispose();
    }
}
