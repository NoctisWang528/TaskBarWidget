using System.Text;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： JSONL 追加写入器（records / events / errors 各一个实例）。
/// 2026/10/24 12:40:43 20211
/// 采集线程只调 <see cref="Append"/>（加锁），不直接碰文件；Dispose 时 flush 并关闭。
/// </summary>
public sealed class CaptureJsonlWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public CaptureJsonlWriter(string path)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
    }

    public int Count { get; private set; }

    public void Append(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
            Count++;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception ex)
            {
                Utils.Log.Warn($"采集 JSONL 关闭失败: {ex.Message}");
            }
        }
    }
}
