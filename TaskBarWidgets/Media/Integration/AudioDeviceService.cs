// Derived from AF-Media-Bar/Classes/Services/Audio/AudioDeviceService.cs (MIT).
// Extended for capture endpoints, volume notifications, independent roles and readback.
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace TaskBarWidgets.Media.Integration;

public sealed record AudioEndpoint(string Id, string Name, bool IsDefault, bool IsCommunications,
    double Volume, bool IsMuted)
{
    public override string ToString() => Name + (IsCommunications ? "  · 通信" : "");
}

public sealed record AudioSnapshot(IReadOnlyList<AudioEndpoint> Outputs, IReadOnlyList<AudioEndpoint> Inputs);

public sealed class AudioDeviceService : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MMDeviceEnumerator? _enumerator;
    private readonly List<MMDevice> _watched = [];
    private NotificationClient? _notification;
    private int _disposed;
    private int _signalPending;
    private readonly object _disposeLock = new();
    private Task _disposal = Task.CompletedTask;
    public event Action? Changed;

    public Task<AudioSnapshot> GetSnapshotAsync() => RunAsync(() =>
    {
        EnsureEnumerator();
        ReleaseWatchedDevices();
        return new AudioSnapshot(Read(DataFlow.Render), Read(DataFlow.Capture));
    });

    public Task<AudioSwitchResult> SetDefaultAsync(string id, bool input, bool communications) => RunAsync(() =>
    {
        EnsureEnumerator();
        // Validate active endpoint and direction before asking the undocumented policy service.
        using var device = _enumerator!.GetDevice(id);
        if (device.State != DeviceState.Active || device.DataFlow != (input ? DataFlow.Capture : DataFlow.Render))
            throw new InvalidOperationException("设备已断开或设备类型不匹配");
        var errors = AudioPolicyConfig.SetDefaultEndpoint(id, communications);
        var flow = input ? DataFlow.Capture : DataFlow.Render;
        return AudioRolePolicy.ReadBack(id, communications, role => DefaultId(flow, (Role)role), errors);
    });

    public Task SetVolumeAsync(string id, double volume, bool? mute = null) => RunAsync(() =>
    {
        EnsureEnumerator();
        using var device = _enumerator!.GetDevice(id);
        if (device.State != DeviceState.Active) throw new InvalidOperationException("设备已断开");
        device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(Math.Clamp(volume, 0, 100) / 100);
        if (mute.HasValue) device.AudioEndpointVolume.Mute = mute.Value;
        return true;
    });

    private IReadOnlyList<AudioEndpoint> Read(DataFlow flow)
    {
        var current = DefaultId(flow, Role.Console);
        var communications = DefaultId(flow, Role.Communications);
        var result = new List<AudioEndpoint>();
        var devices = _enumerator!.EnumerateAudioEndPoints(flow, DeviceState.Active);
        for (var index = 0; index < devices.Count; index++)
        {
            MMDevice? device = null;
            try
            {
                device = devices[index];
                var isDefault = string.Equals(device.ID, current, StringComparison.OrdinalIgnoreCase);
                result.Add(new AudioEndpoint(device.ID, device.FriendlyName, isDefault,
                    string.Equals(device.ID, communications, StringComparison.OrdinalIgnoreCase),
                    device.AudioEndpointVolume.MasterVolumeLevelScalar * 100, device.AudioEndpointVolume.Mute));
                if (isDefault)
                {
                    device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
                    _watched.Add(device);
                    device = null;
                }
            }
            catch (COMException) { /* A hotplug may invalidate a device midway through the snapshot. */ }
            finally { device?.Dispose(); }
        }
        return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private string? DefaultId(DataFlow flow, Role role)
    {
        try
        {
            if (!_enumerator!.HasDefaultAudioEndpoint(flow, role)) return null;
            using var device = _enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
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
    private void SignalChanged()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _signalPending, 1) != 0) return;
        // Never invoke consumers on the COM notification thread or while an operation holds _gate.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (Volatile.Read(ref _disposed) == 0) Changed?.Invoke();
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            finally { Interlocked.Exchange(ref _signalPending, 0); }
        });
    }

    private void ReleaseWatchedDevices()
    {
        foreach (var device in _watched)
        {
            try { device.AudioEndpointVolume.OnVolumeNotification -= OnVolume; }
            catch (COMException exception) { System.Diagnostics.Trace.WriteLine(exception); }
            finally
            {
                try { device.Dispose(); }
                catch (COMException exception) { System.Diagnostics.Trace.WriteLine(exception); }
            }
        }
        _watched.Clear();
    }

    private Task<T> RunAsync<T>(Func<T> operation) => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return operation();
        }
        finally { _gate.Release(); }
    });

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Changed = null;
            // COM release is serialized with in-flight work, away from the WPF dispatcher.
            _disposal = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    try
                    {
                        if (_notification is not null) _enumerator?.UnregisterEndpointNotificationCallback(_notification);
                    }
                    catch (COMException exception) { System.Diagnostics.Trace.WriteLine(exception); }
                    ReleaseWatchedDevices();
                    try { _enumerator?.Dispose(); }
                    catch (COMException exception) { System.Diagnostics.Trace.WriteLine(exception); }
                    _enumerator = null;
                    _notification = null;
                }
                finally { _gate.Release(); }
            });
            _ = _disposal.ContinueWith(task => System.Diagnostics.Trace.WriteLine(task.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(_disposal);
    }

    private sealed class NotificationClient(AudioDeviceService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.SignalChanged();
        public void OnDeviceAdded(string deviceId) => owner.SignalChanged();
        public void OnDeviceRemoved(string deviceId) => owner.SignalChanged();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => owner.SignalChanged();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) => owner.SignalChanged();
    }
}
