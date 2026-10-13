// 后台读取边界：发现网易云进程并独占其读取器；调用者串行调用 Read，并在后台 Dispose。
using System.Diagnostics;
using AFMediaBar.Classes.Interop;
using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Services.Media.Sources.NetEase;

/// <summary>一次网易云进程读取，允许独立测试取消和资源释放而不访问真实进程。</summary>
internal interface INetEaseMemoryReader : IDisposable
{
    (PlayerInfo? Info, int ProcessId) Read(string? expectedTitle);
}

/// <summary>在窗口所属进程变化或退出时替换并释放网易云读取器。</summary>
internal sealed class NetEaseMemoryReader : INetEaseMemoryReader
{
    private NetEase? _reader;

    public (PlayerInfo? Info, int ProcessId) Read(string? expectedTitle)
    {
        var processId = 0;
        try
        {
            var hwnd = NativeMethods.FindWindow("OrpheusBrowserHost", null);
            if (hwnd != IntPtr.Zero)
                NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            if (processId <= 0)
            {
                Dispose();
                return (null, 0);
            }
            if (_reader is null || !_reader.Validate(processId))
            {
                Dispose();
                _reader = new NetEase(processId);
            }
            return (_reader.GetPlayerInfo(expectedTitle), processId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NetEaseMemoryReader] Read failed: {ex.Message}");
            Dispose();
            return (null, processId);
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
    }
}
