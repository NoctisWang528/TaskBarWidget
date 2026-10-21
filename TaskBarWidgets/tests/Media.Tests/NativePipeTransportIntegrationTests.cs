using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using TaskBarWidgets.Media.Integration;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;
using Xunit;

namespace TaskBarWidgets.Media.Tests;

// The managed MediaPipeServer is the real protocol producer and the native client
// runs the product's NativePipeReader, so this covers the Win32 OVERLAPPED
// completion paths end to end instead of a static JSON fixture. The client uses
// a uniquely named pipe and exits on its own; no product process is involved.
public sealed class NativePipeTransportIntegrationTests
{
    private sealed record Inbound(string Action, string RequestId, long Generation);

    [Fact]
    public async Task ProductReaderConsumesRealMediaServerTraffic()
    {
        var clientPath = NativeClientPath();
        var name = "TaskBarWidgets.NativeTransport." + Guid.NewGuid().ToString("N");
        var style = NativeMediaPayloadFactory.CreateStyle(new AppSettings(), 1);
        var cover = NativeMediaPayloadFactory.CreateCover(
            "data:image/png;base64,AAA", "Q", "rgba(67,160,71,1)", "", "QQMUSIC|TITLE|ARTIST");
        var first = Frame(1, "first line", style, cover);
        var second = Frame(2, "second line", style, cover);

        var commands = Channel.CreateUnbounded<Inbound>();
        var connections = new ConcurrentQueue<(bool Connected, long Generation)>();
        await using var server = new MediaPipeServer(
            name,
            MediaProtocol.Initialize,
            () => first,
            (command, generation) =>
            {
                commands.Writer.TryWrite(new Inbound(
                    command.GetProperty("action").GetString() ?? "",
                    command.GetProperty("requestId").GetString() ?? "",
                    generation));
                return Task.CompletedTask;
            },
            (connected, generation) => connections.Enqueue((connected, generation)));
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var process = StartClient(clientPath, name, timeout.Token);
        try
        {
            var echo = await ReadCommand(commands.Reader, timeout.Token);
            Assert.Equal("pipeTestEcho", echo.Action);
            await server.SendCommandAsync(echo.Generation, new
            {
                version = 1,
                type = "command",
                action = "pipeTestReply",
                requestId = echo.RequestId,
                success = true
            });

            var publish = await ReadCommand(commands.Reader, timeout.Token);
            Assert.Equal("pipeTestPublish", publish.Action);
            server.Publish(second);

            var disconnect = await ReadCommand(commands.Reader, timeout.Token);
            Assert.Equal("pipeTestDisconnect", disconnect.Action);

            var done = await ReadCommand(commands.Reader, timeout.Token);
            Assert.Equal("pipeTestDone", done.Action);

            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0,
                $"native client exited {process.ExitCode}:{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            Assert.Contains("NATIVE-PIPE-CLIENT OK", stdout);
            Assert.DoesNotContain("FAIL", stdout);
            Assert.DoesNotContain("FAIL", stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        Assert.True(connections.Count(entry => entry.Connected) >= 2,
            "the server must serve the initial and the rebuilt connection");
    }

    private static async Task<Inbound> ReadCommand(ChannelReader<Inbound> reader, CancellationToken token) =>
        await reader.ReadAsync(token);

    private static Process StartClient(string path, string name, CancellationToken token)
    {
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(name);
        info.ArgumentList.Add("--server-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--timeout");
        info.ArgumentList.Add("30000");
        var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start the native pipe client.");
        token.Register(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        });
        return process;
    }

    private static string NativeClientPath()
    {
        var configured = Environment.GetEnvironmentVariable("TBW_NATIVE_PIPE_CLIENT");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "TaskBarWidgets", "Native", "Bin", "Tests",
                "NativePipeIntegrationClient.exe");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "NativePipeIntegrationClient.exe is missing; run TaskBarWidgets/Native/tests/run-native-tests.ps1 first.");
    }

    private static MediaFrame Frame(long revision, string current, MediaStyleFrame style, MediaCoverFrame cover) =>
        new(
            MediaProtocol.Version,
            "frame",
            MediaProtocol.RenderVersion,
            revision,
            "qq",
            true,
            true,
            false,
            "QQMUSIC|TITLE|ARTIST",
            new MediaLyricsFrame(current, "next", 0.25, 0, "QQMUSIC|TITLE|ARTIST", false, true,
                "", "", false, true, 0.5, "lyrics"),
            style,
            cover,
            new MediaBrowserFrame(false, "Title", "Artist", "qq", true, true, true, true),
            NativeMediaPayloadFactory.CreateSpectrumTuning(SpectrumTuningSettings.CreateDefault()),
            []);
}
