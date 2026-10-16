// Derived from AF-Media-Bar/Classes/Services/Audio/AudioDeviceService.cs (MIT).
// Extended for capture endpoints, persistent notifications and independent mute writes.
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using TaskbarLyrics.Core.Utilities;

namespace TaskBarWidgets.Media.Integration;

public sealed record AudioEndpoint(string Id, string Name, bool IsDefault, bool IsCommunications,
    double Volume, bool IsMuted)
{
    public bool CanControl { get; init; } = true;
    public override string ToString() => Name + (IsCommunications ? "  · 通信" : "");
}

public sealed record AudioSnapshot(IReadOnlyList<AudioEndpoint> Outputs, IReadOnlyList<AudioEndpoint> Inputs);

public sealed class AudioDeviceService : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private MMDeviceEnumerator? _enumerator;
    private readonly List<CachedDevice> _devices = [];
    private readonly Dictionary<(DataFlow Flow, Role Role), string?> _defaults = [];
    private NotificationClient? _notification;
    private int _topologyRevision;
    private int _cachedTopologyRevision = -1;
    private int _roleRevision;
    private int _cachedRoleRevision = -1;
    private int _disposed;
    private int _signalPending;
    private int _timingLogs;
    private int _snapshotLogs;
    private readonly object _disposeLock = new();
    private Task _disposal = Task.CompletedTask;
    private sealed record CachedDevice(MMDevice Device, string Id, string Name, DataFlow Flow, bool Subscribed);
    public event Action? Changed;

    public Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => RunAsync("snapshot", () =>
    {
        EnsureEnumerator();
        RefreshDevices();
        RefreshDefaults();
        return new AudioSnapshot(Read(DataFlow.Render), Read(DataFlow.Capture));
    }, cancellationToken);

    public Task<AudioSwitchResult> SetDefaultAsync(string id, bool input, bool communications, CancellationToken cancellationToken = default) => RunAsync("setDefault", () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var device = OpenEndpoint(id, input);
        var errors = AudioPolicyConfig.SetDefaultEndpoint(device.ID, communications);
        Interlocked.Increment(ref _roleRevision);
        var flow = input ? DataFlow.Capture : DataFlow.Render;
        return AudioRolePolicy.ReadBack(device.ID, communications, role => DefaultId(flow, (Role)role), errors);
    }, cancellationToken);

    public Task SetVolumeAsync(string? id, double volume, bool? input = null, CancellationToken cancellationToken = default) => RunAsync("setVolume", () =>
    {
        if (!double.IsFinite(volume) || volume < 0 || volume > 100) throw new ArgumentOutOfRangeException(nameof(volume), "音量范围无效。");
        using var device = OpenEndpoint(id, input);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(volume / 100);
        return true;
    }, cancellationToken);

    public Task SetMuteAsync(string? id, bool muted, bool? input = null, CancellationToken cancellationToken = default) => RunAsync("setMute", () =>
    {
        using var device = OpenEndpoint(id, input);
        device.AudioEndpointVolume.Mute = muted;
        if (device.AudioEndpointVolume.Mute != muted) throw new InvalidOperationException("设备未确认新的静音状态，请重试。");
        return true;
    }, cancellationToken);

    private MMDevice OpenEndpoint(string? id, bool? input)
    {
        EnsureEnumerator();
        MMDevice device;
        if (id is null)
        {
            if (!input.HasValue) throw new ArgumentException("选择默认设备时必须指定输入或输出类型。");
            device = _enumerator!.GetDefaultAudioEndpoint(input.Value ? DataFlow.Capture : DataFlow.Render, Role.Console);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 2048) throw new ArgumentException("设备标识无效。");
            device = _enumerator!.GetDevice(id);
        }
        try
        {
            if (device.State != DeviceState.Active || input.HasValue && device.DataFlow != (input.Value ? DataFlow.Capture : DataFlow.Render))
                throw new InvalidOperationException("设备已断开或设备类型不匹配。");
            return device;
        }
        catch { device.Dispose(); throw; }
    }

    private void RefreshDevices()
    {
        var revision = Volatile.Read(ref _topologyRevision);
        if (_cachedTopologyRevision == revision) return;
        ReleaseDevices();
        try
        {
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                var devices = _enumerator!.EnumerateAudioEndPoints(flow, DeviceState.Active);
                for (var index = 0; index < devices.Count; index++)
                {
                    MMDevice? device = null;
                    try
                    {
                        device = devices[index];
                        var id = device.ID; var name = device.FriendlyName; var subscribed = false;
                        try { device.AudioEndpointVolume.OnVolumeNotification += OnVolume; subscribed = true; }
                        catch (COMException) { /* Snapshot readback remains available when notifications are unsupported. */ }
                        _devices.Add(new CachedDevice(device, id, name, flow, subscribed));
                        device = null;
                    }
                    catch (COMException) { Interlocked.Increment(ref _topologyRevision); }
                    finally { device?.Dispose(); }
                }
            }
            _cachedTopologyRevision = revision;
        }
        catch { ReleaseDevices(); _cachedTopologyRevision = -1; throw; }
    }

    private void RefreshDefaults()
    {
        var revision = Volatile.Read(ref _roleRevision);
        if (_cachedRoleRevision == revision) return;
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            foreach (var role in new[] { Role.Console, Role.Communications }) _defaults[(flow, role)] = DefaultId(flow, role);
        _cachedRoleRevision = revision;
    }

    private IReadOnlyList<AudioEndpoint> Read(DataFlow flow)
    {
        var result = new List<AudioEndpoint>();
        foreach (var entry in _devices.Where(entry => entry.Flow == flow))
        {
            try
            {
                result.Add(new AudioEndpoint(entry.Id, entry.Name,
                    string.Equals(entry.Id, _defaults.GetValueOrDefault((flow, Role.Console)), StringComparison.OrdinalIgnoreCase),
                    string.Equals(entry.Id, _defaults.GetValueOrDefault((flow, Role.Communications)), StringComparison.OrdinalIgnoreCase),
                    entry.Device.AudioEndpointVolume.MasterVolumeLevelScalar * 100, entry.Device.AudioEndpointVolume.Mute));
            }
            catch (COMException) {
                // Some drivers reject NAudio's native notification registration in
                // the volume getter itself. Keep the endpoint and report unavailable
                // controls instead of inventing a volume or rebuilding subscriptions.
                result.Add(new AudioEndpoint(entry.Id, entry.Name,
                    string.Equals(entry.Id, _defaults.GetValueOrDefault((flow, Role.Console)), StringComparison.OrdinalIgnoreCase),
                    string.Equals(entry.Id, _defaults.GetValueOrDefault((flow, Role.Communications)), StringComparison.OrdinalIgnoreCase), 0, false) { CanControl = false });
            }
        }
        return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private string? DefaultId(DataFlow flow, Role role)
    {
        try { using var device = _enumerator!.GetDefaultAudioEndpoint(flow, role); return device.ID; }
        catch (COMException) { return null; }
    }
    private void EnsureEnumerator()
    {
        if (_enumerator is not null) return;
        _enumerator = new MMDeviceEnumerator();
        _notification = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_notification);
    }
    private void OnVolume(AudioVolumeNotificationData _) => SignalChanged();
    private void InvalidateTopology() { Interlocked.Increment(ref _topologyRevision); Interlocked.Increment(ref _roleRevision); SignalChanged(); }
    private void InvalidateDefaults() { Interlocked.Increment(ref _roleRevision); SignalChanged(); }
    private void SignalChanged()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _signalPending, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { if (Volatile.Read(ref _disposed) == 0) Changed?.Invoke(); }
            catch (Exception exception) { Trace.WriteLine($"Audio change callback: {exception.GetType().Name}"); }
            finally { Interlocked.Exchange(ref _signalPending, 0); }
        });
    }
    private void ReleaseDevices()
    {
        foreach (var entry in _devices)
        {
            try { if (entry.Subscribed) entry.Device.AudioEndpointVolume.OnVolumeNotification -= OnVolume; }
            catch (COMException) { }
            finally { try { entry.Device.Dispose(); } catch (COMException) { } }
        }
        _devices.Clear();
    }
    private Task<T> RunAsync<T>(string name, Func<T> operation, CancellationToken cancellationToken)
    {
        var queued = Stopwatch.GetTimestamp();
        return Task.Run(async () =>
        {
            var acquired = false;
            var started = queued;
            var outcome = "ok";
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
                await _gate.WaitAsync(linked.Token).ConfigureAwait(false); acquired = true;
                started = Stopwatch.GetTimestamp();
                linked.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return operation();
            }
            catch (Exception exception) { outcome = exception.GetType().Name; throw; }
            finally
            {
                var finished = Stopwatch.GetTimestamp();
                if (acquired) _gate.Release();
                var queueMs = Stopwatch.GetElapsedTime(queued, acquired ? started : finished).TotalMilliseconds;
                var operationMs = acquired ? Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds : 0;
                if ((name != "snapshot" || Interlocked.Increment(ref _snapshotLogs) <= 4 || queueMs + operationMs >= 100) &&
                    Volatile.Read(ref _timingLogs) < 128 && Interlocked.Increment(ref _timingLogs) <= 128)
                    Log.Diagnostic("AUDIO", $"operation={name} queueMs={queueMs:F1} operationMs={operationMs:F1} outcome={outcome}");
            }
        });
    }
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Changed = null; _stop.Cancel();
            _disposal = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    try { if (_notification is not null) _enumerator?.UnregisterEndpointNotificationCallback(_notification); }
                    catch (COMException) { }
                    ReleaseDevices();
                    try { _enumerator?.Dispose(); } catch (COMException) { }
                    _enumerator = null; _notification = null;
                }
                finally { _gate.Release(); _stop.Dispose(); }
            });
            _ = _disposal.ContinueWith(task => Trace.WriteLine($"Audio disposal: {task.Exception?.GetType().Name}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return new ValueTask(_disposal); }
    private sealed class NotificationClient(AudioDeviceService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.InvalidateTopology();
        public void OnDeviceAdded(string deviceId) => owner.InvalidateTopology();
        public void OnDeviceRemoved(string deviceId) => owner.InvalidateTopology();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => owner.InvalidateDefaults();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) => owner.InvalidateTopology();
    }
}
