using System.IO;
using System.Reflection;
using System.Text.Json;
using TaskbarLyrics.App;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class CommunicationsPreferenceTests
{
    [Fact]
    public void NewSettingsEnableCommunicationDeviceSync()
    {
        Assert.True(new AppSettings().SyncDefaultCommunicationsDevice);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"SyncDefaultCommunicationsDevice\":true}", true)]
    [InlineData("{\"SyncDefaultCommunicationsDevice\":false}", false)]
    public void StoredPreferencePreservesMissingDefaultAndExplicitChoice(string json, bool expected)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, $"communications-preference-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(filePath, json);
            var store = new SettingsStore(filePath);
            var loaded = store.Load();
            Assert.Equal(expected, loaded.SyncDefaultCommunicationsDevice);
            var clone = loaded.Clone();
            Assert.Equal(expected, clone.SyncDefaultCommunicationsDevice);
            clone.SyncDefaultCommunicationsDevice = !expected;
            Assert.Equal(expected, loaded.SyncDefaultCommunicationsDevice);
            Assert.True(store.Save(loaded));
            using var stored = JsonDocument.Parse(File.ReadAllText(filePath));
            Assert.Equal(expected, stored.RootElement.GetProperty(nameof(AppSettings.SyncDefaultCommunicationsDevice)).GetBoolean());
            Assert.Equal(expected, store.Load().SyncDefaultCommunicationsDevice);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SettingsCopyPreservesFalseAndResetRestoresEnabledDefault()
    {
        var copy = typeof(SettingsWindow).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var target = new AppSettings();
        copy.Invoke(null, [new AppSettings { SyncDefaultCommunicationsDevice = false }, target]);
        Assert.False(target.SyncDefaultCommunicationsDevice);
        copy.Invoke(null, [new AppSettings(), target]);
        Assert.True(target.SyncDefaultCommunicationsDevice);
    }

    [Fact]
    public void PreferenceChangeDoesNotRequestMediaOrLyricsReapplication()
    {
        var current = new AppSettings();
        var next = current.Clone();
        next.SyncDefaultCommunicationsDevice = false;
        var changes = AppSettingsChangeSet.Create(current, next);

        Assert.Equal(AppSettingsChangeSet.Create(current, current.Clone()), changes);
        Assert.False(changes.RequiresLyricsWindowApply);
    }
}
