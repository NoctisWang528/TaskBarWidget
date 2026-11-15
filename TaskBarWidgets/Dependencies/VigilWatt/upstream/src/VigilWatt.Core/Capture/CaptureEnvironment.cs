using System.Security.Principal;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 采集环境描述（系统、后端、当前进程权限）。报告与预检共用，避免两处写法不一致。
/// 2026/10/24 12:40:43 20211
/// </summary>
public static class CaptureEnvironment
{
    /// <summary>本采集只用用户态 Win32 API，不装驱动、不申请提权。</summary>
    public const string Backend = "Win32 hid.dll + CfgMgr32（用户态，无驱动）";

    public static string OsVersion
    {
        get
        {
            var version = Environment.OSVersion.Version;
            var name = version.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} {version}";
        }
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Utils.Log.Debug($"读取进程权限失败: {ex.Message}");
                return false;
            }
        }
    }

    public static string PermissionLabel => IsElevated ? "管理员（高完整性）" : "标准用户（Medium 完整性）";

    /// <summary>常见安装位置里找 USBPcapCMD.exe；只检测，不安装、不调用。</summary>
    public static string? FindUsbPcapCommand()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBPcap", "USBPcapCMD.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wireshark", "extcap", "USBPcapCMD.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBPcap", "USBPcapCMD.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
