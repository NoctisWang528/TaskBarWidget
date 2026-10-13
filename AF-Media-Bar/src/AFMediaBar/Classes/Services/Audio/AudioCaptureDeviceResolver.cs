// Resolves the audible endpoint in the background; each scan disposes its NAudio devices and sessions.
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using AFMediaBar.Classes.Abstractions;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 后台解析频谱采集的目标输出端点，并把结果缓存下来供采集路径读取。
///
/// 端点和音频会话的枚举（Core Audio + 进程查询）是毫秒级的阻塞调用，不能从频谱的 UI 计时器执行：
/// 本服务只在频谱持续请求采样时，用一条后台循环按档位节奏扫描（正常 2 秒、Idle 10 秒、息屏/睡眠不枚举），
/// 选出"正在出声的那个设备"后写入 <see cref="TargetDeviceId"/>；采集路径只读取这个缓存值，不做任何枚举。
/// Resolves the spectrum capture's target render endpoint on a background loop and caches the result for the capture path.
///
/// Enumerating endpoints and audio sessions (Core Audio plus process queries) consists of blocking calls in the millisecond range and
/// must not run from the spectrum's UI timer: while spectrum samples are requested, a background loop scans on a level-aware cadence
/// (two seconds normally, ten when idle, no enumeration while the display is off or the system suspends), picks the audible device and
/// writes it to <see cref="TargetDeviceId"/>; the capture path only reads that cached value and never enumerates.
/// </summary>
public sealed class AudioCaptureDeviceResolver : IDisposable, IMemoryPrunable
{

    /// <summary>"有会话在出声"的峰值下限。会话表是端点音量之前的数值，因此不受系统音量影响，低音量听歌也能被识别。
    /// Session peak that counts as audible. Session meters are pre-endpoint-volume, so system volume does not scale them and quiet
    /// listening is still recognized.</summary>
    private const float AudibleSessionPeakThreshold = 0.002f;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _scanRequested = new(0, 1);
    private int _scanWakePending;
    private long _lastDemandTick = long.MinValue;
    private volatile string? _targetDeviceId;
    private volatile MemoryPruneLevel _pruneLevel;
    private volatile bool _isDisposed;

    // 频谱最慢每 200 ms 取样一次；停止请求满一秒即让后台循环停在信号量上，不再周期唤醒。
    // Spectrum samples arrive at least every 200 ms; after one second without demand the loop parks without periodic wakeups.
    private const long DemandIdleMilliseconds = 1_000;

    /// <summary>最近一次解析出的目标端点标识；尚未解析或解析不出时为空，采集路径遇到空值会回退到系统默认端点。
    /// The endpoint identifier resolved most recently; null before the first resolution or when nothing could be resolved, in which
    /// case the capture path falls back to the system default endpoint.</summary>
    public string? TargetDeviceId => _targetDeviceId;

    /// <summary>参与者名称，只用于诊断。/ Participant name, used for diagnostics only.</summary>
    public string PruneParticipantName => "audio-capture-device";

    /// <summary>
    /// 启动按需解析循环；频谱第一次取样时立即解析，避免后台无频谱时仍枚举音频会话。
    /// Starts the demand-driven resolution loop; the first spectrum sample triggers an immediate scan without background enumeration
    /// while the spectrum is unused.
    /// </summary>
    public AudioCaptureDeviceResolver()
    {
        var token = _cancellation.Token;
        _ = Task.Run(() => RunLoopAsync(token));
    }

    /// <summary>报告频谱正在取样；从休眠状态恢复时唤醒后台解析器，不在调用线程枚举设备。
    /// Reports active spectrum sampling and wakes the background resolver when parked, without enumerating on the caller's thread.</summary>
    public void RequestScan()
    {
        if (_isDisposed)
        {
            return;
        }

        var now = Environment.TickCount64;
        var previous = Interlocked.Exchange(ref _lastDemandTick, now);
        if (previous != long.MinValue && now - previous <= DemandIdleMilliseconds)
        {
            return;
        }

        // Several taskbar hosts can resume together; coalesce their wake-ups before
        // Release so the normal full-semaphore case never throws a first-chance exception.
        if (Interlocked.Exchange(ref _scanWakePending, 1) != 0)
            return;
        try { _scanRequested.Release(); }
        catch (ObjectDisposedException)
        {
            // A final UI tick can race with application shutdown.
        }
    }

    /// <summary>记录剪枝档位；循环按档位决定扫描间隔与是否枚举。/ Records the prune level; the loop derives its scan interval and whether to enumerate from it.</summary>
    /// <param name="level">目标档位。/ The target level.</param>
    public void Prune(MemoryPruneLevel level) => _pruneLevel = level;

    /// <summary>停止后台循环；循环在下一个唤醒点自我结束，已解析的目标保持可读（只影响采集重建的时机）。</summary>
    /// <summary>Stops the background loop; it ends at its next wake point, and the last resolved target stays readable (which only affects when a capture is rebuilt).</summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            await _scanRequested.WaitAsync(token).ConfigureAwait(false);
            Interlocked.Exchange(ref _scanWakePending, 0);
            while (!token.IsCancellationRequested)
            {
                if (!HasRecentDemand())
                {
                    await _scanRequested.WaitAsync(token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _scanWakePending, 0);
                }

                ResolveOnce(token);
                if (await _scanRequested.WaitAsync(AudioCaptureDevicePolicy.ResolveScanInterval(_pruneLevel), token)
                        .ConfigureAwait(false))
                    Interlocked.Exchange(ref _scanWakePending, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose cancels either wait immediately.
        }
        finally
        {
            _scanRequested.Dispose();
        }
    }

