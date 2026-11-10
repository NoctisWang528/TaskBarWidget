using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskBarWidgets.Media.Updates;
using Xunit;

namespace TaskBarWidgets.Media.Tests.Updates;

public sealed class UpdateBridgeTests
{
    [Fact]
    public async Task LocalPushAcceptsBridgeWithoutExecutingOrMovingTheFinalRootFiles()
    {
        using var package = new BridgePackage();
        await using var coordinator = package.Coordinator();
        var state = await coordinator.ImportLocalAsync(package.Release);
        Assert.Equal("ready", state.Phase);
        var prepared = await coordinator.PrepareInstall();
        Assert.False(File.Exists(Path.Combine(prepared.StageRoot, "TaskBarWidgets.PeripheralProbe.exe")));
        Assert.True(File.Exists(Path.Combine(prepared.StageRoot, "Media/UpgradePayload/manifest.json")));
        Assert.Equal("bridge helper", File.ReadAllText(Path.Combine(prepared.StageRoot, "TaskBarWidgets.exe")));
        Assert.False(Directory.Exists(Path.Combine(package.Root, "installation")));
    }

    [Theory]
    [InlineData("personal")]
    [InlineData("unknown-dependency")]
    [InlineData("traversal")]
    [InlineData("nested")]
    [InlineData("missing-blob")]
    [InlineData("inner-hash")]
    [InlineData("inner-version")]
    [InlineData("inner-size")]
    [InlineData("wrong-entry")]
    public async Task ValidOuterHashesDoNotAuthorizeAnInvalidInnerPayload(string problem)
    {
        using var package = new BridgePackage(problem);
        await using var coordinator = package.Coordinator();
        var state = await coordinator.ImportLocalAsync(package.Release);
        Assert.Equal("error", state.Phase);
        Assert.False(state.CanInstall);
    }

    [Fact]
    public async Task PrepareInstallRechecksTheBridgeAfterReady()
    {
        using var package = new BridgePackage();
        await using var coordinator = package.Coordinator();
        Assert.Equal("ready", (await coordinator.ImportLocalAsync(package.Release)).Phase);
        var inner = Directory.GetFiles(coordinator.CacheRoot, "manifest.json", SearchOption.AllDirectories).Single();
        File.AppendAllText(inner, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.PrepareInstall());
    }

    [Theory]
    [InlineData("TaskBarWidgets.PeripheralProbe.exe")]
    [InlineData("peripheral-compatibility.csv")]
    [InlineData("peripheral-compatibility.json")]
    [InlineData("外设电量.md")]
    [InlineData("Dependencies/HeadsetControl/headsetcontrol.exe")]
    [InlineData("Dependencies/HeadsetControl/HeadsetControl-4.1.0-source.zip")]
    public void FinalLayoutHasExactProgramPathExceptions(string path) => Assert.Equal(path, UpdatePackage.ValidateRelativePath(path));

    [Theory]
    [InlineData("Dependencies/HeadsetControl/settings.json")]
    [InlineData("Dependencies/HeadsetControl/private.db")]
    [InlineData("Dependencies/Other/headsetcontrol.exe")]
    [InlineData("Dependencies/HeadsetControl/subdir/LICENSE")]
    [InlineData("Data/config/peripherals.json")]
    public void FinalLayoutDoesNotWidenPersonalOrDependencyDirectories(string path) =>
        Assert.Throws<InvalidDataException>(() => UpdatePackage.ValidateRelativePath(path));

    private sealed class BridgePackage : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "tbw-bridge-" + Guid.NewGuid().ToString("N"));
        internal string Release => Path.Combine(Root, "release");
        private const string Version = "261110-2100";
        private const string Prefix = "Media/UpgradePayload/";
        internal UpdateCoordinator Coordinator() => new(Path.Combine(Root, "installation"), "261109-2209", false,
            _ => { }, Path.Combine(Root, "cache"), debugEnabled: () => true);

        internal BridgePackage(string? problem = null)
        {
            Directory.CreateDirectory(Release);
            var final = new Dictionary<string, byte[]>
            {
                ["TaskBarWidgets.exe"] = Encoding.UTF8.GetBytes("final product"),
                ["TaskBarWidgets.Updater.exe"] = Encoding.UTF8.GetBytes("bridge helper"),
                [problem switch
                {
                    "personal" => "Data/config/secret.json",
                    "unknown-dependency" => "Dependencies/HeadsetControl/private.db",
                    "traversal" => "../private.exe",
                    "nested" => Prefix + "nested.bin",
                    _ => "TaskBarWidgets.PeripheralProbe.exe"
                }] = Encoding.UTF8.GetBytes("probe"),
                ["Dependencies/HeadsetControl/headsetcontrol.exe"] = Encoding.UTF8.GetBytes("headset probe"),
                ["外设电量.md"] = Encoding.UTF8.GetBytes("documentation")
            };
            var inner = final.Select(file => new AppFile(file.Key, file.Value.Length + (problem == "inner-size" ? 1 : 0), Hash(file.Value))).ToArray();
            var innerBytes = JsonSerializer.SerializeToUtf8Bytes(new AppFileManifest(1,
                problem == "inner-version" ? "261109-2209" : Version, "win-x64", inner), UpdatePackage.Json);
            var envelope = new Dictionary<string, byte[]>
            {
                ["TaskBarWidgets.exe"] = Encoding.UTF8.GetBytes(problem == "wrong-entry" ? "wrong entry" : "bridge helper"),
                ["TaskBarWidgets.Updater.exe"] = final["TaskBarWidgets.Updater.exe"],
                [Prefix + "manifest.json"] = innerBytes,
                [Prefix + "bridge.json"] = JsonSerializer.SerializeToUtf8Bytes(new { format = "TBW_BRIDGE_V1", version = Version,
                    fileManifestSha256 = problem == "inner-hash" ? new string('0', 64) : Hash(innerBytes) }),
                [Prefix + "resume.json"] = Encoding.UTF8.GetBytes("{}")
            };
            foreach (var file in final)
                if (problem != "missing-blob" || file.Key != "TaskBarWidgets.exe")
                    envelope[Prefix + Hash(file.Value)[..16] + ".bin"] = file.Value;
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new AppFileManifest(1, Version, "win-x64",
                envelope.Select(file => new AppFile(file.Key, file.Value.Length, Hash(file.Value))).ToArray()), UpdatePackage.Json);
            envelope["app-files.json"] = manifestBytes;
            var assetName = $"TaskBarWidgets-{Version}-win-x64.zip";
            var zip = Path.Combine(Release, assetName);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var file in envelope)
                {
                    using var stream = archive.CreateEntry(Path.GetFileNameWithoutExtension(assetName) + "/" + file.Key).Open();
                    stream.Write(file.Value);
                }
            var zipBytes = File.ReadAllBytes(zip);
            File.WriteAllBytes(Path.Combine(Release, "update.json"), JsonSerializer.SerializeToUtf8Bytes(new UpdateMetadata(1, Version,
                "win-x64", assetName, zipBytes.Length, Hash(zipBytes), Hash(manifestBytes), new("8.0.0", "2.5.1.0", 22000)), UpdatePackage.Json));
            // A second portable asset is permitted by the real coordinator's checksum reader.
            File.WriteAllText(Path.Combine(Release, "SHA256SUMS.txt"), Hash(zipBytes) + "  " + assetName + "\n" +
                new string('0', 64) + $"  TaskBarWidgets-{Version}-win-x64-portable.zip\n");
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        public void Dispose() => Directory.Delete(Root, true);
    }
}
