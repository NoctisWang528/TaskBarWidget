using System.IO;
using System.Reflection;

namespace TaskbarLyrics.App;

// Copies the packaged factory settings into place the first time the media helper
// runs. The target file's existence is the only signal: an existing user file is
// never read from, merged with, or overwritten by the packaged copy.
internal static class FactoryDefaults
{
    private const string ResourceName = "media-defaults.json";

    internal static bool EnsureSeeded(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || File.Exists(settingsPath)) return false;
        try
        {
            using var source = typeof(FactoryDefaults).Assembly.GetManifestResourceStream(ResourceName);
            if (source is null) return false;

            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Write to a sibling file first and rename without overwrite so a crash or a
            // second process can never leave a truncated configuration behind.
            var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    source.CopyTo(file);
                File.Move(temporary, settingsPath, overwrite: false);
                return true;
            }
            catch (IOException)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return false;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }
}
