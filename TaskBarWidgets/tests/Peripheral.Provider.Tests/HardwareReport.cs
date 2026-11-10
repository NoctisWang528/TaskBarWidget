using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using TaskBarWidgets.Media.Peripherals;

// Explicit diagnostic mode only; never starts the normal product or saves user settings.
internal static class HardwareReport
{
    internal static async Task RunAsync(string directory, string output)
    {
        if (!File.Exists(Path.Combine(directory, "TaskBarWidgets.PeripheralProbe.exe")))
            throw new FileNotFoundException("A complete isolated build is required.");
        Console.WriteLine("Measuring isolated host baseline (15 seconds).");
        var baseline = await MeasureAsync(null, 15);
        var native = new NativePeripheralBatteryProvider(directory);
        await using var service = new PeripheralBatteryService([native, new WindowsPeripheralBatteryProvider()], null);
        service.SetSettingsActive(true);
        service.Start();
        var wait = Stopwatch.StartNew();
        while (service.Snapshot.Revision < 2 || service.Snapshot.Refreshing)
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(100)) throw new TimeoutException("Initial battery cycle did not finish.");
            await Task.Delay(100);
        }
        Console.WriteLine($"Discovered {service.Snapshot.Devices.Length} devices; measuring active demand (65 seconds).");
        var active = await MeasureAsync(native, 65);
        var snapshot = service.Snapshot;
        service.SetSettingsActive(false);
        await Task.Delay(1000);
        Console.WriteLine("Measuring disabled demand (15 seconds).");
        var disabled = await MeasureAsync(native, 15);
        var report = new {
            utc = DateTimeOffset.UtcNow, scope = "Steady isolated battery service after initial discovery, Windows and native providers; optional extensions disabled. Includes owned native worker. Does not represent the normal Media host, physical unplugging or charging acceptance.",
            baseline, active, disabled,
            incrementalActiveCpuPercent = Math.Max(0, active.cpuPercent - baseline.cpuPercent),
            incrementalActivePrivateMiB = Math.Max(0, active.privateMiB - baseline.privateMiB),
            workerStoppedAfterDemandRemoved = Worker(native) is null,
            snapshot
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Hardware and resource report: {output}");
    }

    private sealed record Sample(double cpuSeconds, double privateMiB);
    private sealed record Measurement(double seconds, double cpuPercent, double privateMiB, double peakPrivateMiB, bool workerObserved);

    // Reflection stays in this diagnostic executable so production need not expose a process handle.
    private static Process? Worker(NativePeripheralBatteryProvider provider)
    {
        var field = typeof(NativePeripheralBatteryProvider).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Update the resource probe after changing the process owner.");
        try { return field.GetValue(provider) is Process worker && !worker.HasExited ? worker : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static Sample Read(Process process)
    {
        process.Refresh();
        return new(process.TotalProcessorTime.TotalSeconds, process.PrivateMemorySize64 / 1048576d);
    }

    private static async Task<Measurement> MeasureAsync(NativePeripheralBatteryProvider? provider, int seconds)
    {
        using var host = Process.GetCurrentProcess();
        var before = Read(host);
        var clocks = new Dictionary<int, double>();
        if (provider is not null && Worker(provider) is Process initial) clocks[initial.Id] = Read(initial).cpuSeconds;
        var peak = before.privateMiB;
        double childCpu = 0, finalMemory = peak;
        bool observed = clocks.Count != 0;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(500);
            var parent = Read(host);
            finalMemory = parent.privateMiB;
            if (provider is not null && Worker(provider) is Process child)
            {
                try
                {
                    var sample = Read(child);
                    childCpu += Math.Max(0, sample.cpuSeconds - clocks.GetValueOrDefault(child.Id));
                    clocks[child.Id] = sample.cpuSeconds;
                    finalMemory += sample.privateMiB;
                    observed = true;
                }
                catch (InvalidOperationException) { }
            }
            peak = Math.Max(peak, finalMemory);
        }
        return new(elapsed.Elapsed.TotalSeconds,
            (Read(host).cpuSeconds - before.cpuSeconds + childCpu) / elapsed.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
            finalMemory, peak, observed);
    }
}
