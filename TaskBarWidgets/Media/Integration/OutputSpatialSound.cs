using System.Diagnostics;
using Windows.Media.Audio;

namespace TaskBarWidgets.Media.Integration;

internal readonly record struct OutputSpatialSound(Guid Selected, Guid Active)
{
    // GetForDeviceId expects the render device interface path, not the MMDevice endpoint ID.
    private static SpatialAudioDeviceConfiguration Open(string endpointId) =>
        SpatialAudioDeviceConfiguration.GetForDeviceId(
            $@"\\?\SWD#MMDEVAPI#{endpointId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}");

    private static OutputSpatialSound Read(SpatialAudioDeviceConfiguration config) =>
        new(Guid.Parse(config.DefaultSpatialAudioFormat), Guid.Parse(config.ActiveSpatialAudioFormat));

    internal static OutputSpatialSound? TryRead(string endpointId)
    {
        try
        {
            var config = Open(endpointId);
            return config.IsSpatialAudioSupported ? Read(config) : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Spatial audio snapshot: {ex.GetType().Name}");
            return null;
        }
    }

    internal Guid? FormatToRestore(OutputSpatialSound current) =>
        current.Selected == Selected && (Active != Selected || current.Active == Active) ? null : Selected;

    internal static async Task<bool> PreserveAsync(string endpointId, OutputSpatialSound? before)
    {
        try
        {
            var config = Open(endpointId);
            if (!config.IsSpatialAudioSupported) return before is null || before.Value.Selected == Guid.Empty;
            var expected = before ?? new OutputSpatialSound(Guid.Empty, Guid.Empty);
            if (expected.FormatToRestore(Read(config)) is not Guid format) return true;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var change = await config.SetDefaultSpatialAudioFormatAsync(format.ToString("B"))
                .AsTask(timeout.Token).ConfigureAwait(false);
            if (change.Status != SetDefaultSpatialAudioFormatStatus.Succeeded) return false;
            if (expected.FormatToRestore(Read(Open(endpointId))) is null) return true;
            await Task.Delay(150).ConfigureAwait(false);
            return expected.FormatToRestore(Read(Open(endpointId))) is null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Spatial audio restore: {ex.GetType().Name}");
            return false;
        }
    }
}
