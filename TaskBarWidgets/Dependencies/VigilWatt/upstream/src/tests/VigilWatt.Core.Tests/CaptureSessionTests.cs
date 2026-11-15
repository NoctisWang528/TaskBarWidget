using VigilWatt.Core.Capture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 会话生命周期：无适配器也能建会话、不自动切换方式、停止幂等、失败保留证据、上限截断、历史可重开。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureSessionTests
{
    [Fact]
    public void StateMachine_AllowsHappyPath_AndStopsIdempotently()
    {
        var machine = new CaptureSessionStateMachine();
        Assert.True(machine.BeginProbe());
        Assert.True(machine.MarkReady());
        Assert.True(machine.Start());
        Assert.True(machine.Pause());
        Assert.True(machine.Resume());
        Assert.True(machine.Stop());
        Assert.False(machine.Stop());
        Assert.Equal(CaptureSessionState.Stopped, machine.State);
    }

    [Fact]
    public void StateMachine_RejectsIllegalTransitions()
    {
        var machine = new CaptureSessionStateMachine();
        Assert.False(machine.Start());
        Assert.False(machine.Pause());
        machine.BeginProbe();
        Assert.False(machine.Start());
        machine.MarkReady();
        Assert.True(machine.Fail());
        Assert.Equal(CaptureSessionState.Failed, machine.State);
        Assert.True(machine.IsTerminal);
    }

    [Fact]
    public void Session_CanBeCreatedWithoutAnyAdapter()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using var session = CaptureSession.Create(device, [], new CaptureOptions(), () => new FakeCaptureSource(device, []),
                new CaptureStore(root));

            Assert.Equal(CaptureSessionState.Ready, session.State);
            Assert.True(File.Exists(CaptureStore.PathOf(session.Directory, "session.json")));
            Assert.True(File.Exists(CaptureStore.PathOf(session.Directory, "device.json")));
            var sessionJson = File.ReadAllText(CaptureStore.PathOf(session.Directory, "session.json"));
            Assert.Contains("usb-hid", sessionJson);
            Assert.Contains("unknown-device", sessionJson);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_RecordsFrames_AndProducesPackage()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            var frames = new[] { new byte[] { 0x07, 0x10 }, new byte[] { 0x07, 0x11 }, new byte[] { 0x07, 0x12 } };
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, frames), new CaptureStore(root));

            Assert.True(session.Start());
            await WaitForRecords(session, frames.Length);
            var bundle = await session.StopAsync();

            Assert.Equal(3, bundle.Records.Count);
            Assert.Equal(3, bundle.Session.RecordCount);
            Assert.Equal(CaptureSessionState.Stopped, bundle.Session.State);
            Assert.NotNull(session.PackagePath);
            Assert.True(File.Exists(session.PackagePath!));
            Assert.True(File.Exists(CaptureStore.PathOf(session.Directory, "report.html")));

            using var zip = System.IO.Compression.ZipFile.OpenRead(session.PackagePath!);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("report.html", names);
            Assert.Contains("records.jsonl", names);
            Assert.Contains("session.json", names);
            Assert.Contains("analysis.json", names);
            Assert.Contains("README.txt", names);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_Abort_DeletesDirectory_AndWritesNoReport()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, [new byte[] { 0x07, 0x10 }]), new CaptureStore(root));
            session.Start();
            await WaitForRecords(session, 1);
            var directory = session.Directory;
            Assert.True(Directory.Exists(directory));

            var bundle = await session.AbortAsync();

            // 内存里还能拿到证据（界面要报「删了几条」），磁盘上什么都不留
            Assert.Single(bundle.Records);
            Assert.True(session.RecordsDeleted);
            Assert.False(Directory.Exists(directory));
            Assert.Null(session.PackagePath);
            Assert.Empty(new CaptureStore(root).ListSessions());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_AbortThenStop_DoesNotResurrectFiles()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, [new byte[] { 0x07 }]), new CaptureStore(root));
            session.Start();
            await WaitForRecords(session, 1);
            var directory = session.Directory;

            var aborted = await session.AbortAsync();
            // 放弃之后又点了「停止」：不能把目录和报告重新写出来
            var stopped = await session.StopAsync();

            Assert.Same(aborted, stopped);
            Assert.False(Directory.Exists(directory));
            Assert.Null(session.PackagePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_Send_RecordsHostFrame_AndChecksCaps()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();   // output 长度 19，report ID 0x13
            var source = new FakeWritableSource(device);
            using var session = CaptureSession.Create(device, [], new CaptureOptions { AllowSend = true },
                () => source, new CaptureStore(root));
            session.Start();
            await WaitForRecords(session, 1);
            Assert.True(session.CanSend);

            // 长度不对：HID 对长度是严格的，先在 caps 层面挡掉，别等 Win32 报错
            Assert.False(session.TrySend(new byte[] { 0x13 }, out var lengthError));
            Assert.Contains("长度必须是 19", lengthError);
            Assert.Empty(source.Sent);

            // report ID 必须来自 caps
            var badId = new byte[19];
            Assert.False(session.TrySend(badId, out var idError));
            Assert.Contains("report ID", idError);
            Assert.Empty(source.Sent);

            var frame = new byte[19];
            frame[0] = 0x13;
            Assert.True(session.TrySend(frame, out var sendError), sendError);

            var bundle = await session.StopAsync();
            var sent = Assert.Single(bundle.Records, r => r.Status == CaptureRecordStatus.Sent);
            Assert.Equal(CaptureLayer.HidApiOutput, sent.Layer);
            Assert.Equal("WriteFile", sent.Api);
            Assert.Equal(0x13, sent.ReportId);
            Assert.Equal(1, bundle.Analysis.SentRecords);
            // 主机发出的帧不能算成「失败记录」
            Assert.Equal(0, bundle.Analysis.FailedRecords);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_Send_RefusedWithoutAllowSend()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            var source = new FakeWritableSource(device);
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => source, new CaptureStore(root));
            session.Start();
            await WaitForRecords(session, 1);

            Assert.False(session.CanSend);
            Assert.False(session.TrySend(new byte[19], out var error));
            Assert.Contains("没有开启", error);
            Assert.Empty(source.Sent);
            await session.StopAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Store_RefusesToDeleteAnythingOutsideCapturesRoot()
    {
        var root = CaptureTestData.NewTempDir();
        var outside = CaptureTestData.NewTempDir();
        try
        {
            var store = new CaptureStore(root);

            Assert.False(await store.TryDeleteSessionAsync(outside));
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Session_StopIsIdempotent()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, [new byte[] { 0x07 }]), new CaptureStore(root));
            session.Start();
            await WaitForRecords(session, 1);

            var first = await session.StopAsync();
            var second = await session.StopAsync();

            Assert.Same(first, second);
            Assert.Single(second.Records);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_OpenFailure_KeepsEvidenceAndStillReports()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, [], failOpen: true), new CaptureStore(root));
            session.Start();
            await WaitForErrors(session, 1);

            var bundle = await session.StopAsync();

            Assert.Empty(bundle.Records);
            Assert.Contains(bundle.Errors, e => e.Kind == CaptureErrorKind.OpenDenied && e.HResult == 5);
            Assert.True(File.Exists(CaptureStore.PathOf(session.Directory, "report.html")));
            var html = File.ReadAllText(CaptureStore.PathOf(session.Directory, "report.html"));
            Assert.Contains("open_denied", html);
            Assert.Contains("本次没有记录到任何报文", html);
            Assert.Contains("不等于设备没有相关协议", html);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_MarksStayOutOfRecords()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, [new byte[] { 0x07 }]), new CaptureStore(root));
            session.Start();
            await WaitForRecords(session, 1);
            session.Mark("按下设备按键");

            var bundle = await session.StopAsync();

            Assert.Single(bundle.Records);
            Assert.Contains(bundle.Events, e => e.Kind == CaptureEventKind.UserMark && e.Note == "按下设备按键");
            Assert.DoesNotContain(bundle.Records, r => r.RecordType.Contains("mark", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_NeverFallsBackToAnotherMethod_WhenSourceFails()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, [], failOpen: true), new CaptureStore(root));
            session.Start();
            await WaitForErrors(session, 1);

            var bundle = await session.StopAsync();

            Assert.Equal(CaptureMethod.UsbHid, bundle.Session.Method);
            Assert.All(bundle.Records, r => Assert.Equal("usb-hid", r.Source));
            Assert.Empty(bundle.Records);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_TruncatesAtRecordLimit_AndSaysSo()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            var frames = Enumerable.Range(0, 8).Select(i => new byte[] { 0x07, (byte)i }).ToArray();
            var limits = new CaptureLimits { MaxRecords = 3, MaxBytes = long.MaxValue };
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, frames), new CaptureStore(root), limits);
            session.Start();
            await WaitForRecords(session, 3);
            await Task.Delay(100);

            var bundle = await session.StopAsync();

            Assert.Equal(3, bundle.Records.Count);
            Assert.True(bundle.Session.RecordsTruncated);
            Assert.Contains(bundle.Errors, e => e.Kind == CaptureErrorKind.BufferLimit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Store_ListsAndReloadsSavedSession()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using (var session = CaptureSession.Create(device, [], new CaptureOptions(),
                       () => new FakeCaptureSource(device, [new byte[] { 0x07, 0x10 }]), new CaptureStore(root)))
            {
                session.Start();
                await WaitForRecords(session, 1);
                await session.StopAsync();
            }

            // 模拟重启：新的 store 实例扫目录
            var reopened = new CaptureStore(root);
            var sessions = reopened.ListSessions();
            var entry = Assert.Single(sessions);
            Assert.Equal(1, entry.RecordCount);
            Assert.Equal("Test Dongle", entry.DeviceName);
            Assert.True(entry.HasReport);

            Assert.True(CaptureStore.TryLoad(entry.Directory, out var bundle));
            Assert.Single(bundle!.Records);
            Assert.Equal(new byte[] { 0x07, 0x10 }, bundle.Records[0].Data);
            Assert.NotNull(bundle.Device);
            Assert.Equal("SN-0001", bundle.Device!.SerialNumber);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_MarkBattery_OnlyWhileRunning_AndRejectsOutOfRange()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, [new byte[] { 0x07, 0x4E }]), new CaptureStore(root));

            Assert.False(session.MarkBattery(78));

            session.Start();
            await WaitForRecords(session, 1);
            Assert.False(session.MarkBattery(101));
            Assert.False(session.MarkBattery(-1));
            Assert.False(session.MarkBattery(0));
            Assert.True(session.MarkBattery(78));

            var bundle = await session.StopAsync();
            var reference = Assert.Single(bundle.Events, e => e.Kind == CaptureEventKind.BatteryReference);
            Assert.Equal(78, reference.Percent);
            Assert.Single(bundle.Analysis.BatteryReferences);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_Slice_ReturnsOnlyRecordsAfterIndex()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            var frames = new[] { new byte[] { 0x07, 0x01 }, new byte[] { 0x07, 0x02 }, new byte[] { 0x07, 0x03 } };
            using var session = CaptureSession.Create(device, [], new CaptureOptions(),
                () => new FakeCaptureSource(device, frames), new CaptureStore(root));
            session.Start();
            await WaitForRecords(session, 3);

            // 界面增量刷新：从已渲染到的下标往后取，不能重复也不能漏
            Assert.Equal(3, session.Slice(0, 100).Count);
            Assert.Equal(2, session.Slice(1, 100).Count);
            Assert.Empty(session.Slice(3, 100));
            // max 是"最多保留最近 N 条"，从尾部截
            Assert.Equal(2, session.Slice(0, 2).Count);

            await session.StopAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_FlagsSessionWithoutReport()
    {
        var root = CaptureTestData.NewTempDir();
        try
        {
            var device = CaptureTestData.Device();
            // 建了会话但从不停止 = 应用在采集途中被关掉，历史里会出现一条点不开的僵尸会话
            using (var session = CaptureSession.Create(device, [], new CaptureOptions(),
                       () => new FakeCaptureSource(device, []), new CaptureStore(root)))
            {
                Assert.Equal(CaptureSessionState.Ready, session.State);
            }

            var entry = Assert.Single(new CaptureStore(root).ListSessions());
            Assert.False(entry.HasReport);
            Assert.False(File.Exists(CaptureStore.PathOf(entry.Directory, "report.html")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForRecords(CaptureSession session, int expected)
    {
        for (var i = 0; i < 300 && session.RecordCount < expected; i++) await Task.Delay(10);
        Assert.True(session.RecordCount >= expected, $"等待记录超时，实际 {session.RecordCount} 条");
    }

    private static async Task WaitForErrors(CaptureSession session, int expected)
    {
        for (var i = 0; i < 300 && session.ErrorCount < expected; i++) await Task.Delay(10);
        Assert.True(session.ErrorCount >= expected, $"等待错误超时，实际 {session.ErrorCount} 条");
    }
}
