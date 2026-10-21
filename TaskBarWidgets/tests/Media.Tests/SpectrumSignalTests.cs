using TaskbarLyrics.App;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class SpectrumSignalTests
{
    private const int SampleRate = 48000;
    private const int SampleWindow = 2048;
    private const long PacketTick = 1000;

    [Fact]
    public void IdenticalAndInvertedStereoRetainMonoSpectrumAndInputPeak()
    {
        var tone = Tone(750, 0.01);
        using var mono = CreateService();
        using var identical = CreateService();
        using var inverted = CreateService();
        Feed(mono, PacketTick, tone);
        Feed(identical, PacketTick, tone, tone);
        Feed(inverted, PacketTick, tone, tone.Select(value => -value).ToArray());

        var expected = mono.GetSpectrum(PacketTick);
        Assert.True(expected.Max() > 0.001f);
        AssertBarsEqual(expected, identical.GetSpectrum(PacketTick));
        AssertBarsEqual(expected, inverted.GetSpectrum(PacketTick));
        Assert.Equal(mono.GetDiagnostics().InputPeak, inverted.GetDiagnostics().InputPeak);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneActiveStereoChannelRetainsItsShareOfSpectralPower(bool rightChannel)
    {
        var tone = Tone(750, 0.01);
        var silence = new float[SampleWindow];
        using var mono = CreateService();
        using var stereo = CreateService();
        Feed(mono, PacketTick, tone);
        Feed(stereo, PacketTick, rightChannel ? silence : tone, rightChannel ? tone : silence);

        var expected = mono.GetSpectrum(PacketTick).Select(value => value / MathF.Sqrt(2)).ToArray();
        AssertBarsEqual(expected, stereo.GetSpectrum(PacketTick));
        Assert.Equal(mono.GetDiagnostics().InputPeak, stereo.GetDiagnostics().InputPeak);
    }

    [Fact]
    public void RotatingDifferentChannelTonesPreservesSpectrumWithoutNewHarmonics()
    {
        var left = Tone(750, 0.01);
        var right = Tone(2250, 0.006);
        var sum = left.Zip(right, (a, b) => (a + b) / MathF.Sqrt(2)).ToArray();
        var difference = left.Zip(right, (a, b) => (a - b) / MathF.Sqrt(2)).ToArray();
        using var original = CreateService();
        using var rotated = CreateService();
        Feed(original, PacketTick, left, right);
        Feed(rotated, PacketTick, sum, difference);

        AssertBarsEqual(original.GetSpectrum(PacketTick), rotated.GetSpectrum(PacketTick));
    }

    [Fact]
    public void QuietAmplitudeChangesSurviveDefaultCurveAndPeakFloor()
    {
        using var quiet = CreateService(SpectrumTuningSettings.CreateDefault());
        using var louder = CreateService(SpectrumTuningSettings.CreateDefault());
        Feed(quiet, PacketTick, Tone(750, 0.001));
        Feed(louder, PacketTick, Tone(750, 0.002));

        // Let the default adaptive peak reach its floor while the packet is still fresh.
        for (var frame = 0; frame < 20; frame++)
        {
            quiet.GetSpectrum(PacketTick + (frame * 10));
            louder.GetSpectrum(PacketTick + (frame * 10));
        }
        var quietPeak = quiet.GetSpectrum(PacketTick + 200).Max();
        var louderPeak = louder.GetSpectrum(PacketTick + 200).Max();
        Assert.InRange(quietPeak, 0.000001f, 0.01f);
        Assert.InRange(louderPeak / quietPeak, 2.82f, 2.84f);
    }

    [Theory]
    [InlineData(440, 7.5, 21.5)]
    [InlineData(1760, 5.0, 14.5)]
    [InlineData(3000, 3.5, 10.5)]
    [InlineData(0, 5.3, 15.5)]
    public void QuietNotesProduceVisibleHeightChangesWithDefaultTuning(
        double frequency, double minimumQuietExcursion, double minimumLouderExcursion)
    {
        var waveform = frequency == 0 ? PianoTone() : Tone(frequency, 1);
        using var quiet = CreateService(SpectrumTuningSettings.CreateDefault());
        using var louder = CreateService(SpectrumTuningSettings.CreateDefault());
        Feed(quiet, PacketTick, waveform.Select(value => value * 0.01f).ToArray());
        Feed(louder, PacketTick, waveform.Select(value => value * 0.02f).ToArray());
        for (var frame = 0; frame < 20; frame++)
        {
            quiet.GetSpectrum(PacketTick + (frame * 10));
            louder.GetSpectrum(PacketTick + (frame * 10));
        }

        var quietExcursion = 26.0 * quiet.GetSpectrum(PacketTick + 200).Max();
        var louderExcursion = 26.0 * louder.GetSpectrum(PacketTick + 200).Max();
        Assert.InRange(quietExcursion, minimumQuietExcursion, 26);
        Assert.InRange(louderExcursion, minimumLouderExcursion, 26);
        Assert.True(louderExcursion - quietExcursion > 5);
    }

    [Fact]
    public void MissingPacketsExpireAndReleaseToSilenceThenFreshAudioRecovers()
    {
        var tuning = LinearTuning();
        tuning.BackendRelease = 0.5;
        using var service = CreateService(tuning);
        var tone = Tone(750, 0.01);
        Feed(service, PacketTick, tone);
        var fresh = service.GetSpectrum(PacketTick + 249);
        Assert.True(fresh.Max() > 0.001f);
        Assert.True(service.GetDiagnostics().InputPeak > 0);

        var expired = service.GetSpectrum(PacketTick + 250);
        AssertBarsEqual(fresh.Select(value => value * 0.5f).ToArray(), expired);
        Assert.Equal(0f, service.GetDiagnostics().InputPeak);
        AssertBarsEqual(expired.Select(value => value * 0.5f).ToArray(), service.GetSpectrum(PacketTick + 300));

        Feed(service, PacketTick + 301, tone);
        AssertBarsEqual(fresh, service.GetSpectrum(PacketTick + 301));
    }

    [Fact]
    public void ExplicitSilentPacketClearsEveryChannel()
    {
        using var service = CreateService();
        var tone = Tone(750, 0.01);
        Feed(service, PacketTick, tone, tone.Select(value => -value).ToArray());
        Assert.True(service.GetSpectrum(PacketTick).Max() > 0.001f);

        service.AppendSilence(SampleWindow, PacketTick + 1);
        Assert.All(service.GetSpectrum(PacketTick + 1), value => Assert.Equal(0f, value));
        Assert.Equal(0f, service.GetDiagnostics().InputPeak);
    }

    [Fact]
    public void StopResetsAdaptivePeakToConfiguredInitialValue()
    {
        var tuning = LinearTuning();
        tuning.PeakInitial = 0.02;
        tuning.PeakFloor = 0.003;
        tuning.PeakCeiling = 1;
        tuning.PeakDecay = 0.995;
        using var service = CreateService(tuning);
        var quiet = Tone(750, 0.001);
        Feed(service, PacketTick, quiet);
        var expected = service.GetSpectrum(PacketTick);
        Feed(service, PacketTick + 1, Tone(750, 0.8));
        service.GetSpectrum(PacketTick + 1);

        service.Stop();
        Feed(service, PacketTick + 2, quiet);
        AssertBarsEqual(expected, service.GetSpectrum(PacketTick + 2));
    }

    [Fact]
    public void ChangingPeakInitialAppliesToTheNextQuietFrame()
    {
        var tuning = LinearTuning();
        tuning.PeakInitial = 0.2;
        tuning.PeakFloor = 0.003;
        tuning.PeakCeiling = 1;
        using var service = CreateService(tuning);
        Feed(service, PacketTick, Tone(750, 0.001));
        var before = service.GetSpectrum(PacketTick).Max();

        tuning.PeakInitial = 0.02;
        service.ApplyTuning(tuning);
        var after = service.GetSpectrum(PacketTick).Max();
        Assert.InRange(after / before, 9.99f, 10.01f);
    }

    [Fact]
    public void ResetDuringFftCannotRestoreOldBarsOrOverwriteNewAdaptivePeak()
    {
        using var service = CreateService(SpectrumTuningSettings.CreateDefault());
        using var fresh = CreateService(SpectrumTuningSettings.CreateDefault());
        Feed(service, PacketTick, Tone(750, 0.8));
        var obsolete = service.GetSpectrum(PacketTick, beforeCommit: service.Stop);
        Assert.All(obsolete, value => Assert.Equal(0f, value));
        var quiet = Tone(750, 0.001);
        Feed(service, PacketTick + 1, quiet);
        Feed(fresh, PacketTick + 1, quiet);
        AssertBarsEqual(fresh.GetSpectrum(PacketTick + 1), service.GetSpectrum(PacketTick + 1));
    }

    [Fact]
    public void TuningDuringFftCannotRestoreOldBarCount()
    {
        using var service = CreateService();
        Feed(service, PacketTick, Tone(750, 0.01));
        var tuning = LinearTuning();
        tuning.BarCount = 8;
        var bars = service.GetSpectrum(PacketTick, beforeCommit: () => service.ApplyTuning(tuning));
        Assert.Equal(8, bars.Length);
        Assert.All(bars, value => Assert.Equal(0f, value));
        Assert.Equal(8, service.GetSpectrum(PacketTick + 1).Length);
    }

    private static SpectrumTuningSettings LinearTuning() => new()
    {
        PeakInitial = 0.08, PeakFloor = 0.08, PeakCeiling = 0.08,
        BackendAttack = 1, BackendRelease = 1, OutputCurve = 1,
        LowBandGain = 1, BandGainStep = 0, FrequencyWeightBase = 1, FrequencyWeightSlope = 0
    };

    private static SystemAudioSpectrumService CreateService(SpectrumTuningSettings? tuning = null)
    {
        var service = new SystemAudioSpectrumService();
        service.ApplyTuning(tuning ?? LinearTuning());
        return service;
    }

    private static float[] Tone(double frequency, double amplitude) => Enumerable.Range(0, SampleWindow)
        .Select(index => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * index / SampleRate))).ToArray();

    private static float[] PianoTone()
    {
        var samples = Enumerable.Range(0, SampleWindow).Select(index =>
        {
            var phase = 2 * Math.PI * 440 * index / SampleRate;
            return (float)((0.6 * Math.Sin(phase)) + (0.25 * Math.Sin(phase * 2)) +
                (0.1 * Math.Sin(phase * 3)) + (0.05 * Math.Sin(phase * 4)));
        }).ToArray();
        var peak = samples.Max(value => Math.Abs(value));
        return samples.Select(value => value / peak).ToArray();
    }

    private static void Feed(SystemAudioSpectrumService service, long tick, params float[][] channels)
    {
        var samples = new float[SampleWindow * channels.Length];
        for (var frame = 0; frame < SampleWindow; frame++)
            for (var channel = 0; channel < channels.Length; channel++)
                samples[(frame * channels.Length) + channel] = channels[channel][frame];
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var format = new SystemAudioSpectrumService.AudioFormat(channels.Length, SampleRate,
            channels.Length * sizeof(float), sizeof(float), true);
        service.AppendSamples(bytes, SampleWindow, format, tick);
    }

    private static void AssertBarsEqual(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - actual[i]), 0f, 0.000001f);
    }
}
