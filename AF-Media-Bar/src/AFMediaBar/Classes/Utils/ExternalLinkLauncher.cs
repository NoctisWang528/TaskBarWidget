using System.Diagnostics;

namespace AFMediaBar.Classes.Utils;

/// <summary>
/// 用系统默认程序打开一个链接或本地路径。
/// Opens a link or a local path with the system default program.
///
/// 只接受 http/https：界面上的链接全部来自固定清单或仓库名单文件，一旦那份文件被替换，允许任意协议就意味着
/// 一次点击可以启动任意本机程序。因此 <c>file:</c>、<c>ms-settings:</c> 之类的协议在这里一律被拒绝，
/// 需要它们的调用方自己调用 <see cref="Process.Start(ProcessStartInfo)"/>（现有几处正是这么做的）。
/// Only http and https are accepted: every link on the interface comes from a fixed catalog or from the repository's list file, and if
/// that file were ever replaced, allowing arbitrary schemes would mean one click can start any local program. Schemes such as
/// <c>file:</c> or <c>ms-settings:</c> are therefore refused here, and callers that need them call
/// <see cref="Process.Start(ProcessStartInfo)"/> themselves, which is what the existing call sites already do.
/// </summary>
public static class ExternalLinkLauncher
{
    /// <summary>
    /// 尝试打开一个 http/https 链接；地址为空、协议不符或启动失败时返回 false，绝不抛出。
    /// Tries to open an http/https link, returning false when the address is empty, the scheme is not allowed, or the start fails; it
    /// never throws.
    /// </summary>
    /// <param name="url">链接地址。/ The link address.</param>
    /// <returns>是否已交给系统打开。/ Whether the address was handed to the system.</returns>
    public static bool TryOpen(string? url)
    {
        if (!IsOpenable(url))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(url!.Trim()) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception)
        {
            // 打开浏览器失败不是程序错误：没有默认浏览器、关联损坏、被策略拦截都会走到这里，记一行调试输出即可。
            // Failing to open a browser is not a program error — no default browser, a broken association, or a policy block all land
            // here — and one debug line is enough.
            Debug.WriteLine($"[Link] Cannot open {url}: {exception.Message}");
            return false;
        }
    }

    /// <summary>该地址是否是一个可以交给系统打开的 http/https 链接。/ Whether the address is an http/https link that may be handed to the system.</summary>
    /// <param name="url">链接地址。/ The link address.</param>
    /// <returns>可打开时为 true。/ True when it can be opened.</returns>
    public static bool IsOpenable(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
