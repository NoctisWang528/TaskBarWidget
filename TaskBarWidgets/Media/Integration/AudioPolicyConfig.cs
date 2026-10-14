// Adapted from AF-Media-Bar/Classes/Interop/AudioPolicyConfig.cs (MIT).
// The copied source and upstream license are recorded in THIRD-PARTY-NOTICES.md.
using System.Runtime.InteropServices;

namespace TaskBarWidgets.Media.Integration;

internal static class AudioPolicyConfig
{
    internal static IReadOnlyDictionary<AudioRole, string?> SetDefaultEndpoint(string id, bool communications)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        object? client = null;
        var errors = new Dictionary<AudioRole, string?>();
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), true)!;
            client = Activator.CreateInstance(type) ?? throw new InvalidOperationException("音频策略服务不可用");
            var policy = (IPolicyConfig)client;
            foreach (var role in AudioRolePolicy.GetRoles(communications))
            {
                try
                {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
                    errors[role] = null;
                }
                catch (COMException exception) { errors[role] = exception.Message; }
            }
        }
        finally
        {
            if (client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client);
        }
        return errors;
    }

    // Preserve the full Windows PolicyConfig ABI vtable, including unused slots.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, out nint format);
        [PreserveSig] int GetDeviceFormat(string id, int defaultFormat, out nint format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, int defaultPeriod, out long period, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string id, ref long period);
        [PreserveSig] int GetShareMode(string id, out nint mode);
        [PreserveSig] int SetShareMode(string id, nint mode);
        [PreserveSig] int GetPropertyValue(string id, int store, nint key, out nint value);
        [PreserveSig] int SetPropertyValue(string id, int store, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, AudioRole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
