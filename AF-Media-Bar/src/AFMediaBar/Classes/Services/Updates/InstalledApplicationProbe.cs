using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace AFMediaBar.Classes.Services.Updates;

/// <summary>
/// 本机安装信息。
/// Local installation information.
/// </summary>
/// <param name="IsInstalled">是否存在由安装程序写入的安装记录。/ Whether an installation record written by the installer exists.</param>
/// <param name="IsMachineWide">该记录是否在 HKLM（为所有用户安装）。/ Whether the record lives under HKLM, meaning an all-users installation.</param>
/// <param name="InstallLocation">安装目录。/ Install directory.</param>
/// <param name="DisplayVersion">安装记录里的版本号。/ Version recorded by the installer.</param>
/// <param name="Publisher">发布者。/ Publisher.</param>
public sealed record InstalledApplicationInfo(
    bool IsInstalled,
    bool IsMachineWide,
    string? InstallLocation,
    string? DisplayVersion,
    string? Publisher)
{
    /// <summary>没有安装记录的取值。/ Value used when no installation record exists.</summary>
    public static InstalledApplicationInfo NotInstalled { get; } = new(false, false, null, null, null);
}

/// <summary>
/// 读取安装程序留下的安装记录，并判断当前进程是否来自该安装目录。
///
/// 便携版和安装版跑的是同一个可执行文件，唯一可靠的区别就是这条注册表记录：没有它就没有"原目录"，
/// 因此更新必须降级为手动下载，而不是尝试替换一个不属于安装程序管理的文件。
/// Reads the installation record left by the installer and decides whether the running process lives inside that
/// directory.
///
/// The portable build and the installed build are the same executable, and this registry record is the only
/// reliable difference: without it there is no "original directory", so the update must fall back to manual
/// download instead of replacing a file the installer never managed.
/// </summary>
public sealed class InstalledApplicationProbe
{
    /// <summary>
    /// 安装程序的应用标识，必须与 <c>installer/AFMediaBar.iss</c> 的 <c>AppId</c> 完全一致。
    /// 不一致时程序会把自己当成便携版，自动更新会静默退化为手动下载。
    /// The installer's application identifier, which must match <c>AppId</c> in <c>installer/AFMediaBar.iss</c>
    /// exactly. A mismatch makes the application treat itself as portable, silently degrading automatic updates to
    /// manual downloads.
    /// </summary>
    public const string ApplicationId = "{7C1B9E4C-5B2A-4E7E-9F1D-3A6C2E8B45D1}";

    /// <summary>Inno Setup 为卸载项添加的后缀。/ Suffix Inno Setup appends to uninstall entries.</summary>
    public const string UninstallKeySuffix = "_is1";

    /// <summary>本程序的进程名（不含扩展名），只用于统计其它正在运行的实例。/ Process name without extension, used only to count other running instances.</summary>
    public const string ProcessName = "AFMediaBar";

    private const string UninstallKeyPrefix = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// <summary>
    /// 读取安装记录。读取失败（权限、损坏的注册表）一律按"未安装"处理：宁可让用户手动更新，也不要在这里抛异常。
    /// Reads the installation record. A read failure (permissions, damaged registry) is treated as "not installed":
    /// asking the user to update manually is preferable to throwing here.
    /// </summary>
    public InstalledApplicationInfo Probe()
    {
        var machineWide = ReadRecord(Registry.LocalMachine, isMachineWide: true);
        if (machineWide.IsInstalled)
        {
            return machineWide;
        }

        return ReadRecord(Registry.CurrentUser, isMachineWide: false);
    }

    /// <summary>
    /// 当前进程是否来自安装记录里的目录。
    /// Whether the running process comes from the directory named by the installation record.
    /// </summary>
    /// <param name="info">安装信息。/ Installation information.</param>
    /// <param name="processPath">当前进程的可执行文件路径；为 null 时按不匹配处理。/ Executable path of the running process, or null when unknown.</param>
    public static bool MatchesRunningProcess(InstalledApplicationInfo info, string? processPath)
    {
        if (!info.IsInstalled || string.IsNullOrWhiteSpace(info.InstallLocation) || string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var installed = Normalize(info.InstallLocation);
        var running = Normalize(Path.GetDirectoryName(processPath) ?? string.Empty);
        return installed.Length > 0 && string.Equals(installed, running, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 统计其它正在运行的本程序实例数量（不含当前进程）。
    ///
    /// 只在准备安装之前调用：有其它实例时推迟安装，避免安装程序关闭它们时把用户正在用的实例一起带走。
    /// Counts the other running instances of this application, excluding the current process.
    ///
    /// Only called right before installing: another instance defers the install, so the installer closing them does
    /// not take away an instance the user is still using.
    /// </summary>
    /// <param name="currentProcessId">当前进程编号。/ Current process id.</param>
    public static int CountOtherRunningInstances(int currentProcessId)
    {
        try
        {
            var count = 0;
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    if (process.Id != currentProcessId)
                    {
                        count++;
                    }
                }
            }

            return count;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Update] Could not enumerate running instances: {exception.Message}");
            return 0;
        }
    }

    private static InstalledApplicationInfo ReadRecord(RegistryKey root, bool isMachineWide)
    {
        try
        {
            using var key = root.OpenSubKey(UninstallKeyPrefix + ApplicationId + UninstallKeySuffix);
            if (key is null)
            {
                return InstalledApplicationInfo.NotInstalled;
            }

            var location = key.GetValue("InstallLocation") as string;
            return new InstalledApplicationInfo(
                true,
                isMachineWide,
                location,
                key.GetValue("DisplayVersion") as string,
                key.GetValue("Publisher") as string);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            Debug.WriteLine($"[Update] Could not read the installation record: {exception.Message}");
            return InstalledApplicationInfo.NotInstalled;
        }
    }

    private static string Normalize(string path) => path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
