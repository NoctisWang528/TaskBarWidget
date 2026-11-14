using System.IO;
using TaskbarLyrics.App;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

public sealed class CardMaterialSettingsTests
{
    [Theory]
    [InlineData(null, "Mica")]
    [InlineData("", "Mica")]
    [InlineData("FutureMaterial", "Mica")]
    [InlineData("Mica", "Mica")]
    [InlineData("LiquidGlass", "LiquidGlass")]
    [InlineData("Solid", "Solid")]
    public void MaterialIdsNormalizeAndClone(string? input, string expected)
    {
        var settings = new AppSettings { CardMaterial = input!, CardGlassBlur = 0.73 };
        settings.NormalizeQuickCards();
        Assert.Equal(expected, settings.CardMaterial);
        var clone = settings.Clone();
        Assert.Equal(expected, clone.CardMaterial);
        Assert.Equal(0.73, clone.CardGlassBlur);
        clone.CardMaterial = "Solid";
        Assert.Equal(expected, settings.CardMaterial);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(0.73, 0.73)]
    [InlineData(double.NaN, 0.35)]
    [InlineData(double.PositiveInfinity, 0.35)]
    [InlineData(double.NegativeInfinity, 0.35)]
    public void BlurNormalizesBeforeSave(double input, double expected)
    {
        var settings = new AppSettings { CardGlassBlur = input };
        settings.NormalizeQuickCards();
        Assert.Equal(expected, settings.CardGlassBlur);
    }

    [Fact]
    public void LegacyLoadDoesNotRewriteFileAndMaterialRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbw-material-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            const string legacy = "{\"CardMicaVivid\":false,\"CardMicaTintOpacity\":0.24}";
            File.WriteAllText(path, legacy);
            var store = new SettingsStore(path);
            var settings = store.Load();
            Assert.Equal("Mica", settings.CardMaterial);
            Assert.Equal(0.35, settings.CardGlassBlur);
            Assert.Equal(legacy, File.ReadAllText(path));
            settings.CardMaterial = "LiquidGlass";
            settings.CardGlassBlur = 0.67;
            Assert.True(store.Save(settings));
            var loaded = store.Load();
            Assert.Equal("LiquidGlass", loaded.CardMaterial);
            Assert.Equal(0.67, loaded.CardGlassBlur);
            Assert.False(loaded.CardMicaVivid);
            Assert.Equal(0.24, loaded.CardMicaTintOpacity);
            settings.CardMaterial = "Unknown";
            settings.CardGlassBlur = double.NaN;
            Assert.True(store.Save(settings));
            loaded = store.Load();
            Assert.Equal("Mica", loaded.CardMaterial);
            Assert.Equal(0.35, loaded.CardGlassBlur);
        }
        finally { Directory.Delete(directory, true); }
    }
}
