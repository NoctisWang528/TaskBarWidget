using System.Diagnostics;
using TaskBarWidgets.Media.Integration;

namespace TaskbarLyrics.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!TryGetHostArguments(args, out var hostPid, out var pipeName)) return 2;
        try
        {
            using var host = Process.GetProcessById(hostPid);
            if (host.HasExited || host.SessionId != Process.GetCurrentProcess().SessionId) return 2;
            var app = new App(host, pipeName);
            app.InitializeComponent();
            return app.Run();
        }
        catch (ArgumentException) { return 2; }
    }

    internal static bool TryGetHostArguments(string[] args, out int hostPid, out string pipeName)
    {
        hostPid = 0; pipeName = "";
        if (args.Length != 4 || args[0] != "--host-pid" || args[2] != "--pipe-name" ||
            !int.TryParse(args[1], out hostPid) || hostPid <= 0 || hostPid == Environment.ProcessId) return false;
        pipeName = args[3];
        return pipeName == $"TaskBarWidgets.{hostPid}";
    }
}
