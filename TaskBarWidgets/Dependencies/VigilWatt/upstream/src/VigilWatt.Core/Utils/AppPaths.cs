namespace VigilWatt.Core.Utils;

/// <summary>
/// 功 能： 统一管理应用数据根目录。
/// 2026/10/06 15:53:36 xliu
/// 三级取值，优先级从高到低：
/// 1. 环境变量 <see cref="DataDirVariable"/>——显式指定，调试/隔离时用；
/// 2. **Debug 构建：exe 旁边的 <c>data\</c>**——.NET 开发的常规做法，不污染 %LOCALAPPDATA%，
///    开发版与正式版各用一份 settings/history/logs，互不覆盖；
/// 3. Release 构建：<c>%LOCALAPPDATA%\VigilWatt</c>——安装版的正式位置。
/// </summary>
public static class AppPaths
{
    public const string AppName = "VigilWatt";

    /// <summary>覆盖数据根目录的环境变量。优先级最高，用于临时把数据指到别处。</summary>
    public const string DataDirVariable = "VIGILWATT_DATA_DIR";

    public static readonly string RootDir = ResolveRootDir();

    /// <summary>非法环境变量（含非法字符等）当作没设，继续往下走。**这里不打日志**：
    /// Log 自己依赖 AppPaths，在静态初始化里调 Log 会绕成环。想确认实际路径看启动日志里的
    /// `history.db 就绪: ...` 即可。</summary>
    private static string ResolveRootDir()
    {
        var custom = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try { return Path.GetFullPath(custom.Trim()); }
            catch (Exception) { /* 退回下一级 */ }
        }

#if DEBUG
        return Path.Combine(AppContext.BaseDirectory, "data");
#else
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
#endif
    }
}
