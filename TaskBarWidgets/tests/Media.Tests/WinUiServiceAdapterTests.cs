using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Interop;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class WinUiServiceAdapterTests
{
    [Fact]
    public void HeadlessSettingsAdapterReturnsSettingsAndSafeCommandWithoutCreatingWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var cleared = false;
                var controller = new SettingsWindow(new AppSettings(), null!,
                    () => Task.FromResult<CurrentTrackLyricsContext?>(null),
                    () => throw new NotSupportedException(), (_, _, _) => throw new NotSupportedException(),
                    (_, _, _) => throw new NotSupportedException(), () => cleared = true);
                var payload = controller.ReadServiceSettings(new AppSettings { ShowLyricTranslation = true });
                Assert.True(payload.GetProperty("showLyricTranslation").GetBoolean());
                Assert.True(payload.TryGetProperty("spectrumTuning", out _));
                var reply = controller.ExecuteServiceCommandAsync(new AppSettings(), "clearCache", null).GetAwaiter().GetResult();
                Assert.True(cleared);
                Assert.True(reply.TryGetProperty("settings", out _));
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(controller).Handle);
                controller.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Hidden service adapter did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
