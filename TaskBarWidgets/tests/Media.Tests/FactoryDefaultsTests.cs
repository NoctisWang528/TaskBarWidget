using System.IO;
using TaskbarLyrics.App;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class FactoryDefaultsTests
{
    [Fact]
    public void SeedsOnlyWhenTheSettingsFileIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "media-settings.json");
            Assert.True(FactoryDefaults.EnsureSeeded(path));
            Assert.True(File.Exists(path));

            var seeded = File.ReadAllBytes(path);
            Assert.True(seeded.Length > 0);

            File.WriteAllText(path, "{\"FontSize\":42}");
            Assert.False(FactoryDefaults.EnsureSeeded(path));
            Assert.Equal("{\"FontSize\":42}", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SeedsADirectoryThatDoesNotExistYet()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-factory-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", "media-settings.json");
        try
        {
            Assert.True(FactoryDefaults.EnsureSeeded(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SeededSettingsUseSystemFontsAndStayAnonymous()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "media-settings.json");
            Assert.True(FactoryDefaults.EnsureSeeded(path));
            var settings = new SettingsStore(path).Load(systemUsesLightTheme: true);
            Assert.Equal("Microsoft YaHei UI", settings.FontFamily);
            Assert.Equal("Microsoft YaHei UI", settings.CardFontFamily);
            Assert.Empty(settings.LocalMusicFolders);
            Assert.Empty(settings.SelectedDisplayIds);
            Assert.Empty(settings.CustomPlayerSources);
            Assert.Null(settings.LastUpdateCheckUtc);
            Assert.False(settings.SpectrumAudioAccessGranted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
