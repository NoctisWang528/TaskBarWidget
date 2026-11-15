using System.Text;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 一次采集会话：状态机、记录/事件/错误汇聚、落盘与收尾出包。停止幂等，失败也保留证据；只有用户主动「放弃」才删目录。
/// 2026/10/24 12:40:43 20211
/// 采集在后台线程跑，UI 只取快照；所有共享集合都在 _gate 下访问（见 docs/project/capture.md）。
/// </summary>
public sealed class CaptureSession : ICaptureSink, IDisposable
{
    private readonly CaptureSessionInfo _info;
    private readonly CaptureDeviceInfo _device;
    private readonly CaptureStore _store;
    private readonly CaptureLimits _limits;
    private readonly Func<ICaptureSource> _sourceFactory;
    private readonly bool _allowSend;
    private readonly CaptureSessionStateMachine _machine = new();
    private readonly CaptureClock _clock = new();
    private readonly List<CaptureRecord> _records = [];
    private readonly List<CaptureEvent> _events = [];
    private readonly List<CaptureError> _errors = [];
    private readonly StringBuilder _backendLog = new();
    private readonly object _gate = new();
    // 连点「停止」会并发写同一批文件；SemaphoreSlim 不申请等待句柄，故不占 OS 资源、不必 Dispose。
    private readonly SemaphoreSlim _stopGate = new(1, 1);

    private CaptureJsonlWriter? _recordWriter;
    private CaptureJsonlWriter? _eventWriter;
    private CaptureJsonlWriter? _errorWriter;
    private ICaptureSource? _source;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private CaptureBundle? _bundle;
    private string _directory = "";
    private long _bytes;
    private bool _disposed;

    private CaptureSession(string directory, CaptureSessionInfo info, CaptureDeviceInfo device,
        CaptureStore store, CaptureLimits limits, Func<ICaptureSource> sourceFactory, bool allowSend)
    {
        _directory = directory;
        _info = info;
        _device = device;
        _store = store;
        _limits = limits;
        _sourceFactory = sourceFactory;
        _allowSend = allowSend;
    }

    public string SessionId => _info.Id;

    public CaptureSessionInfo Info => _info;

    public CaptureDeviceInfo Device => _device;

    public CaptureSessionState State => _machine.State;

    public string Directory => _directory;

    public string? PackagePath { get; private set; }

    /// <summary>放弃采集后会话目录是否真的删掉了。false = 文件被占用，目录还在（要如实告诉用户）。</summary>
    public bool RecordsDeleted { get; private set; }

    public bool RecordsTruncated => _info.RecordsTruncated;

    public int RecordCount
    {
        get { lock (_gate) return _records.Count; }
    }

    /// <summary>相对会话开始的毫秒数。界面靠它显示「已等 N 秒」；暂停期间照走，那正是记录缺口。</summary>
    public long ElapsedMs => _clock.ElapsedMs;

    /// <summary>本会话能不能发帧：用户开始前授权过，且来源确实支持写。</summary>
    public bool CanSend => _allowSend && _source is ICaptureWriter;

    /// <summary>
    /// 用户手动发一帧（主机→设备）。**这是本功能唯一的写入路径**，每次都由用户显式触发，
    /// 且要求本会话开始前已授权。发出的帧和失败原因都会进 records.jsonl，作为证据。
    /// </summary>
    public bool TrySend(byte[] report, out string? error)
    {
        error = null;
        if (!_allowSend)
        {
            error = "本会话没有开启「允许发送」";
            return false;
        }

        if (_source is not ICaptureWriter writer)
        {
            error = "当前采集来源不支持发送";
            return false;
        }

        if (_machine.State is not (CaptureSessionState.Running or CaptureSessionState.Paused))
        {
            error = "只有在采集进行中才能发送";
            return false;
        }

        // 长度和 report ID 先按 caps 校验：HID 对长度是严格的，发错长度只会得到一个 Win32 错误
        if (_device.OutputReportLength > 0 && report.Length != _device.OutputReportLength)
        {
            error = $"长度必须是 {_device.OutputReportLength} 字节（该接口的 output 报文长度），当前 {report.Length} 字节";
            return false;
        }

        if (_device.UsesOutputReportId && !_device.OutputReportIds.Contains(report[0]))
        {
            var ids = string.Join("、", _device.OutputReportIds.Select(id => $"0x{id:X2}"));
            error = $"首字节必须是 caps 里存在的 report ID（{ids}），当前 0x{report[0]:X2}";
            return false;
        }

        var ok = writer.TrySend(report, out error);
        Record(new CaptureRecord
        {
            TimestampMono = _clock.ElapsedMs,
            TimestampUtc = DateTimeOffset.UtcNow,
            Layer = CaptureLayer.HidApiOutput,
            Source = "usb-hid",
            DeviceId = _device.DeviceId,
            InterfaceId = _device.InterfacePath,
            RecordType = "hid_output_report",
            ReportId = _device.UsesOutputReportId && report.Length > 0 ? report[0] : null,
            Api = "WriteFile",
            Data = report,
            Status = ok ? CaptureRecordStatus.Sent : CaptureRecordStatus.Failed,
            Error = ok ? null : error,
        });

        if (!ok) Log($"发帧失败：{error}");
        return ok;
    }

