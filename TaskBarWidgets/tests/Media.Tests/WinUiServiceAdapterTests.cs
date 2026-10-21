using System.IO;
using System.Text.Json;
using TaskbarLyrics.App;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class WinUiServiceAdapterTests
{
    [Fact]
    public void HeadlessSettingsServiceReadsAndAppliesWithoutCreatingAWindow()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tbw-settings-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, "media-settings.json");
            var store = new SettingsStore(settingsPath);
            using var service = MediaSettingsServiceTestFactory.Create(
                new AppSettings(),
                saveSettings: settings => store.Save(settings),
                getDisplays: () => [],
                getPrimaryDpiScale: () => 1,
                getWorkAreaWidth: () => 1920);

            Assert.False(typeof(MediaSettingsService).IsSubclassOf(typeof(System.Windows.Window)));

            var payload = service.ReadServiceSettings(new AppSettings
            {
                ShowLyricTranslation = true,
                CardMicaVivid = false,
                CardMicaTintOpacity = 0.08,
                CardMicaLuminosityOpacity = 0.30,
                CardMicaDarkTintOpacity = 0.06,
                CardMicaDarkLuminosityOpacity = 0.28
            });
            Assert.True(payload.GetProperty("showLyricTranslation").GetBoolean());
            Assert.False(payload.GetProperty("cardMicaVivid").GetBoolean());
            Assert.Equal(0.08, payload.GetProperty("cardMicaTintOpacity").GetDouble());
            Assert.Equal(0.30, payload.GetProperty("cardMicaLuminosityOpacity").GetDouble());
            Assert.Equal(0.06, payload.GetProperty("cardMicaDarkTintOpacity").GetDouble());
            Assert.Equal(0.28, payload.GetProperty("cardMicaDarkLuminosityOpacity").GetDouble());
            Assert.True(payload.TryGetProperty("spectrumTuning", out _));
            Assert.False(payload.TryGetProperty("mediaHotkeys", out _));
            Assert.False(payload.TryGetProperty("enableGlobalMediaHotkeys", out _));
            Assert.False(payload.TryGetProperty("enableControlPanel", out _));
            Assert.False(payload.TryGetProperty("syncDefaultCommunicationsDevice", out _));

            var patch = JsonSerializer.SerializeToElement(new
            {
                showLyricTranslation = true,
                cardMicaVivid = false,
                cardMicaTintOpacity = 0.12,
                cardMicaLuminosityOpacity = 0.34
            });
            var applied = service.ApplyServiceSettings(new AppSettings(), patch);
            Assert.False(applied.GetProperty("cardMicaVivid").GetBoolean());
            Assert.Equal(0.12, applied.GetProperty("cardMicaTintOpacity").GetDouble());
            Assert.Equal(0.34, applied.GetProperty("cardMicaLuminosityOpacity").GetDouble());

            var saved = new SettingsStore(settingsPath).Load();
            Assert.True(saved.ShowLyricTranslation);
            Assert.False(saved.CardMicaVivid);
            Assert.Equal(0.12, saved.CardMicaTintOpacity, 6);
            Assert.Equal(0.34, saved.CardMicaLuminosityOpacity, 6);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SafeCommandWhitelistRejectsUnknownOperations()
    {
        var cleared = false;
        using var service = MediaSettingsServiceTestFactory.Create(
            new AppSettings(),
            clearCache: () => cleared = true,
            getDisplays: () => [],
            getPrimaryDpiScale: () => 1,
            getWorkAreaWidth: () => 1920);

        var reply = await service.ExecuteServiceCommandAsync(
            new AppSettings(),
            "clearCache",
            null);
        Assert.True(cleared);
        Assert.True(reply.TryGetProperty("settings", out _));
        Assert.Equal(JsonValueKind.Array, reply.GetProperty("events").ValueKind);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ExecuteServiceCommandAsync(
                new AppSettings(),
                "openSettings",
                null));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ExecuteServiceCommandAsync(
                new AppSettings(),
                "runProcess",
                null));
    }

    [Fact]
    public void LegacyDisabledAudioAndEnabledShortcutsCannotReenableRemovedPreferences()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "removed-preferences-" + Guid.NewGuid().ToString("N"));
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

            using var service = MediaSettingsServiceTestFactory.Create(
                settings,
                saveSettings: _ => true,
                getDisplays: () => [],
                getPrimaryDpiScale: () => 1,
                getWorkAreaWidth: () => 1920);
            var payload = service.ReadServiceSettings(settings);
            Assert.True(payload.GetProperty("showLyricTranslation").GetBoolean());
            Assert.False(payload.TryGetProperty("mediaHotkeys", out _));
            Assert.False(payload.TryGetProperty("enableGlobalMediaHotkeys", out _));
            Assert.False(payload.TryGetProperty("enableControlPanel", out _));
            Assert.False(payload.TryGetProperty("syncDefaultCommunicationsDevice", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
