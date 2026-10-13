using AFMediaBar.Classes.Settings;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 可变柱数频谱的频段划分。频段按对数均匀分布，因此柱数变化时低频分辨率始终高于高频，观感与固定九段时一致。
/// Band division for a variable-count spectrum. Bands are distributed logarithmically, so low frequencies always keep a
/// higher resolution than high ones and the appearance matches the previous fixed nine-band layout.
/// </summary>
public static class SpectrumBandPolicy
{
    /// <summary>最低频段下界（Hz）。 / Lower edge of the first band in hertz.</summary>
    public const float FirstBandEdgeHz = 45f;

    /// <summary>最高频段上界（Hz）。 / Upper edge of the last band in hertz.</summary>
    public const float LastBandEdgeHz = 20000f;

    /// <summary>把柱数夹取到持久化区间。 / Clamps a bar count to the persisted range.</summary>
    /// <param name="bandCount">请求的柱数。/ Requested bar count.</param>
    public static int ClampBandCount(int bandCount) =>
        Math.Clamp(bandCount, SpectrumComponentSettings.MinimumBandCount, SpectrumComponentSettings.MaximumBandCount);

    /// <summary>
    /// 生成 count + 1 个频段边界。首尾边界固定为 45 Hz 与 20 kHz，中间按等比数列展开，
    /// 因此相邻频段宽度之比恒定，柱数越多高频越细、低频仍然分得开。
    /// Builds count + 1 band edges. The outer edges stay at 45 Hz and 20 kHz while the interior edges follow a geometric
    /// progression, so adjacent bands always share one width ratio: more bars means finer high frequencies without losing
    /// low-frequency separation.
    /// </summary>
    /// <param name="bandCount">柱数；越界时先被夹取。/ Bar count; clamped to the persisted range first.</param>
    /// <returns>长度为柱数加一的边界数组。/ An edge array with bar count plus one entries.</returns>
    public static float[] CreateBandEdges(int bandCount)
    {
        var count = ClampBandCount(bandCount);
        var edges = new float[count + 1];
        var ratio = Math.Pow(LastBandEdgeHz / (double)FirstBandEdgeHz, 1d / count);
        for (var index = 0; index <= count; index++)
        {
            edges[index] = (float)(FirstBandEdgeHz * Math.Pow(ratio, index));
        }

        // 等比数列的浮点累积误差会让首尾偏离设定值，这里直接写回端点，保证 Nyquist 夹取结果与柱数无关地稳定。
        // Floating-point accumulation in the geometric progression drifts at both ends, so the endpoints are written back
        // to keep the Nyquist clamping independent of the band count.
        edges[0] = FirstBandEdgeHz;
        edges[count] = LastBandEdgeHz;
        return edges;
    }
}