    public int ErrorCount
    {
        get { lock (_gate) return _errors.Count; }
    }

    public static CaptureSession Create(CaptureDeviceInfo device, IReadOnlyList<CapabilityItem> capabilities,
        CaptureOptions options, Func<ICaptureSource> sourceFactory,
        CaptureStore? store = null, CaptureLimits? limits = null)
    {
        var actualStore = store ?? new CaptureStore();
        var actualLimits = limits ?? new CaptureLimits();
        var started = DateTimeOffset.UtcNow;
        var info = new CaptureSessionInfo
        {
            Id = CaptureStore.NewSessionId(started),
            Task = options.Task,
            StartedUtc = started,
            OsVersion = options.OsVersion,
            AppVersion = options.AppVersion,
            Backend = options.Backend,
            DeviceId = device.DeviceId,
            Capabilities = capabilities,
            FeaturePollingEnabled = options.FeatureReportIds.Count > 0,
            FeaturePollReportIds = options.FeatureReportIds,
        };

        var directory = actualStore.CreateSessionDirectory(info.Id);
        var session = new CaptureSession(directory, info, device, actualStore, actualLimits, sourceFactory,
            options.AllowSend);

        session._machine.BeginProbe();
        session._machine.MarkReady();
        info.State = session._machine.State;
        CaptureStore.WriteSession(directory, info);
        CaptureStore.WriteDevice(directory, device);
        session.OpenWriters();
        session.Log($"会话 {info.Id} 已创建：{device.InterfaceKey}，{actualLimits.Describe()}");
        return session;
    }

    public bool Start()
    {
        if (!_machine.Start()) return false;
        _info.State = _machine.State;
        _clock.Start();
        AddEvent(CaptureEventKind.SessionStarted, "开始采集");
        Log($"开始采集：{_device.InterfacePath}");
        StartSource();
        return true;
    }

    /// <summary>暂停 = 停止读取并释放句柄。暂停期间**没有记录不等于没有数据**，报告里会写明这个缺口。</summary>
    public async Task PauseAsync()
    {
        if (!_machine.Pause()) return;
        _info.State = _machine.State;
        AddEvent(CaptureEventKind.Paused, "暂停采集（期间不读取设备）");
        Log("暂停采集");
        await StopSourceAsync().ConfigureAwait(false);
    }

    public void Resume()
    {
        if (!_machine.Resume()) return;
        _info.State = _machine.State;
        AddEvent(CaptureEventKind.Resumed, "继续采集");
        Log("继续采集");
        StartSource();
    }

    public void Mark(string? note, string? stepId = null)
    {
        if (_machine.State is not (CaptureSessionState.Running or CaptureSessionState.Paused)) return;
        AddEvent(CaptureEventKind.UserMark, string.IsNullOrWhiteSpace(note) ? "(未填写)" : note.Trim(), stepId);
    }

    /// <summary>停止并出包。重复调用返回同一个结果，不会重复写文件。</summary>
    public Task<CaptureBundle> StopAsync() => FinishAsync(deleteSession: false);

    /// <summary>放弃这次采集：停采集、关文件，然后把会话目录整个删掉——不留报告，也不留原始记录。</summary>
    public Task<CaptureBundle> AbortAsync() => FinishAsync(deleteSession: true);

