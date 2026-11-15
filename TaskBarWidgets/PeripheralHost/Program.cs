using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace TaskBarWidgets.PeripheralHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"]) return await SelfTest.RunAsync().ConfigureAwait(false);
        if (args is not ["--parent-pid", var pidText] || !int.TryParse(pidText, out int pid)
            || pid <= 0 || pid == Environment.ProcessId)
        {
            Console.Error.WriteLine("parent_pid_required");
            return 2;
        }
        ParentLifetime parent;
        try
        {
            var handle = OpenProcess(0x00100000, false, (uint)pid); // SYNCHRONIZE only
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 87) return 0; // parent already exited
                Console.Error.WriteLine("parent_unavailable"); return 2;
            }
            parent = new ParentLifetime(handle);
        }
        catch (Exception) { Console.Error.WriteLine("parent_unavailable"); return 2; }

        using (parent)
        using (var lifetime = new CancellationTokenSource())
        {
            // Process.EnableRaisingEvents also requests query access, which may be
            // denied even when waiting for this parent's exit is permitted.
            var parentWait = ThreadPool.RegisterWaitForSingleObject(parent,
                (_, _) => Environment.Exit(0), null, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            // A separate stdin pump observes EOF even if a synchronous driver call is
            // stuck. Returning Main terminates background work and closes OS handles.
            _ = PumpAsync(Console.OpenStandardInput(), queue.Writer, lifetime);
            var engine = new HostEngine(VigilWatt.Core.Hid.HidDeviceScanner.Scan,
                PluginRegistry.Create, Metadata.ReadContainer);
            try
            {
                await foreach (byte[] line in queue.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                {
                    int requestId = -1;
                    Request request;
                    try { request = Request.Parse(line, out requestId); }
                    catch (InvalidDataException error) { Wire.Write(Response.Failure(requestId, error.Message), requestId); continue; }
                    catch (Exception) { Wire.Write(Response.Failure(requestId, "invalid_request"), requestId); continue; }
                    try
                    {
                        // Native HID APIs may block; only the parent owns a request timeout.
                        var work = Task.Run(() => engine.ExecuteAsync(request, message => Wire.Write(message, requestId)));
                        var reply = await work.WaitAsync(lifetime.Token).ConfigureAwait(false);
                        Wire.Write(reply, requestId);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
                    catch (Exception) { Wire.Write(Response.Failure(requestId, "host_failed"), requestId); }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) { return 3; }
            }
            finally { parentWait.Unregister(null); }
        }
        return 0;
    }

    private sealed class ParentLifetime : WaitHandle
    {
        public ParentLifetime(SafeWaitHandle handle) { SafeWaitHandle = handle; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint pid);

    internal static async Task PumpAsync(Stream input, ChannelWriter<byte[]> queue, CancellationTokenSource lifetime)
    {
        try
        {
            var buffer = new byte[4096];
            using var line = new MemoryStream();
            int count;
            while ((count = await input.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    byte b = buffer[i];
                    if (b == '\n')
                    {
                        if (!queue.TryWrite(line.ToArray())) throw new InvalidDataException("request_queue_full");
                        line.SetLength(0);
                    }
                    else
                    {
                        if (line.Length >= Wire.MaximumLineBytes - 1) throw new InvalidDataException("request_too_large");
                        line.WriteByte(b);
                    }
                }
            }
        }
        catch (Exception) { /* EOF, closed pipe, malformed line or parent shutdown ends ownership. */ }
        finally
        {
            queue.TryComplete();
            lifetime.Cancel();
        }
    }
}
