using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using TaskBarWidgets.Media.Diagnostics;

namespace TaskbarLyrics.App;

public sealed class SystemAudioSpectrumService : IDisposable
{
    private const int RingBufferSize = 8192;
    private const int ClsctxAll = 23;
    private const int AudclntSharemodeShared = 0;
    private const int AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntBufferflagsSilent = 0x00000002;
    private const int DefaultDeviceCheckIntervalMs = 500;
    private const int CaptureRetryBaseDelayMs = 100;
    private const int CaptureRetryMaxDelayMs = 2000;
    private const int PacketExpiryMs = 250;
    private const short WaveFormatPcm = 1;
    private const short WaveFormatIeeeFloat = 3;
    private const short WaveFormatExtensibleTag = -2;

    private static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IAudioCaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    // Hann windows depend only on the coerced sample window, which is 512, 1024 or 2048,
    // so this cache stays bounded at three immutable arrays for the process lifetime.
    private static readonly ConcurrentDictionary<int, double[]> HannWindows = new();

    private readonly object _lifecycleSync = new();
    private readonly object _sync = new();
    private float[][] _ringBuffers = [new float[RingBufferSize]];
    private float[] _smoothedBars = new float[SpectrumTuningSettings.DefaultBarCount];
    private SpectrumScratch? _idleScratch;
    private CancellationTokenSource? _captureCancellation;
    private Thread? _captureThread;
    private int _consecutiveCaptureFailures;
    private int _writeIndex;
    private int _sampleRate = 48000;
    private float _adaptivePeak = (float)SpectrumTuningSettings.CreateDefault().PeakInitial;
    private long? _lastPacketTick;
    private int _signalGeneration;
    private SpectrumTuningSettings _tuningSettings = SpectrumTuningSettings.CreateDefault();
    private volatile bool _isAvailable;
    private int _diagnosticSampleRate;
    private int _diagnosticChannels;
    private float _diagnosticInputPeak;
    private DateTimeOffset? _lastAudioUtc;
    private string _diagnosticFormat = "Waiting";
    private string _lastError = string.Empty;
    private bool _disposed;

    public bool IsAvailable => _isAvailable;

    public SpectrumCaptureDiagnostics GetDiagnostics()
    {
        lock (_sync)
        {
            return new SpectrumCaptureDiagnostics(
                _isAvailable,
                _diagnosticSampleRate,
                _diagnosticChannels,
                _diagnosticFormat,
                _diagnosticInputPeak,
                _lastAudioUtc,
                _lastError);
        }
    }

