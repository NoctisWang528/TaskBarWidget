using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TaskBarWidgets.Media.Updates;
using TaskbarLyrics.Core.Utilities;
using TaskbarLyrics.App;

namespace TaskBarWidgets.Media.Integration;

// Executes only after explicit UI confirmation. Downloading never enters this path.
internal sealed class UpdateInstallBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly UpdateCoordinator _updates;
    private readonly NativeSettingsClient _native;
    private readonly UiHostBridge _ui;
    private readonly Process _host;
    private readonly string _root, _dataRoot;
    private int _installing;

    internal UpdateInstallBridge(UpdateCoordinator updates, NativeSettingsClient native, UiHostBridge ui,
        Process host, string root, string dataRoot)
    { _updates = updates; _native = native; _ui = ui; _host = host; _root = root; _dataRoot = dataRoot; }

    internal UpdateSnapshot Begin(CancellationToken lifetime)
    {
        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0) return _updates.Snapshot;
        TaskObserver.Observe(InstallAsync(lifetime), "confirmed application update");
        return _updates.Snapshot;
    }
    private async Task InstallAsync(CancellationToken token)
    {
        string? work = null;
        bool exitRequested = false, handedOff = false;
        try
        {
            var prepared = await _updates.PrepareInstall();
            token.ThrowIfCancellationRequested();
            var context = await _native.RequestAsync("getUpdateContext", cancellationToken: token);
            var installRoot = Path.GetFullPath(context.GetProperty("installRoot").GetString()!).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(installRoot, Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("主程序目录与更新目标不一致，已停止安装。");
            work = Path.Combine(prepared.CacheRoot, "jobs", Guid.NewGuid().ToString("N"));
            UpdatePackage.EnsureSafePath(work);
            Directory.CreateDirectory(work);
            var updater = Path.Combine(work, "TaskBarWidgets.Updater.exe");
            var trustedUpdater = Path.Combine(_root, "TaskBarWidgets.Updater.exe");
            UpdatePackage.EnsureSafePath(trustedUpdater);
            if (!File.Exists(trustedUpdater)) throw new FileNotFoundException("独立更新器缺失，请完整解压最新发行包。", trustedUpdater);
            File.Copy(trustedUpdater, updater, false);
            if (!SHA256.HashData(File.ReadAllBytes(trustedUpdater)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(updater))))
                throw new IOException("独立更新器复制校验失败。");
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var job = new {
                protocolVersion = 2, installRoot, stageRoot = prepared.StageRoot, workRoot = work,
                dataRoot = context.GetProperty("dataRoot").GetString(),
                version = prepared.Version, fileManifestSha256 = prepared.FileManifestSha256, requirements = prepared.Requirements,
                nonce, nativePid = _host.Id, mediaPid = Environment.ProcessId, uiPid = _ui.ProcessId
            };
            var jobPath = Path.Combine(work, "job.json");
            await WriteDurableAsync(jobPath, JsonSerializer.Serialize(job, Json), token);
            await File.WriteAllTextAsync(Path.Combine(work, "recover.cmd"), "@echo off\r\n\"%~dp0TaskBarWidgets.Updater.exe\" --recover \"%~dp0job.json\"\r\n", token);
            await WriteDurableAsync(Path.Combine(prepared.CacheRoot, "last-install.json"), JsonSerializer.Serialize(new { jobPath, version = prepared.Version }, Json), token);
            var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = work };
            start.ArgumentList.Add("--job"); start.ArgumentList.Add(jobPath);
            using var helper = Process.Start(start) ?? throw new IOException("无法启动独立更新器。");
            var ready = false;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(40))
            {
                token.ThrowIfCancellationRequested();
                var statusPath = Path.Combine(work, "status.json");
                if (File.Exists(statusPath))
                {
                    var status = await ReadStatusAsync(statusPath, token);
                    var phase = status.GetProperty("phase").GetString();
                    if (phase == "readyToExit") { ready = true; break; }
                    if (phase is "error" or "recoveryRequired") throw new IOException(StatusMessage(status));
                }
                if (helper.HasExited) throw new IOException("更新器未完成预检，请在关于页重试或使用手动更新。");
                await Task.Delay(200, token);
            }
            if (!ready) throw new TimeoutException("更新预检超时，当前版本保持运行。请稍后重试。");
            _updates.SetInstallStatus("installing");
            // Native validates the exact job and helper, persists history/config, and closes normally.
            exitRequested = true;
            await _native.RequestAsync("exitForUpdate", JsonSerializer.SerializeToElement(new { jobPath, nonce }), token);
            handedOff = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { handedOff = exitRequested; }
        catch (Exception error) { _updates.SetInstallStatus("error", "更新未完成：" + error.Message); }
        finally
        {
            if (!handedOff && work is not null)
            {
                try { await File.WriteAllTextAsync(Path.Combine(work, "cancel.request"), "cancel"); }
                catch (IOException) { Log.Warn("Could not persist update cancellation; shutdown approval is still required."); }
                catch (UnauthorizedAccessException) { Log.Warn("Could not persist update cancellation; shutdown approval is still required."); }
            }
            Interlocked.Exchange(ref _installing, 0);
        }
    }
    internal async Task ReportPreviousFailureAsync(CancellationToken token)
    {
        try
        {
            await _updates.Initialization;
            var pointer = Path.Combine(_updates.CacheRoot, "last-install.json");
            if (!File.Exists(pointer)) return;
            var value = await ReadStatusAsync(pointer, token);
            var job = Path.GetFullPath(value.GetProperty("jobPath").GetString()!);
            if (!job.StartsWith(Path.Combine(_updates.CacheRoot, "jobs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            UpdatePackage.EnsureSafePath(job);
            var statusPath = Path.Combine(Path.GetDirectoryName(job)!, "status.json");
            if (!File.Exists(statusPath)) return;
            var status = await ReadStatusAsync(statusPath, token);
            var phase = status.GetProperty("phase").GetString();
            if (phase is "starting" or "installing" or "backingUp" or "waitingForExit" or "readyToExit")
            {
                try {
                    using var helper = Process.GetProcessById(status.GetProperty("helperPid").GetInt32());
                    var expectedHelper = Path.Combine(Path.GetDirectoryName(job)!, "TaskBarWidgets.Updater.exe");
                    if (!helper.HasExited && string.Equals(helper.MainModule?.FileName, expectedHelper, StringComparison.OrdinalIgnoreCase))
                        return; // Only this job's helper can still be completing startup.
                } catch (ArgumentException) { } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
            }
            if (phase is not "complete")
                _updates.SetInstallStatus("error", "上次更新未完成：" + StatusMessage(status));
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException) { Log.Warn("Could not read previous update outcome: " + error.GetType().Name); }
    }
    private static async Task WriteDurableAsync(string path, string content, CancellationToken token)
    {
        UpdatePackage.EnsureSafePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content), token);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string StatusMessage(JsonElement status) => status.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "请查看更新备份目录。" : "请查看更新备份目录。";
    private static async Task<JsonElement> ReadStatusAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("更新状态文件过大。");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        return document.RootElement.Clone();
    }
}