    private async Task<CaptureBundle> FinishAsync(bool deleteSession)
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_bundle != null) return _bundle;

            _machine.Stop();
            _info.State = _machine.State;
            await StopSourceAsync().ConfigureAwait(false);

            // 快照必须在 _gate 内取：StopSourceAsync 最多只等 3 秒，来源线程可能还在往 _records 里写。
            List<CaptureRecord> records;
            lock (_gate)
            {
                _recordWriter?.Dispose();
                _recordWriter = null;
                records = [.. _records];
                _info.RecordCount = records.Count;
            }

            AddEvent(CaptureEventKind.SessionStopped, deleteSession ? "放弃采集" : "结束采集");
            _eventWriter?.Dispose();
            _eventWriter = null;
            _errorWriter?.Dispose();
            _errorWriter = null;

            List<CaptureEvent> events;
            List<CaptureError> errors;
            lock (_gate)
            {
                events = [.. _events];
                errors = [.. _errors];
            }

            _info.EndedUtc = DateTimeOffset.UtcNow;
            _info.DurationMs = _clock.ElapsedMs;
            _info.EventCount = events.Count;
            _info.ErrorCount = errors.Count;

            var bundle = new CaptureBundle
            {
                Session = _info,
                Device = _device,
                Records = records,
                Events = events,
                Errors = errors,
                Analysis = CaptureAnalyzer.Analyze(records, events, errors),
            };

            if (deleteSession)
            {
                // 放弃：不写 analysis / report / zip，直接把目录删掉。删不掉要如实记下来，别假装成功。
                Log($"放弃采集：{records.Count} 条记录，删除目录 {_directory}");
                RecordsDeleted = await _store.TryDeleteSessionAsync(_directory).ConfigureAwait(false);
                Log(RecordsDeleted ? "会话目录已删除" : "会话目录未能删除（文件被占用）");
            }
            else
            {
                WriteOutputs(bundle);
            }

            _bundle = bundle;
            return bundle;
        }
        finally
        {
            _stopGate.Release();
        }
    }

    /// <summary>写 analysis / README / 报告并打包。收尾失败只记日志，内存里的 bundle 仍然返回。</summary>
    private void WriteOutputs(CaptureBundle bundle)
    {
        try
        {
            CaptureStore.WriteSession(_directory, _info);
            CaptureStore.WriteAnalysis(_directory, bundle.Analysis);
            CaptureStore.WriteReadme(_directory, bundle);
            CaptureStore.WriteBackendLog(_directory, _backendLog.ToString());
            var zipPath = CaptureStore.PathOf(_directory, CapturePackageWriter.ZipFileName);
            // report.html 必须先写，再打包，否则压缩包里没有报告。
            CaptureStore.WriteReport(_directory, bundle, zipPath);
            CapturePackageWriter.Write(_directory, zipPath, overwrite: true);
            PackagePath = zipPath;
            Log($"收尾完成：{_info.RecordCount} 条记录，诊断包 {zipPath}");
            CaptureStore.WriteBackendLog(_directory, _backendLog.ToString());
        }
        catch (Exception ex)
        {
            Utils.Log.Error($"采集收尾失败 {_directory}", ex);
            Log($"收尾失败：{ex.Message}");
        }
    }

    public IReadOnlyList<CaptureRecord> Snapshot(int max)
    {
        lock (_gate)
        {
            var start = Math.Max(0, _records.Count - max);
            return _records.Skip(start).ToList();
        }
    }

    /// <summary>从 <paramref name="startIndex"/> 起取至多 <paramref name="max"/> 条。界面增量刷新用，避免整表重建。</summary>
    public IReadOnlyList<CaptureRecord> Slice(int startIndex, int max)
    {
        lock (_gate)
        {
            if (startIndex >= _records.Count || max <= 0) return [];
            var start = Math.Max(startIndex, _records.Count - max);
            return _records.GetRange(start, _records.Count - start);
        }
    }

    public IReadOnlyList<CaptureError> ErrorsSnapshot()
    {
        lock (_gate) return [.. _errors];
    }

    public void Record(CaptureRecord record)
    {
        var reachedLimit = false;
        lock (_gate)
        {
            if (_info.RecordsTruncated) return;
            if (_limits.IsExceeded(_records.Count, _bytes))
            {
                _info.RecordsTruncated = true;
                var limit = new CaptureError
                {
                    TimestampMono = _clock.ElapsedMs,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Kind = CaptureErrorKind.BufferLimit,
                    Message = $"达到采集上限（{_limits.Describe()}），后续报文未记录",
                    DeviceId = _device.DeviceId,
                };
                _errors.Add(limit);
                _errorWriter?.Append(CaptureJson.ErrorLine(limit));
                reachedLimit = true;
            }
            else
            {
                _records.Add(record);
                _bytes += record.Data.Length;
                _recordWriter?.Append(CaptureJson.RecordLine(record));
                if (_records.Count % 64 == 0) _recordWriter?.Flush();
            }
        }

        if (!reachedLimit) return;
        Log("达到采集上限，停止记录");
        CancelSource();
    }

    /// <summary>取消来源。**只能在 _gate 外调用**：Cancel 会同步跑回调，而 StopSourceAsync 会在别处 Dispose 同一个 CTS。</summary>
    private void CancelSource()
    {
        var cts = _cts;
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 停止流程刚好把它释放了，属正常竞态
        }
    }

    public void Error(CaptureError error)
    {
        lock (_gate)
        {
            _errors.Add(error);
            _errorWriter?.Append(CaptureJson.ErrorLine(error));
        }
        Log($"[{error.Kind}] {error.Message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _source?.Dispose();
            _source = null;
            lock (_gate)
            {
                _recordWriter?.Dispose();
                _eventWriter?.Dispose();
                _errorWriter?.Dispose();
                _recordWriter = null;
                _eventWriter = null;
                _errorWriter = null;
            }
        }
        catch (Exception ex)
        {
            Utils.Log.Warn($"采集会话释放失败: {ex.Message}");
        }
    }

    private void StartSource()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var source = _sourceFactory();
        _source = source;
        var clock = _clock;
        _runTask = Task.Run(async () =>
        {
            try
            {
                await source.RunAsync(clock, this, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                Error(new CaptureError
                {
                    TimestampMono = clock.ElapsedMs,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Kind = CaptureErrorKind.SourceException,
                    Message = $"采集来源异常退出：{ex.Message}",
                    DeviceId = _device.DeviceId,
                });
                _machine.Fail();
                _info.State = _machine.State;
            }
        }, token);
    }

    private async Task StopSourceAsync()
    {
        var cts = _cts;
        var task = _runTask;
        _cts = null;
        _runTask = null;
        if (cts == null) return;

        try
        {
            cts.Cancel();
            if (task != null) await Task.WhenAny(task, Task.Delay(3000)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Utils.Log.Warn($"停止采集来源失败: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            _source?.Dispose();
            _source = null;
        }
    }

    /// <summary>记录用户从官方软件/设备屏幕读到的电量。**外部参照，不是报文**，只进 events.jsonl。</summary>
    public bool MarkBattery(int percent)
    {
        if (_machine.State is not (CaptureSessionState.Running or CaptureSessionState.Paused)) return false;
        // 0 在界面上表示"不使用"，这里同样拒绝，免得写出 0% 这种没意义的参照
        if (percent is < 1 or > 100) return false;
        AddEvent(CaptureEventKind.BatteryReference, $"{percent}%", null, percent);
        return true;
    }

    private void AddEvent(CaptureEventKind kind, string? note, string? stepId = null, int? percent = null)
    {
        var item = new CaptureEvent
        {
            TimestampMono = _clock.ElapsedMs,
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = kind,
            Note = note,
            StepId = stepId,
            Percent = percent,
            DeviceId = _device.DeviceId,
        };
        lock (_gate)
        {
            _events.Add(item);
            _eventWriter?.Append(CaptureJson.EventLine(item));
            _eventWriter?.Flush();
        }
    }

    private void OpenWriters()
    {
        _recordWriter = new CaptureJsonlWriter(CaptureStore.PathOf(_directory, "records.jsonl"));
        _eventWriter = new CaptureJsonlWriter(CaptureStore.PathOf(_directory, "events.jsonl"));
        _errorWriter = new CaptureJsonlWriter(CaptureStore.PathOf(_directory, "errors.jsonl"));
    }

    /// <summary>采集后端自己的日志，随包分发；只记本会话相关事件，不记用户目录等无关信息。</summary>
    private void Log(string message)
    {
        lock (_gate)
        {
            if (_backendLog.Length > 64 * 1024) return;
            _backendLog.Append(DateTimeOffset.Now.ToString("HH:mm:ss.fff")).Append("  ").AppendLine(message);
        }
        Utils.Log.Info($"[采集 {_info.Id}] {message}");
    }
}