    public void ApplyTuning(SpectrumTuningSettings settings)
    {
        var snapshot = settings.Clone();
        snapshot.SampleWindow = CoerceSampleWindow(snapshot.SampleWindow);
        snapshot.UpdateIntervalMs = Math.Clamp(snapshot.UpdateIntervalMs, 16, 100);
        snapshot.BarCount = Math.Clamp(
            snapshot.BarCount,
            SpectrumTuningSettings.MinBarCount,
            SpectrumTuningSettings.MaxBarCount);
        snapshot.MinFrequency = Math.Clamp(
            snapshot.MinFrequency,
            SpectrumTuningSettings.MinimumFrequency,
            SpectrumTuningSettings.MaximumFrequency - SpectrumTuningSettings.MinimumFrequencySpan);
        snapshot.MaxFrequency = Math.Clamp(
            snapshot.MaxFrequency,
            SpectrumTuningSettings.MinimumFrequency + SpectrumTuningSettings.MinimumFrequencySpan,
            SpectrumTuningSettings.MaximumFrequency);
        snapshot.FrequencyDistributionBias = Math.Clamp(snapshot.FrequencyDistributionBias, -1, 1);
        if (snapshot.MaxFrequency < snapshot.MinFrequency + SpectrumTuningSettings.MinimumFrequencySpan)
        {
            snapshot.MaxFrequency = snapshot.MinFrequency + SpectrumTuningSettings.MinimumFrequencySpan;
        }

        snapshot.PeakInitial = Math.Clamp(snapshot.PeakInitial, 0.004, 0.2);
        snapshot.PeakDecay = Math.Clamp(snapshot.PeakDecay, 0.85, 0.995);
        snapshot.PeakFloor = Math.Clamp(snapshot.PeakFloor, 0.003, 0.08);
        snapshot.PeakCeiling = Math.Clamp(snapshot.PeakCeiling, snapshot.PeakFloor, 1.0);
        snapshot.NoiseFloor = Math.Clamp(snapshot.NoiseFloor, 0, 0.25);
        snapshot.OutputCurve = Math.Clamp(snapshot.OutputCurve, 0.25, 1.5);
        snapshot.LowBandGain = Math.Clamp(snapshot.LowBandGain, 0.2, 4);
        snapshot.BandGainStep = Math.Clamp(snapshot.BandGainStep, -0.05, 0.12);
        snapshot.FrequencyWeightBase = Math.Clamp(snapshot.FrequencyWeightBase, 0.2, 2.5);
        snapshot.FrequencyWeightSlope = Math.Clamp(snapshot.FrequencyWeightSlope, -0.1, 0.2);
        snapshot.BackendAttack = Math.Clamp(snapshot.BackendAttack, 0.05, 1);
        snapshot.BackendRelease = Math.Clamp(snapshot.BackendRelease, 0.02, 1);

        lock (_sync)
        {
            var resetPeak = _lastPacketTick is null || snapshot.PeakInitial != _tuningSettings.PeakInitial;
            _signalGeneration++;
            _tuningSettings = snapshot;
            if (_smoothedBars.Length != snapshot.BarCount)
            {
                _smoothedBars = new float[snapshot.BarCount];
            }
            _adaptivePeak = Math.Clamp(resetPeak ? (float)snapshot.PeakInitial : _adaptivePeak,
                (float)snapshot.PeakFloor, (float)snapshot.PeakCeiling);
        }
    }

