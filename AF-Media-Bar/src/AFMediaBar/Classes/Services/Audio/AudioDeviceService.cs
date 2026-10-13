// Enumerates render endpoints through NAudio; each operation owns and disposes its native wrappers.
using System.Runtime.InteropServices;
using AFMediaBar.Classes.Interop;
using AFMediaBar.Classes.Models;
using NAudio.CoreAudioApi;

namespace AFMediaBar.Classes.Services.Audio;

/// <summary>
/// 枚举输出设备，并切换默认 Console/Multimedia 端点；设备显示名保持系统原文。
/// Enumerates render devices and switches Console/Multimedia defaults, preserving system device names.
/// </summary>
public sealed class AudioDeviceService
{
    /// <summary>在后台枚举活动端点并返回 UI 快照。 / Enumerates active endpoints off-thread into UI snapshots.</summary>
    public Task<IReadOnlyList<AudioDeviceOption>> GetRenderDevicesAsync() => Task.Run<IReadOnlyList<AudioDeviceOption>>(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = GetDefaultId(enumerator);
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var options = new List<AudioDeviceOption>();
        for (var index = 0; index < devices.Count; index++)
        {
            try
            {
                using var device = devices[index];
                var id = device.ID;
                var name = device.FriendlyName;
                // Keep the existing WinRT ID contract used by spatial-audio configuration.
                var interfaceId = @"\\?\SWD#MMDEVAPI#" + id + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
                options.Add(new AudioDeviceOption(interfaceId, id,
                    string.IsNullOrWhiteSpace(name) ? id : name,
                    string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
            catch (COMException)
            {
                // A device may be unplugged between enumeration and reading its properties.
            }
        }
        return options.OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    });

    /// <summary>读取系统默认设备；调用者应在后台执行。 / Reads the system default; callers must run off-thread.</summary>
    public bool IsDefaultRenderDevice(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        return string.Equals(GetDefaultId(enumerator), GetPolicyDeviceId(deviceId), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>切换非通信角色的系统默认端点。 / Changes system defaults for the non-communications roles.</summary>
    public void SetDefaultRenderDevice(string policyDeviceId) => AudioPolicyConfig.SetDefaultRenderDevice(policyDeviceId);

    private static string? GetDefaultId(MMDeviceEnumerator enumerator)
    {
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
            return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        return device.ID;
    }

    private static string GetPolicyDeviceId(string deviceInformationId)
    {
        const string marker = "MMDEVAPI#";
        var start = deviceInformationId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return deviceInformationId;
        }

        start += marker.Length;
        var end = deviceInformationId.IndexOf("#{", start, StringComparison.OrdinalIgnoreCase);
        return end > start ? deviceInformationId[start..end] : deviceInformationId[start..];
    }

}
