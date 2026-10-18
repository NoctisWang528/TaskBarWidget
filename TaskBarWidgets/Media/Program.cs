using System.Diagnostics;
using TaskBarWidgets.Media.Integration;

namespace TaskbarLyrics.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--validate-package") return PackageProbe.Run(args[1]);
        if (args.Length == 5 && args[0] == "--validate-update-package") return PackageProbe.ValidateUpdateAsync(args[1], args[2], args[3], args[4]).GetAwaiter().GetResult();
        if (!TryGetHostArguments(args, out var hostPid, out var pipeName, out var portableData)) return 2;
        try
        {
            using var host = Process.GetProcessById(hostPid);
            if (host.HasExited || host.SessionId != Process.GetCurrentProcess().SessionId) return 2;
            var app = new App(host, pipeName, portableData);
            return app.Run();
        }
        catch (ArgumentException) { return 2; }
    }

    internal static bool TryGetHostArguments(string[] args, out int hostPid, out string pipeName)
        => TryGetHostArguments(args, out hostPid, out pipeName, out _);

    internal static bool TryGetHostArguments(string[] args, out int hostPid, out string pipeName, out bool? portableData)
    {
        hostPid = 0; pipeName = ""; portableData = null;
        if (args.Length == 6)
        {
            if (args[4] != "--portable-data" || args[5] is not ("0" or "1")) return false;
            portableData = args[5] == "1";
        }
        if (args.Length is not (4 or 6) || args[0] != "--host-pid" || args[2] != "--pipe-name" ||
            !int.TryParse(args[1], out hostPid) || hostPid <= 0 || hostPid == Environment.ProcessId) return false;
        pipeName = args[3];
        return pipeName == $"TaskBarWidgets.{hostPid}";
    }
}
