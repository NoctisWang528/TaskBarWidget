using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DrawingIcon = System.Drawing.Icon;

namespace AFMediaBar.Classes.Services.Audio;

/// <summary>
/// 从音频会话或进程可执行文件读取应用图标，并按来源路径缓存原始图像字节。
/// Reads application icons from audio sessions or process executables and caches raw image bytes by source path.
/// </summary>
public sealed class ApplicationIconService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly AudioProcessInfoService _processInfo;

    /// <summary>
    /// 创建应用图标加载器，并复用进程信息服务解析可执行文件路径。
    /// Creates the application-icon loader and reuses process information to resolve executable paths.
    /// </summary>
    public ApplicationIconService(AudioProcessInfoService processInfo)
    {
        _processInfo = processInfo;
    }

    /// <summary>按会话图标路径和进程路径依次读取图标。 / Reads an icon from the session path and then the process path.</summary>
    public byte[]? GetIconData(uint processId, string? sessionIconPath)
    {
        var candidates = (processId == 0
                ? new[]
                {
                    Path.Combine(Environment.SystemDirectory, "SndVol.exe"),
                    NormalizeIconPath(sessionIconPath)
                }
                : new[]
                {
                    NormalizeIconPath(sessionIconPath),
                    _processInfo.GetExecutablePath(processId)
                })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(candidate!, out var cached))
                {
                    return cached;
                }
            }

            var icon = ReadIcon(candidate!);
            if (icon is null)
            {
                continue;
            }

            lock (_gate)
            {
                _cache[candidate!] = icon;
            }
            return icon;
        }

        return null;
    }

    private static byte[]? ReadIcon(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var extension = Path.GetExtension(path);
            // 位图文件本身就是像素正确的图像，原样交给 WPF 解码。
            // A bitmap file already carries the correct pixels, so it goes to WPF as it is.
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return File.ReadAllBytes(path);
            }

            // .ico 与 .exe/.dll 都走图标路径：`Icon.Save` 写出的是带 AND 掩码的 DIB，WPF 读它时颜色与透明度都不可靠
            // （图标发灰、带色偏），因此先按掩码合成到 32bpp ARGB，再编码成 PNG。
            // Both .ico and .exe/.dll go through the icon path: `Icon.Save` writes a DIB with an AND mask whose colours and transparency WPF
            // does not decode reliably (icons come out grey and colour-cast), so the icon is composited onto 32bpp ARGB and encoded as PNG.
            using var icon = extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? new DrawingIcon(path, new System.Drawing.Size(32, 32))
                : DrawingIcon.ExtractAssociatedIcon(path);
            return icon is null ? null : EncodeIcon(icon);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按图标自带的透明掩码合成到 32bpp ARGB 位图上并编码为 PNG：这是唯一能让 WPF 拿到正确颜色与透明度的路径。
    /// Composites an icon onto a 32bpp ARGB bitmap using its own transparency mask and encodes it as PNG: this is the only path that gives
    /// WPF the correct colours and transparency.
    /// </summary>
    /// <param name="icon">已解码的图标。/ The decoded icon.</param>
    private static byte[] EncodeIcon(DrawingIcon icon)
    {
        var width = icon.Width > 0 ? icon.Width : 32;
        var height = icon.Height > 0 ? icon.Height : 32;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawIcon(icon, new Rectangle(0, 0, width, height));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static string? NormalizeIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = Environment.ExpandEnvironmentVariables(value.Trim().TrimStart('@').Trim('"'));
        var resourceSeparator = path.LastIndexOf(',');
        if (resourceSeparator > 0 && int.TryParse(path[(resourceSeparator + 1)..], out _))
        {
            path = path[..resourceSeparator];
        }

        return path.Trim('"');
    }
}
