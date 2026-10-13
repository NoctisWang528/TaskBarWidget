using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using AFMediaBar.Classes.Utils;

namespace AFMediaBar.Classes.Services.Credits;

/// <summary>
/// 贡献者头像的加载与缓存：内存 LRU（最近用过的几十张）+ 磁盘缓存（<c>%LOCALAPPDATA%\AFMediaBar\cache\avatars</c>）。
/// Loading and caching of contributor avatars: an in-memory LRU for the last few dozen plus a disk cache under
/// <c>%LOCALAPPDATA%\AFMediaBar\cache\avatars</c>.
///
/// 为什么要有磁盘缓存：名单本身缓存 24 小时，头像若每次都联网，用户每进一次「关于」页就要下载几十张图；
/// 而头像是按 URL（其中含 GitHub 的用户 id）命名的，几乎不会变。磁盘缓存条目没有过期时间，也不做数量上限——
/// 每张只有几十 KB，而"头像突然全白"比多占几兆更让人困惑。
/// Why a disk cache: the lists are cached for 24 hours, and without one every visit to the about page would download dozens of images while avatars are
/// addressed by URL — which contains the GitHub user id — and therefore almost never change. Disk entries carry no expiry and no count limit: each is a few
/// tens of kilobytes, and "every avatar suddenly blank" is far more confusing than a few megabytes on disk.
///
/// 解码后的位图一律 <c>Freeze()</c>：冻结对象可以跨线程使用，因此后台线程解码、UI 线程直接绑定，不需要再拷贝一次。
/// Every decoded bitmap is <c>Freeze()</c>d: a frozen object may be used across threads, so a background thread decodes and the UI thread binds it directly
/// without another copy.
/// </summary>
public sealed class AvatarImageLoader : IDisposable
{
    /// <summary>内存里保留的头像张数；名单上限 40，留一倍余量。/ How many avatars are kept in memory: the list is capped at 40, so this leaves room to spare.</summary>
    private const int MemoryCacheCapacity = 80;

    /// <summary>并发下载数上限：几十张图同时打过去只会互相拖慢。/ Upper bound on concurrent downloads: dozens of requests at once only slow each other down.</summary>
    private const int MaximumConcurrentDownloads = 4;

    private readonly string _directoryPath;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly LruCache<string, BitmapImage?> _memory = new(MemoryCacheCapacity);
    private readonly SemaphoreSlim _concurrency = new(MaximumConcurrentDownloads, MaximumConcurrentDownloads);
    private bool _disposed;

    /// <summary>
    /// 创建头像加载器。
    /// Creates the avatar loader.
    /// </summary>
    /// <param name="directoryPath">磁盘缓存目录；省略时取 <c>%LOCALAPPDATA%\AFMediaBar\cache\avatars</c>。/ Disk cache directory, defaulting to <c>%LOCALAPPDATA%\AFMediaBar\cache\avatars</c>.</param>
    public AvatarImageLoader(string? directoryPath = null)
    {
        _directoryPath = directoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AFMediaBar",
            "cache",
            "avatars");
    }

    /// <summary>
    /// 取一张头像；失败一律返回 null，绝不抛出（头像只是装饰）。
    /// Gets one avatar, always returning null on failure and never throwing: an avatar is decoration only.
    /// </summary>
    /// <param name="avatarUrl">头像地址。/ The avatar address.</param>
    /// <param name="cancellationToken">取消标记。/ Cancellation token.</param>
    /// <returns>冻结的位图，或 null。/ A frozen bitmap, or null.</returns>
    public async Task<BitmapImage?> LoadAsync(string? avatarUrl, CancellationToken cancellationToken)
    {
        var requestUrl = CreditsPresentationPolicy.ResolveAvatarRequestUrl(avatarUrl);
        if (_disposed || requestUrl.Length == 0)
        {
            return null;
        }

        if (_memory.TryGetValue(requestUrl, out var cached))
        {
            return cached;
        }

        var cacheFile = Path.Combine(_directoryPath, CreditsPresentationPolicy.ResolveCacheFileName(requestUrl) + ".img");

        var fromDisk = await Task.Run(() => TryReadFromDisk(cacheFile), cancellationToken).ConfigureAwait(false);
        if (fromDisk is not null)
        {
            _memory.Set(requestUrl, fromDisk);
            return fromDisk;
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await DownloadAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                // 暂时失败短期去重；网络恢复后再次打开名单仍能重试。
                _memory.Set(requestUrl, null, TimeSpan.FromSeconds(30));
                return null;
            }

            var image = Decode(bytes);
            _memory.Set(requestUrl, image, image is null ? TimeSpan.FromSeconds(30) : null);
            if (image is not null)
            {
                TryWriteToDisk(cacheFile, bytes);
            }

            return image;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _concurrency.Dispose();
        _httpClient.Dispose();
    }

    private static BitmapImage? TryReadFromDisk(string path)
    {
        try
        {
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Credits] Avatar cache unreadable: {exception.Message}");
            return null;
        }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // DecodePixelWidth 让解码时就缩到列表需要的大小：原图可能是 460×460，按原尺寸留着只是白占内存。
            // DecodePixelWidth scales during decoding to what the list needs: the source can be 460×460, and keeping it at that size only wastes memory.
            image.DecodePixelWidth = CreditsPresentationPolicy.AvatarPixelSize;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or ArgumentException)
        {
            // 内容不是图片（例如代理返回了一段 HTML）时只记一行，不让它变成异常冒到界面。
            // Content that is not an image, such as an HTML page from a proxy, only gets a debug line instead of an exception reaching the interface.
            Debug.WriteLine($"[Credits] Avatar decode failed: {exception.Message}");
            return null;
        }
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AFMediaBar-Credits");
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            Debug.WriteLine($"[Credits] Avatar download failed ({url}): {exception.Message}");
            return null;
        }
    }

    private void TryWriteToDisk(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_directoryPath);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 写不进磁盘只是下次还要再下一次，不影响本次显示。
            // A failed disk write only means the next run downloads again; this display is unaffected.
            Debug.WriteLine($"[Credits] Avatar cache write failed: {exception.Message}");
        }
    }
}
