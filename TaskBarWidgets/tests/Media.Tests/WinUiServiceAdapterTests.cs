using System.Runtime.ExceptionServices;
using System.IO;
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
                var payload = controller.ReadServiceSettings(new AppSettings
                {
                    ShowLyricTranslation = true,
                    CardMicaTintOpacity = 0.08,
                    CardMicaLuminosityOpacity = 0.30
                });
                Assert.True(payload.GetProperty("showLyricTranslation").GetBoolean());
                Assert.Equal(0.08, payload.GetProperty("cardMicaTintOpacity").GetDouble());
                Assert.Equal(0.30, payload.GetProperty("cardMicaLuminosityOpacity").GetDouble());
                Assert.True(payload.TryGetProperty("spectrumTuning", out _));
                Assert.False(payload.TryGetProperty("mediaHotkeys", out _));
                Assert.False(payload.TryGetProperty("enableGlobalMediaHotkeys", out _));
                Assert.False(payload.TryGetProperty("enableControlPanel", out _));
                Assert.False(payload.TryGetProperty("syncDefaultCommunicationsDevice", out _));
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

    [Fact]
    public void LegacyDisabledAudioAndEnabledShortcutsCannotReenableRemovedPreferences()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "removed-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {"ShowLyricTranslation":true,"EnableControlPanel":false,"SyncDefaultCommunicationsDevice":false,"GlobalMediaHotkeys":{"Enabled":true,"TogglePlayPause":"Ctrl+Alt+P"}}
                """);
            var store = new SettingsStore(path);
            var settings = store.Load();
            Assert.True(settings.EnableControlPanel);
            Assert.True(settings.SyncDefaultCommunicationsDevice);
            Assert.True(settings.ShowLyricTranslation);
            Assert.True(store.Save(settings));
            var saved = File.ReadAllText(path);
            Assert.DoesNotContain("GlobalMediaHotkeys", saved);
            Assert.DoesNotContain("EnableControlPanel", saved);
            Assert.DoesNotContain("SyncDefaultCommunicationsDevice", saved);
            Assert.True(store.Load().ShowLyricTranslation);
        }
        finally { Directory.Delete(directory, true); }
    }
}
