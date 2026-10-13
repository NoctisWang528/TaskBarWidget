using System.Windows;
using System.Windows.Media;
using AFMediaBar.Classes.Interop;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 在后台从最终桌面合成结果中稀疏采样屏幕颜色。
/// Sparsely samples screen colors from the final desktop composition on a background thread.
/// </summary>
public sealed class ScreenBackgroundSampler
{
    private const int HorizontalSamples = 11;
    private const int VerticalSamples = 5;

    /// <summary>异步采样指定物理像素矩形。/ Asynchronously samples the specified physical-pixel rectangle.</summary>
    public Task<IReadOnlyList<Color>> SampleAsync(Int32Rect bounds, CancellationToken cancellationToken) =>
        // 取消是采样会话的正常代际切换，不应通过 Task.Run 的取消重载制造
        // OperationCanceledException 首次机会异常；采样循环会返回已收集的结果，
        // 上层再按代际丢弃它。
        // Cancellation is a normal generation switch, not an exceptional sampling failure.
        // Avoid Task.Run's cancellation overload and let the loop return partial samples;
        // the owning session discards stale generations afterwards.
        Task.Run(() => Sample(bounds, cancellationToken));

    private static IReadOnlyList<Color> Sample(Int32Rect requestedBounds, CancellationToken cancellationToken)
    {
        if (requestedBounds.Width <= 0 || requestedBounds.Height <= 0)
            return [];

        var virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var virtualWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var virtualHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (virtualWidth <= 0 || virtualHeight <= 0)
            return [];

        var left = Math.Max(requestedBounds.X, virtualLeft);
        var top = Math.Max(requestedBounds.Y, virtualTop);
        var right = Math.Min((long)requestedBounds.X + requestedBounds.Width, (long)virtualLeft + virtualWidth);
        var bottom = Math.Min((long)requestedBounds.Y + requestedBounds.Height, (long)virtualTop + virtualHeight);
        if (right <= left || bottom <= top)
            return [];

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return [];

        try
        {
            var colors = new List<Color>(HorizontalSamples * VerticalSamples);
            for (var row = 0; row < VerticalSamples; row++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return colors;
                var y = top + (int)(((2L * row + 1) * (bottom - top)) / (2L * VerticalSamples));
                for (var column = 0; column < HorizontalSamples; column++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return colors;
                    var x = left + (int)(((2L * column + 1) * (right - left)) / (2L * HorizontalSamples));
                    var colorRef = NativeMethods.GetPixel(screenDc, x, y);
                    if (colorRef == NativeMethods.CLR_INVALID)
                        continue;

                    colors.Add(Color.FromRgb(
                        (byte)(colorRef & 0xFF),
                        (byte)((colorRef >> 8) & 0xFF),
                        (byte)((colorRef >> 16) & 0xFF)));
                }
            }

            return colors;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
