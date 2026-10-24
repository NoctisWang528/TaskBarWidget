using System.Diagnostics;
using System.IO;
using TaskbarLyrics.Core.Utilities;
using TaskBarWidgets.Media.Integration;

namespace TaskbarLyrics.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--validate-package") return PackageProbe.Run(args[1]);
        if (args.Length == 5 && args[0] == "--validate-update-package") return PackageProbe.ValidateUpdateAsync(args[1], args[2], args[3], args[4]).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--migrate-data")
            return RunMigration(args[1]);
        if (!TryGetHostArguments(args, out var hostPid, out var pipeName, out var dataRoot)) return 2;
        try
        {
            using var host = Process.GetProcessById(hostPid);
            if (host.HasExited || host.SessionId != Process.GetCurrentProcess().SessionId) return 2;
            var applicationRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            DataStartup.Prepare(dataRoot ?? AppDataPaths.ResolveForApplicationRoot(applicationRoot), applicationRoot);
            var app = new App(host, pipeName, dataRoot);
            var exitCode = app.Run();
            return exitCode;
        }
        catch (ArgumentException) { return 2; }
        catch (Exception error)
        {
            try
            {
                AppDataPaths.EnsureDirectories();
                File.AppendAllText(AppDataPaths.ErrorLogPath,
                    $"{DateTimeOffset.Now:o} data startup failed{Environment.NewLine}{error}{Environment.NewLine}");
            }
            catch { }
            try { Console.Error.WriteLine(error.Message); } catch { }
            return 3;
        }
    }

    private static int RunMigration(string dataRoot)
    {
        try
        {
            var installRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            var result = DataStartup.Prepare(dataRoot, installRoot);
            if (result.Warnings.Count > 0)
            {
                File.AppendAllLines(AppDataPaths.ErrorLogPath,
                    result.Warnings.Select(warning => $"{DateTimeOffset.Now:o} migration warning: {warning}"));
            }
            return 0;
        }
        catch (Exception error)
        {
            try
            {
                var configured = false;
                try { _ = AppDataPaths.RootDirectory; configured = true; } catch (InvalidOperationException) { }
                if (!configured) AppDataPaths.ConfigureForDataRoot(dataRoot);
                AppDataPaths.EnsureDirectories();
                File.AppendAllText(AppDataPaths.ErrorLogPath,
                    $"{DateTimeOffset.Now:o} data migration failed{Environment.NewLine}{error}{Environment.NewLine}");
            }
            catch { }
            try { Console.Error.WriteLine(error.Message); } catch { }
            return 3;
        }
    }

    internal static bool TryGetHostArguments(string[] args, out int hostPid, out string pipeName)
        => TryGetHostArguments(args, out hostPid, out pipeName, out _);

    internal static bool TryGetHostArguments(string[] args, out int hostPid, out string pipeName, out string? dataRoot)
    {
        hostPid = 0; pipeName = ""; dataRoot = null;
        if (args.Length == 6)
        {
            if (args[4] != "--data-root" || string.IsNullOrWhiteSpace(args[5])) return false;
            dataRoot = Path.GetFullPath(args[5]);
        }
        if (args.Length is not (4 or 6) || args[0] != "--host-pid" || args[2] != "--pipe-name" ||
            !int.TryParse(args[1], out hostPid) || hostPid <= 0 || hostPid == Environment.ProcessId) return false;
        pipeName = args[3];
        return pipeName == $"TaskBarWidgets.{hostPid}";
    }
}