    public void Start()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_captureCancellation is not null)
            {
                return;
            }

            lock (_sync)
            {
                ResetSignal();
            }
            _captureCancellation = new CancellationTokenSource();
            var cancellationToken = _captureCancellation.Token;
            _captureThread = new Thread(() => CaptureLoop(cancellationToken))
            {
                IsBackground = true,
                Name = "TaskbarLyrics Audio Spectrum"
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }
    }

    public void Stop()
    {
        lock (_lifecycleSync)
        {
            StopCaptureSession();
        }
    }

    public float[] GetSpectrum() => GetSpectrum(Environment.TickCount64);

    internal float[] GetSpectrum(long now, Action? beforeCommit = null)
    {
        // One frame owns the scratch buffers from sample copy to bar commit. A concurrent or
        // re-entrant call takes a fresh set instead of interleaving with a frame in flight.
        var scratch = Interlocked.Exchange(ref _idleScratch, null) ?? new SpectrumScratch();
        try
        {
            int sampleRate;
            int generation;
            float[][] samples;
            SpectrumTuningSettings settings;

            lock (_sync)
            {
                // Capture the immutable settings reference with the sample window so the whole
                // frame uses one generation of tuning, exactly like the previous Clone snapshot.
                settings = _tuningSettings;
                var sampleWindow = CoerceSampleWindow(settings.SampleWindow);
                sampleRate = _sampleRate;
                if (_lastPacketTick is { } lastPacket && now - lastPacket >= PacketExpiryMs)
                {
                    foreach (var channel in _ringBuffers) Array.Clear(channel);
                    _lastPacketTick = null;
                    _diagnosticInputPeak = 0;
                    _signalGeneration++;
                }

                generation = _signalGeneration;
                scratch.Prepare(_ringBuffers.Length, sampleWindow);
                samples = scratch.Samples;
                var start = (_writeIndex - sampleWindow + RingBufferSize) % RingBufferSize;
                for (var channel = 0; channel < samples.Length; channel++)
                {
                    for (var i = 0; i < sampleWindow; i++)
                    {
                        samples[channel][i] = _ringBuffers[channel][(start + i) % RingBufferSize];
                    }
                }
            }

            var magnitudes = CalculateFftMagnitudes(scratch, samples);
            // The internal callback lets the signal test reproduce a reset during FFT
            // without starting a real audio endpoint or relying on thread timing.
            beforeCommit?.Invoke();
            float[] bars;
            lock (_sync)
            {
                if (generation != _signalGeneration) return (float[])_smoothedBars.Clone();
                bars = CalculateBars(magnitudes, samples[0].Length, sampleRate, settings, scratch);
                if (_smoothedBars.Length != bars.Length)
                {
                    _smoothedBars = new float[bars.Length];
                }

                for (var i = 0; i < bars.Length; i++)
                {
                    var attack = bars[i] > _smoothedBars[i]
                        ? (float)settings.BackendAttack
                        : (float)settings.BackendRelease;
                    _smoothedBars[i] += (bars[i] - _smoothedBars[i]) * attack;
                    bars[i] = Math.Clamp(_smoothedBars[i], 0f, 1f);
                }
            }

            // The coordinator stores the returned array for later frames and the diagnostics
            // snapshot outlives the call, so the reused bar buffer is copied once per frame.
            return (float[])bars.Clone();
        }
        finally
        {
            Volatile.Write(ref _idleScratch, scratch);
        }
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                RunCaptureSession(cancellationToken);
                _consecutiveCaptureFailures = 0;
            }
            catch (Exception ex)
            {
                _isAvailable = false;
                lock (_sync)
                {
                    _lastError = $"{ex.GetType().Name}: {ex.Message}";
                }

                var failures = ++_consecutiveCaptureFailures;
                var shift = Math.Clamp(failures - 1, 0, 5);
                var delayMs = Math.Min(CaptureRetryBaseDelayMs << shift, CaptureRetryMaxDelayMs);
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delayMs));
                continue;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private void RunCaptureSession(CancellationToken cancellationToken)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr mixFormatPtr = IntPtr.Zero;

        try
        {
            // Every endpoint/retry starts fresh, even if its format is unchanged.
            lock (_sync)
            {
                ResetSignal();
            }
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device));
            var activeDeviceId = GetDeviceId(device);
            var audioClientId = IAudioClientId;
            Marshal.ThrowExceptionForHR(device.Activate(ref audioClientId, ClsctxAll, IntPtr.Zero, out var audioClientObject));
            audioClient = (IAudioClient)audioClientObject;
            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out mixFormatPtr));
            var format = AudioFormat.FromWaveFormat(mixFormatPtr);
            lock (_sync)
            {
                _sampleRate = format.SampleRate;
                _diagnosticSampleRate = format.SampleRate;
                _diagnosticChannels = format.Channels;
                _diagnosticFormat = $"{format.SampleRate} Hz, {format.Channels} ch, {format.BytesPerSample * 8}-bit" +
                    (format.IsFloat ? " float" : " pcm");
            }

            Marshal.ThrowExceptionForHR(audioClient.Initialize(
                AudclntSharemodeShared,
                AudclntStreamflagsLoopback,
                1_000_000,
                0,
                mixFormatPtr,
                IntPtr.Zero));

            var audioCaptureClientId = IAudioCaptureClientId;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref audioCaptureClientId, out var captureClientObject));
            captureClient = (IAudioCaptureClient)captureClientObject;
            Marshal.ThrowExceptionForHR(audioClient.Start());
            _isAvailable = true;
            lock (_sync)
            {
                _lastError = string.Empty;
            }

            var nextDefaultDeviceCheck = Environment.TickCount64 + DefaultDeviceCheckIntervalMs;
            while (!cancellationToken.IsCancellationRequested)
            {
                DrainCaptureClient(captureClient, format);
                var now = Environment.TickCount64;
                if (now >= nextDefaultDeviceCheck)
                {
                    nextDefaultDeviceCheck = now + DefaultDeviceCheckIntervalMs;
                    if (HasDefaultRenderDeviceChanged(enumerator, activeDeviceId))
                    {
                        break;
                    }
                }

                cancellationToken.WaitHandle.WaitOne(15);
            }
        }
        finally
        {
            _isAvailable = false;
            try
            {
                audioClient?.Stop();
            }
            catch
            {
            }

            if (mixFormatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormatPtr);
            }

            ReleaseCom(captureClient);
            ReleaseCom(audioClient);
            ReleaseCom(device);
            ReleaseCom(enumerator);
        }
    }

    private static bool HasDefaultRenderDeviceChanged(
        IMMDeviceEnumerator enumerator,
        string activeDeviceId)
    {
        IMMDevice? defaultDevice = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(
                EDataFlow.Render,
                ERole.Console,
                out defaultDevice));
            var defaultDeviceId = GetDeviceId(defaultDevice);
            return !string.Equals(defaultDeviceId, activeDeviceId, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ReleaseCom(defaultDevice);
        }
    }

    private static string GetDeviceId(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var idPointer));
        try
        {
            return Marshal.PtrToStringUni(idPointer) ?? string.Empty;
        }
        finally
        {
            if (idPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(idPointer);
            }
        }
    }

    private void DrainCaptureClient(IAudioCaptureClient captureClient, AudioFormat format)
    {
        Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out var packetFrames));
        while (packetFrames > 0)
        {
            Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                out var data,
                out var frameCount,
                out var flags,
                out _,
                out _));

            try
            {
                if ((flags & AudclntBufferflagsSilent) != 0)
                {
                    AppendSilence(frameCount, Environment.TickCount64);
                }
                else
                {
                    AppendSamples(data, frameCount, format);
                }
            }
            finally
            {
                Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frameCount));
            }

            Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetFrames));
        }
    }

    internal void AppendSilence(int frameCount, long packetTick)
    {
        lock (_sync)
        {
            if (frameCount > 0) _lastPacketTick = packetTick;
            _diagnosticInputPeak = 0;
            for (var i = 0; i < frameCount; i++)
            {
                foreach (var channel in _ringBuffers) channel[_writeIndex] = 0;
                _writeIndex = (_writeIndex + 1) % RingBufferSize;
            }
        }
    }

    private void AppendSamples(IntPtr data, int frameCount, AudioFormat format)
    {
        var byteCount = frameCount * format.BlockAlign;
        var bytes = new byte[byteCount];
        Marshal.Copy(data, bytes, 0, byteCount);
        AppendSamples(bytes, frameCount, format, Environment.TickCount64);
    }

    internal void AppendSamples(byte[] bytes, int frameCount, AudioFormat format, long packetTick)
    {
        lock (_sync)
        {
            if (_ringBuffers.Length != format.Channels || _sampleRate != format.SampleRate)
            {
                _ringBuffers = Enumerable.Range(0, format.Channels).Select(_ => new float[RingBufferSize]).ToArray();
                ResetSignal();
            }
            _sampleRate = format.SampleRate;
            if (frameCount > 0) _lastPacketTick = packetTick;
            var peak = 0f;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var frameOffset = frame * format.BlockAlign;
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sample = ReadSample(bytes, frameOffset + (channel * format.BytesPerSample), format);
                    peak = Math.Max(peak, Math.Abs(sample));
                    _ringBuffers[channel][_writeIndex] = sample;
                }

                _writeIndex = (_writeIndex + 1) % RingBufferSize;
            }

            _diagnosticInputPeak = peak;
            if (peak > 0.0001f)
            {
                _lastAudioUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private static float ReadSample(byte[] bytes, int offset, AudioFormat format)
    {
        if (format.IsFloat && format.BytesPerSample >= 4)
        {
            return Math.Clamp(BitConverter.ToSingle(bytes, offset), -1f, 1f);
        }

        return format.BytesPerSample switch
        {
            2 => Math.Clamp(BitConverter.ToInt16(bytes, offset) / 32768f, -1f, 1f),
            3 => Read24BitSample(bytes, offset),
            4 => Math.Clamp(BitConverter.ToInt32(bytes, offset) / 2147483648f, -1f, 1f),
            _ => 0f
        };
    }

    private static float Read24BitSample(byte[] bytes, int offset)
    {
        var sample = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
        if ((sample & 0x800000) != 0)
        {
            sample |= unchecked((int)0xFF000000);
        }

        return Math.Clamp(sample / 8388608f, -1f, 1f);
    }

    private float[] CalculateBars(
        float[] magnitudes,
        int sampleWindow,
        int sampleRate,
        SpectrumTuningSettings settings,
        SpectrumScratch scratch)
    {
        var barCount = Math.Clamp(
            settings.BarCount,
            SpectrumTuningSettings.MinBarCount,
            SpectrumTuningSettings.MaxBarCount);
        var bars = scratch.GetBars(barCount);
        var nyquist = Math.Max(1000, sampleRate / 2);
        var maximumAvailableFrequency = Math.Min(SpectrumTuningSettings.MaximumFrequency, nyquist * 0.92);
        var maxFrequency = Math.Clamp(
            settings.MaxFrequency,
            SpectrumTuningSettings.MinimumFrequency + SpectrumTuningSettings.MinimumFrequencySpan,
            maximumAvailableFrequency);
        var minFrequency = Math.Clamp(
            settings.MinFrequency,
            SpectrumTuningSettings.MinimumFrequency,
            maxFrequency - SpectrumTuningSettings.MinimumFrequencySpan);

        var peak = 0f;
        var distributionExponent = Math.Pow(2.0, Math.Clamp(settings.FrequencyDistributionBias, -1, 1));

        for (var band = 0; band < barCount; band++)
        {
            var startRatio = band / (double)barCount;
            var endRatio = (band + 1) / (double)barCount;
            var mappedStartRatio = Math.Pow(startRatio, distributionExponent);
            var mappedEndRatio = Math.Pow(endRatio, distributionExponent);
            var startFrequency = minFrequency * Math.Pow(maxFrequency / minFrequency, mappedStartRatio);
            var endFrequency = minFrequency * Math.Pow(maxFrequency / minFrequency, mappedEndRatio);
            var startBin = Math.Max(1, (int)Math.Floor(startFrequency * sampleWindow / sampleRate));
            var endBin = Math.Min(magnitudes.Length - 1, (int)Math.Ceiling(endFrequency * sampleWindow / sampleRate));
            var weightedPower = 0.0;
            var weightTotal = 0.0;

            for (var bin = startBin; bin <= endBin; bin++)
            {
                var frequency = bin * sampleRate / (double)sampleWindow;
                var frequencyWeight = settings.FrequencyWeightBase +
                    (Math.Log2(Math.Max(2, frequency) / minFrequency) * settings.FrequencyWeightSlope);
                var weightedMagnitude = magnitudes[bin] * Math.Max(0.75, frequencyWeight);
                weightedPower += weightedMagnitude * weightedMagnitude;
                weightTotal += 1.0;
            }

            // RMS retains narrow note energy without the saturation of a peak-only band.
            var magnitude = weightTotal > 0 ? (float)Math.Sqrt(weightedPower / weightTotal) : 0f;
            var bandGain = (float)(settings.LowBandGain + (band * settings.BandGainStep));
            bars[band] = magnitude * bandGain;
            peak = Math.Max(peak, bars[band]);
        }

        _adaptivePeak = Math.Clamp(
            Math.Max(peak, _adaptivePeak * (float)settings.PeakDecay),
            (float)settings.PeakFloor,
            (float)settings.PeakCeiling);
        for (var band = 0; band < barCount; band++)
        {
            var normalized = Math.Clamp(bars[band] / _adaptivePeak, 0f, 1.35f);
            var noiseFloor = (float)settings.NoiseFloor;
            normalized = MathF.Max(0, normalized - noiseFloor) / MathF.Max(0.001f, 1 - noiseFloor);
            bars[band] = Math.Clamp(MathF.Pow(normalized, (float)settings.OutputCurve), 0f, 1f);
        }

        return bars;
    }

    private static int CoerceSampleWindow(int value)
    {
        return value switch
        {
            <= 512 => 512,
            <= 1024 => 1024,
            _ => 2048
        };
    }

    private static double[] GetHannWindow(int length) =>
        HannWindows.GetOrAdd(length, static size => CreateHannWindow(size));

    private static double[] CreateHannWindow(int length)
    {
        var window = new double[length];
        for (var i = 0; i < length; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos((2.0 * Math.PI * i) / (length - 1)));
        }

        return window;
    }

    private float[] CalculateFftMagnitudes(SpectrumScratch scratch, float[][] channels)
    {
        FootprintMetrics.NoteFft();
        var length = channels[0].Length;
        var powers = scratch.Powers;
        var real = scratch.Real;
        var imaginary = scratch.Imaginary;
        var window = scratch.HannWindow;
        // Reused buffers: the power accumulator must start at zero, bin 0 stays unused and
        // the window is the cached table for this length instead of a per-sample cosine.
        Array.Clear(powers);
        foreach (var samples in channels)
        {
            // Only the imaginary part needs a reset per channel; real is fully rewritten.
            Array.Clear(imaginary);
            for (var i = 0; i < length; i++)
            {
                real[i] = samples[i] * window[i];
            }

            FastFourierTransform(real, imaginary);
            for (var i = 1; i < powers.Length; i++)
            {
                powers[i] += (real[i] * real[i]) + (imaginary[i] * imaginary[i]);
            }
        }

        // Combine power after FFT so channel phase cannot cancel or create harmonics.
        var magnitudes = scratch.Magnitudes;
        var scale = 2.0 / length;
        magnitudes[0] = 0f;
        for (var i = 1; i < magnitudes.Length; i++)
        {
            magnitudes[i] = (float)(Math.Sqrt(powers[i] / channels.Length) * scale);
        }

        return magnitudes;
    }

    private static void FastFourierTransform(double[] real, double[] imaginary)
    {
        var length = real.Length;
        var j = 0;
        for (var i = 1; i < length; i++)
        {
            var bit = length >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var size = 2; size <= length; size <<= 1)
        {
            var halfSize = size >> 1;
            var angle = -2.0 * Math.PI / size;
            var phaseStepReal = Math.Cos(angle);
            var phaseStepImaginary = Math.Sin(angle);

            for (var offset = 0; offset < length; offset += size)
            {
                var phaseReal = 1.0;
                var phaseImaginary = 0.0;
                for (var i = 0; i < halfSize; i++)
                {
                    var even = offset + i;
                    var odd = even + halfSize;
                    var oddReal = (real[odd] * phaseReal) - (imaginary[odd] * phaseImaginary);
                    var oddImaginary = (real[odd] * phaseImaginary) + (imaginary[odd] * phaseReal);

                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;

                    var nextPhaseReal = (phaseReal * phaseStepReal) - (phaseImaginary * phaseStepImaginary);
                    phaseImaginary = (phaseReal * phaseStepImaginary) + (phaseImaginary * phaseStepReal);
                    phaseReal = nextPhaseReal;
                }
            }
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCaptureSession();
        }

        GC.SuppressFinalize(this);
    }

    private void StopCaptureSession()
    {
        var cancellation = _captureCancellation;
        var captureThread = _captureThread;
        _captureCancellation = null;
        _captureThread = null;

        cancellation?.Cancel();
        if (captureThread is not null && captureThread.IsAlive)
        {
            captureThread.Join(500);
        }

        if (captureThread is null || !captureThread.IsAlive)
        {
            cancellation?.Dispose();
        }
        _isAvailable = false;
        _consecutiveCaptureFailures = 0;
        lock (_sync)
        {
            ResetSignal();
            _diagnosticSampleRate = 0;
            _diagnosticChannels = 0;
            _diagnosticInputPeak = 0;
            _lastAudioUtc = null;
            _diagnosticFormat = "Waiting";
            _lastError = string.Empty;
        }
    }

    private void ResetSignal()
    {
        _signalGeneration++;
        foreach (var channel in _ringBuffers) Array.Clear(channel);
        Array.Clear(_smoothedBars);
        _writeIndex = 0;
        _lastPacketTick = null;
        _diagnosticInputPeak = 0;
        _adaptivePeak = Math.Clamp((float)_tuningSettings.PeakInitial,
            (float)_tuningSettings.PeakFloor, (float)_tuningSettings.PeakCeiling);
    }

    // Per-frame scratch buffers. The shape fields make sure the channel, window, power,
    // magnitude and bar arrays are only reallocated when the audio format changes.
    private sealed class SpectrumScratch
    {
        internal float[][] Samples = [];
        internal double[] Powers = [];
        internal double[] Real = [];
        internal double[] Imaginary = [];
        internal float[] Magnitudes = [];
        internal float[] Bars = [];
        internal double[] HannWindow = [];
        internal int WindowLength;

        internal void Prepare(int channelCount, int windowLength)
        {
            if (WindowLength != windowLength)
            {
                WindowLength = windowLength;
                HannWindow = GetHannWindow(windowLength);
                var binCount = (windowLength / 2) + 1;
                Powers = new double[binCount];
                Real = new double[windowLength];
                Imaginary = new double[windowLength];
                Magnitudes = new float[binCount];
                Samples = [];
            }

            if (Samples.Length == channelCount)
            {
                return;
            }

            var samples = new float[channelCount][];
            for (var channel = 0; channel < channelCount; channel++)
            {
                samples[channel] = new float[windowLength];
            }

            Samples = samples;
        }

        internal float[] GetBars(int barCount)
        {
            if (Bars.Length != barCount)
            {
                Bars = new float[barCount];
            }

            return Bars;
        }
    }

    internal sealed class AudioFormat
    {
        internal AudioFormat(int channels, int sampleRate, int blockAlign, int bytesPerSample, bool isFloat)
        {
            Channels = channels;
            SampleRate = sampleRate;
            BlockAlign = blockAlign;
            BytesPerSample = bytesPerSample;
            IsFloat = isFloat;
        }

        public int Channels { get; }
        public int SampleRate { get; }
        public int BlockAlign { get; }
        public int BytesPerSample { get; }
        public bool IsFloat { get; }

        public static AudioFormat FromWaveFormat(IntPtr ptr)
        {
            var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(ptr);
            var isFloat = waveFormat.FormatTag == WaveFormatIeeeFloat;

            if (waveFormat.FormatTag == WaveFormatExtensibleTag && waveFormat.ExtraSize >= 22)
            {
                var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(ptr);
                isFloat = extensible.SubFormat == IeeeFloatSubFormat;
            }

            return new AudioFormat(
                waveFormat.Channels,
                waveFormat.SamplesPerSec,
                waveFormat.BlockAlign,
                Math.Max(1, waveFormat.BitsPerSample / 8),
                isFloat);
        }
    }

    private enum EDataFlow
    {
        Render,
        Capture,
        All
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId(out IntPtr id);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        int GetBufferSize(out int bufferFrames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out int paddingFrames);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr deviceFormat);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        int ReleaseBuffer(int frames);
        int GetNextPacketSize(out int frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public short ValidBitsPerSample;
        public int ChannelMask;
        public Guid SubFormat;
    }
}
