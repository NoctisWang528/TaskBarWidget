namespace VigilWatt.Core.Plugins.Mchose;

/// <summary>
/// 功 能： V9 Pro 耳机固定电量查询的报文构造与解析。
/// 2026/10/19 20:45:00 xliu
/// 协议与验证范围见 docs/hardware/mchose-v9-pro.md。
/// </summary>
public static class MchoseV9ProCodec
{
    /// <summary>耳机接收器的厂 ID。与键盘/鼠标接收器的 0x3837 / 0x41E4 不同号段，插件按 VID 分流。</summary>
    public const ushort VendorId = 0x291D;

    /// <summary>V9 Pro 接收器的产品 ID。</summary>
    public const ushort ProductId = 0x385D;

    /// <summary>输入/输出报告长度，**已含 report ID，不加前置零**。</summary>
    public const int ReportLength = 64;

    /// <summary>查询与响应共用的 report ID。</summary>
    public const byte ReportId = 0x55;

    /// <summary>响应里的命令回显字节（与查询帧 `[1]` 相同）。</summary>
    private const byte CommandEcho = 0x65;

    /// <summary>查询帧的固定形态，供 <see cref="TryParse"/> 识别回显。</summary>
    private static readonly byte[] Query = BuildQuery();

    /// <summary>
    /// 电量集合的接口判据。同接收器上还有别的 collection，全部不认。
    /// </summary>
    /// <param name="path">设备路径。贡献者机器上电量集合固定是 `mi_00` 的第 5 个 collection。</param>
    /// <param name="featureLength">feature 报告长度：电量集合是 0，有 feature 通道说明是另一个集合。</param>
    public static bool IsSupportedInterface(ushort vendorId, ushort productId, string path,
        ushort usagePage, ushort usage, ushort inputLength, ushort outputLength,
        ushort featureLength, byte? reportId)
        => vendorId == VendorId && productId == ProductId
           && path.Contains("&mi_00&col05#", StringComparison.OrdinalIgnoreCase)
           && usagePage == 0xFF90 && usage == 0x0001
           && inputLength == ReportLength && outputLength == ReportLength
           && featureLength == 0 && reportId == ReportId;

    /// <summary>64 字节查询帧：`55 65 01`，余下 61 字节为 0。</summary>
    public static byte[] CreateQuery() => (byte[])Query.Clone();

    /// <summary>
    /// 解析一条 input report。成功返回电量（1–100），失败返回 false。
    /// 0 与 &gt;100 在协议层就判无效：耳机休眠/关机时接收器会回 0，与"真的 0%"无法区分。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> report, out int percent)
    {
        percent = 0;
        if (report.Length != ReportLength || report[0] != ReportId
            || report[1] != CommandEcho || report[2] is 0 or > 100)
            return false;

        // 有的接收器会把主机写入回显到输入队列：我们自己的查询帧（55 65 01 …）正好能解析成 1%。
        // 整帧等于查询帧时丢弃。代价是"真 1% 且标志字节恰好为 0"的响应也会被丢，见 mchose-v9-pro.md §5。
        if (report.SequenceEqual(Query)) return false;

        percent = report[2];
        return true;
    }

    private static byte[] BuildQuery()
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = CommandEcho;
        report[2] = 0x01;
        return report;
    }
}