    private bool HasRecentDemand()
    {
        var last = Interlocked.Read(ref _lastDemandTick);
        return last != long.MinValue && Environment.TickCount64 - last <= DemandIdleMilliseconds;
    }

    /// <summary>
    /// 成功枚举后更新缓存（可清空失效 ID）；枚举失败时保留上一次结果，下一次唤醒再试。
    /// Updates the cache after a successful enumeration (including clearing a stale ID); an enumeration failure keeps the previous result.
    /// </summary>
    private void ResolveOnce(CancellationToken token)
    {
        if (token.IsCancellationRequested ||
            !AudioCaptureDevicePolicy.ShouldScan(_pruneLevel, HasRecentDemand()))
        {
            return;
        }

        try
        {
            if (TryResolveTarget(out var resolved))
            {
                if (!token.IsCancellationRequested && !_isDisposed)
                    _targetDeviceId = resolved;
            }
        }
        catch (Exception ex)
        {
            // 音频栈在设备切换/驱动重启期间会短暂抛错：这不是故障，保留上一次目标即可。
            // The audio stack briefly throws while devices switch or drivers restart: that is not a failure, keeping the previous target is enough.
            Debug.WriteLine($"[AudioCaptureDeviceResolver] Resolve failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 枚举活动端点与会话，按 <see cref="AudioCaptureDevicePolicy.SelectTarget"/> 选出目标标识；枚举失败与成功但无端点分开报告。
    /// 每次解析都在当前线程自建并释放 COM 对象，解析之间不共享任何 COM 实例，因此循环运行在线程池线程上也安全。
    /// Enumerates active endpoints and sessions and picks the target identifier through <see cref="AudioCaptureDevicePolicy.SelectTarget"/>;
    /// enumeration failure is distinct from a successful scan with no endpoints.
    /// Every resolution creates and releases its COM objects on the current thread and shares nothing between resolutions, so running
    /// the loop on a thread-pool thread is safe.
    /// </summary>
    private bool TryResolveTarget(out string? targetDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = ResolveDefaultEndpointId(enumerator);
        var activeDeviceIds = new List<string>();
        var candidates = new List<AudioEndpointAudibility>();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var index = 0; index < devices.Count; index++)
        {
            try
            {
                using var endpoint = devices[index];
                var id = endpoint.ID;
                activeDeviceIds.Add(id);
                var rank = ResolveAudibilityRank(endpoint, out var peak);
                if (rank > AudioCaptureDevicePolicy.RankSilent)
                    candidates.Add(new AudioEndpointAudibility(id, rank, peak));
            }
            catch (COMException)
            {
                // A device may disappear while scanning; continue with surviving endpoints.
            }
        }
        targetDeviceId = AudioCaptureDevicePolicy.SelectTarget(_targetDeviceId, defaultId, activeDeviceIds, candidates);
        return true;
    }

    private static int ResolveAudibilityRank(MMDevice endpoint, out float peak)
    {
        peak = 0;
        try
        {
            var sessions = endpoint.AudioSessionManager.Sessions;
            var applicationPeak = 0f;
            var systemPeak = 0f;
            for (var index = 0; index < sessions.Count; index++)
            {
                try
                {
                    using var session = sessions[index];
                    if (session.State != AudioSessionState.AudioSessionStateActive)
                        continue;
                    var processId = session.GetProcessID;
                    if (processId == 0 || processId == (uint)Environment.ProcessId)
                        continue;
                    var sessionPeak = session.AudioMeterInformation?.MasterPeakValue ?? 0;
                    if (IsSystemAudioProcess(processId))
                        systemPeak = Math.Max(systemPeak, sessionPeak);
                    else
                        applicationPeak = Math.Max(applicationPeak, sessionPeak);
                }
                catch (COMException)
                {
                    // One expired session must not suppress the remaining audible sessions.
                }
            }
            peak = Math.Max(applicationPeak, systemPeak);
            return applicationPeak >= AudibleSessionPeakThreshold
                ? AudioCaptureDevicePolicy.RankApplication
                : systemPeak >= AudibleSessionPeakThreshold
                    ? AudioCaptureDevicePolicy.RankSystemOnly
                    : AudioCaptureDevicePolicy.RankSilent;
        }
        catch (COMException)
        {
            return AudioCaptureDevicePolicy.RankSilent;
        }
    }

    /// <summary>该进程是否是系统混音进程（audiodg）；查不到进程时按系统进程处理，它不会成为"应用在出声"的证据。
    /// Whether the process is the system audio engine (audiodg); an unresolvable process counts as a system one, so it can never
    /// become evidence of "an application is playing".</summary>
    private static bool IsSystemAudioProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "audiodg", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>解析系统默认多媒体端点的标识；不可用时返回 null。/ Resolves the system default multimedia endpoint identifier, or null.</summary>
    private static string? ResolveDefaultEndpointId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return endpoint.ID;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
