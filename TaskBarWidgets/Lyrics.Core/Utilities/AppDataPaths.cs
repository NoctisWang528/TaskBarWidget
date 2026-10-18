namespace TaskbarLyrics.Core.Utilities;

/// <summary>Set once by the media host before settings, databases or lyric caches are created.</summary>
public static class AppDataPaths
{
    public static string RootDirectory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskBarWidgets");

    public static string DatabaseDirectory => Path.Combine(RootDirectory, "database");

    public static void ConfigureForApplicationRoot(string applicationRoot, bool? hostPortableMode = null) =>
        RootDirectory = ResolveForApplicationRoot(applicationRoot, hostPortableMode);

    public static string ResolveForApplicationRoot(string applicationRoot, bool? hostPortableMode = null)
    {
        var root = Path.GetFullPath(applicationRoot);
        var globalConfig = Path.Combine(root, "global_cfg.ini");
        var portable = false;
        if (File.Exists(globalConfig))
        {
            var inConfig = false;
            foreach (var raw in File.ReadLines(globalConfig))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line.StartsWith('['))
                {
                    inConfig = string.Equals(line, "[config]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                var separator = line.IndexOf('=');
                if (!inConfig || separator < 0 || !line[..separator].Trim().Equals("portable_mode", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(separator + 1)..].Trim();
                portable = value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    (int.TryParse(value, out var number) && number != 0);
            }
        }
        // The native host also accounts for first-run defaults and directory writability.
        return (hostPortableMode ?? portable) ? Path.Combine(root, "Data") : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskBarWidgets");
    }
}
