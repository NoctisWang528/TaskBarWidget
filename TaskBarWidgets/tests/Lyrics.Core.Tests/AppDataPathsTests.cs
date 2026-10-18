using TaskbarLyrics.Core.Utilities;
using Xunit;

namespace TaskbarLyrics.Core.Tests;

public sealed class AppDataPathsTests
{
    [Fact]
    public void NativeFirstRunAndUnwritableDirectoryDecisionOverridesIni()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.Combine(root, "Data"), AppDataPaths.ResolveForApplicationRoot(root, true));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "global_cfg.ini"), "[config]\nportable_mode=true\n");
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskBarWidgets"), AppDataPaths.ResolveForApplicationRoot(root, false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void PortableFlagSelectsOnlyPackageDataDirectory(string value, bool portable)
    {
        var root = Path.Combine(Path.GetTempPath(), "tbw-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "global_cfg.ini"), $"[other]\nportable_mode=true\n[config]\nportable_mode = {value}\n");
            var expected = portable ? Path.Combine(root, "Data") : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskBarWidgets");
            Assert.Equal(expected, AppDataPaths.ResolveForApplicationRoot(root));
            Assert.False(Directory.Exists(Path.Combine(root, "Data")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
