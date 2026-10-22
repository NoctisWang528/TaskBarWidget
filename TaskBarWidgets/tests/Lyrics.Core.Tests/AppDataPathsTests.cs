using TaskbarLyrics.Core.Utilities;
using Xunit;

namespace TaskbarLyrics.Core.Tests;

public sealed class AppDataPathsTests
{
    [Fact]
    public void ApplicationRootAlwaysResolvesToItsDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-path-test-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.Combine(root, "Data"), AppDataPaths.ResolveForApplicationRoot(root));
    }

    [Fact]
    public void ConfiguredDataRootExposesTheSingleLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-data-test-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        try
        {
            AppDataPaths.ResetForTests();
            AppDataPaths.ConfigureForDataRoot(data);
            Assert.Equal(data, AppDataPaths.RootDirectory);
            Assert.Equal(Path.Combine(data, "config", "app.ini"), AppDataPaths.NativeConfigPath);
            Assert.Equal(Path.Combine(data, "lyrics"), AppDataPaths.LyricsDirectory);
            Assert.Equal(Path.Combine(data, "cache", "lyrics"), AppDataPaths.LyricCacheDirectory);
            AppDataPaths.EnsureDirectories();
            Assert.True(Directory.Exists(AppDataPaths.LogsDirectory));
        }
        finally
        {
            AppDataPaths.ResetForTests();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsAnUnrelatedDataDirectory()
    {
        AppDataPaths.ResetForTests();
        Assert.Throws<ArgumentException>(() =>
            AppDataPaths.ConfigureForDataRoot(Path.Combine(Path.GetTempPath(), "not-data")));
    }
}
