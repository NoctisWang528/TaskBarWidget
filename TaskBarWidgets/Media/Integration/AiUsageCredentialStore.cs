using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TaskBarWidgets.Media.Integration;

// Only current-user DPAPI ciphertext is stored. Keys never enter ordinary settings.
internal sealed class AiUsageCredentialStore(string directory)
{
    internal bool Exists(string id) => File.Exists(PathFor(id));
    private string PathFor(string id)
    {
        if (!AiUsageHub.Catalog.Any(provider => provider.Id == id && provider.Mode == "api"))
            throw new ArgumentException("无效的 API 服务。");
        var path = Path.Combine(directory, "ai-" + id + ".bin");
        EnsureRegular(path);
        return path;
    }
    private static void EnsureRegular(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("凭据路径不允许链接。");
    }
    internal string? Read(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 256 * 1024) throw new CryptographicException();
        var plain = Transform(File.ReadAllBytes(path), false);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal void Write(string id, string? credential)
    {
        var path = PathFor(id);
        if (credential is null) { if (File.Exists(path)) File.Delete(path); return; }
        var plain = Encoding.UTF8.GetBytes(credential);
        byte[] cipher;
        try { cipher = Transform(plain, true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(directory);
        EnsureRegular(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, cipher); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Transform(byte[] input, bool protect)
    {
        var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        Blob output = default;
        try
        {
            var blob = new Blob { Length = input.Length, Data = pin.AddrOfPinnedObject() };
            var ok = protect ? CryptProtectData(ref blob, "TaskBarWidgets AI credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("无法保护或解密当前用户的凭据。");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (!protect) Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
            pin.Free();
        }
    }
}
