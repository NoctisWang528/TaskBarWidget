using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TaskBarWidgets.UI.Settings;
using Windows.Graphics;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    internal JsonObject ProbeNativeSchema => (JsonObject)_schema.DeepClone();
    // The published UI renders real templates; all requests terminate in this
    // synthetic handler. No backend, device, user configuration or startup entry.
    internal static async Task<object> ProbePeripheralBatteryControlsAsync(
        JsonObject nativeFixture, JsonObject mediaFixture, string outputDirectory)
    {
        using var connection = new UiConnection("TaskBarWidgets.UI.peripheral-probe.disconnected");
        var window = new SettingsWindow(connection);
        var activated = false;
        window.Activated += (_, args) => {
            if (args.WindowActivationState == WindowActivationState.PointerActivated) activated = true;
        };
        const string mouse = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string earbuds = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string keyboard = "cccccccccccccccccccccccccccccccc";
        var native = (JsonObject)nativeFixture.DeepClone();
        var media = (JsonObject)mediaFixture.DeepClone();
        media["peripheralBattery"] = JsonNode.Parse($$$"""{"mode":"compact","hideDisconnected":false,"headsetControlEnabled":false,"vendorExtensionsEnabled":false,"order":["{{{mouse}}}","{{{earbuds}}}"],"cardVisibility":{"{{{mouse}}}":true,"{{{earbuds}}}":false}}""");
        var enabled = media["quickCardEnabled"] as JsonObject ?? new JsonObject();
        enabled["peripheralBattery"] = false;
        if (enabled.Parent is null) media["quickCardEnabled"] = enabled;
        var snapshot = JsonNode.Parse($$"""{"version":1,"revision":1,"mode":"compact","hideDisconnected":false,"refreshing":false,"error":"","devices":[{"id":"{{mouse}}","name":"测试鼠标","kind":"mouse","connection":"usb","state":"online","batteries":[{"role":"main","percentage":null,"charging":null}],"stale":false,"showInCard":true,"canRead":true,"source":"fixture","compatibility":"implemented"},{"id":"{{earbuds}}","name":"测试耳机","kind":"earbuds","connection":"bluetooth","state":"online","batteries":[{"role":"left","percentage":0,"charging":null},{"role":"right","percentage":80,"estimated":true,"charging":true}],"stale":false,"showInCard":false,"canRead":true,"source":"fixture","compatibility":"implemented"}]}""")!.AsObject();
        var subscriptions = 0; var releases = 0; var reads = 0; var schemaReads = 0; var refreshes = 0;
        var applyCount = 0; var active = false;
        TaskCompletionSource<bool>? saveStarted = null, saveRelease = null, readStarted = null;
        TaskCompletionSource<JsonObject>? heldRead = null, inFlightRead = null;
        JsonObject? submittedMedia = null;
        var layouts = new List<object>();

        JsonObject Schema()
        {
            var schema = (JsonObject)native.DeepClone();
            var fields = schema["fields"]!.AsArray();
            foreach (var device in PeripheralDevices(snapshot))
            {
                var id = "builtin_peripheral_" + Text(device["id"]);
                foreach (var (property, type, value) in new (string, string, JsonNode?)[] {
                    ("label", "text", JsonValue.Create(Text(device["name"]) + ":")),
                    ("show", "boolean", JsonValue.Create(false)), ("row", "number", JsonValue.Create(0)),
                    ("order", "number", JsonValue.Create(100 + fields.Count)) })
                {
                    var key = $"items.{id}.{property}";
                    if (fields.OfType<JsonObject>().Any(field => Text(field["key"]) == key)) continue;
                    fields.Add(new JsonObject { ["key"] = key, ["label"] = property, ["group"] = "items", ["type"] = type,
                        ["value"] = value, ["itemId"] = id, ["itemName"] = Text(device["name"]), ["sampleValue"] = "80%" });
                }
            }
            return schema;
        }

        window.ProbeRequestHandler = async (action, payload) => {
            switch (action)
            {
                case "subscribePeripheralBatteries":
                    active = payload?["active"]?.GetValue<bool>() == true;
                    if (active) subscriptions++; else releases++;
                    return new JsonObject();
                case "getPeripheralBatteries":
                case "refreshPeripheralBatteries":
                    if (!active) throw new InvalidOperationException("Peripheral probe read without scoped demand.");
                    reads++;
                    if (action == "refreshPeripheralBatteries") refreshes++;
                    if (heldRead is { } held)
                    {
                        heldRead = null;
                        inFlightRead = held;
                        readStarted?.TrySetResult(true);
                        try { return await held.Task; }
                        finally { inFlightRead = null; }
                    }
                    return (JsonObject)snapshot.DeepClone();
                case "getNativeSettings":
                    schemaReads++;
                    return Schema();
                case "applyNativeSettings":
                    native = Schema();
                    foreach (var pair in payload!.AsObject())
                    {
                        var field = native["fields"]!.AsArray().OfType<JsonObject>().SingleOrDefault(field => Text(field["key"]) == pair.Key);
                        if (field is null) throw new InvalidOperationException("Unknown synthetic native setting: " + pair.Key);
                        field["value"] = pair.Value?.DeepClone();
                    }
                    return (JsonObject)native.DeepClone();
                case "applyMediaSettings":
                    submittedMedia = (JsonObject)payload!.DeepClone();
                    foreach (var pair in payload.AsObject()) media[pair.Key] = pair.Value?.DeepClone();
                    applyCount++;
                    saveStarted?.TrySetResult(true);
                    if (saveRelease is { } release) await release.Task;
                    return (JsonObject)media.DeepClone();
                default: throw new InvalidOperationException("Unexpected peripheral probe request: " + action);
            }
        };

        try
        {
            Directory.CreateDirectory(outputDirectory);
            UiTemplateProbe.ShowOffscreen(window, 1180, 1000);
            await Task.Delay(120);
            window.LoadValidationData(Schema(), media);
            await WaitPeripheralProbeAsync(() => window._peripheralSession is { Reading: false, SubscriptionConfirmed: true } && schemaReads == 1);
            var session = window._peripheralSession!;
            if (window._currentPage != "taskbar" || subscriptions != 1 || releases != 0 || window.QuickCardEnabled("peripheralBattery"))
                throw new InvalidOperationException("Taskbar page did not own demand with the battery card disabled.");
            var beforePoll = reads;
            var renders = window.PageRenderCount;
            await WaitPeripheralProbeAsync(() => reads > beforePoll && !session.Reading);
            if (window.PageRenderCount != renders || schemaReads != 1)
                throw new InvalidOperationException("Unchanged inventory polling rebuilt taskbar settings or reloaded its schema.");

            var item = "builtin_peripheral_" + mouse;
            window._layout!.Move(item, 1, 0);
            window.OpenModule(item);
            window._root.UpdateLayout();
            var label = WalkTheme(window._properties).OfType<TextBox>().Single(control =>
                AutomationProperties.GetAutomationId(control) == $"setting-items.{item}.label");
            label.Text = "未应用的标签";
            await Task.Delay(20);
            var layoutDraft = window._native.Capture().Values;
            snapshot["devices"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"{{keyboard}}","name":"测试键盘 · 长设备名称用于检查窄窗口布局","kind":"keyboard","connection":"usb","state":"disconnected","batteries":[{"role":"main","percentage":50,"charging":false}],"stale":true,"showInCard":true,"canRead":true,"source":"fixture","compatibility":"implemented"}"""));
            snapshot["revision"] = 2;
            await WaitPeripheralProbeAsync(() => schemaReads == 2 && !session.Reading && window._layout!.Modules.Any(module => module.Id == "builtin_peripheral_" + keyboard));
            if (layoutDraft.Any(pair => !JsonNode.DeepEquals(window._native.Get(pair.Key), pair.Value)))
                throw new InvalidOperationException("Inventory update lost a taskbar layout or property draft.");
            session.Timer.Stop();

            window.ShowPage("peripheralBattery");
            await Task.Delay(120);
            window._root.UpdateLayout();
            var page = window._peripheralPage ?? throw new InvalidOperationException("Peripheral page did not render.");
            if (!ReferenceEquals(session, window._peripheralSession) || subscriptions != 1 || releases != 0 || page.Rows.Count != 3)
                throw new InvalidOperationException("Taskbar-to-peripheral navigation replaced the shared demand lease or lost devices.");
            if (page.Rows[mouse].Battery.Text.Contains("0%", StringComparison.Ordinal) ||
                !page.Rows[mouse].Battery.Text.Contains("—", StringComparison.Ordinal) ||
                !page.Rows[earbuds].Battery.Text.Contains("左耳 0%", StringComparison.Ordinal) ||
                !page.Rows[earbuds].Battery.Text.Contains("≈80%", StringComparison.Ordinal) ||
                !page.Rows[keyboard].Details.Text.Contains("已断开", StringComparison.Ordinal))
                throw new InvalidOperationException("Battery rows confused unknown, zero, estimated or disconnected states.");
            page.Mode.SelectedIndex = 1;
            page.Options["hideDisconnected"].IsOn = true;
            page.Options["headsetControlEnabled"].IsOn = true;
            page.Options["vendorExtensionsEnabled"].IsOn = true;
            page.Rows[mouse].Visible.IsOn = false;
            page.Rows[earbuds].Visible.IsOn = true;
            window.ReorderPeripheral(page, earbuds, 0);
            if (window.PeripheralVisibleOrder(page)[0] != earbuds || window.PeripheralDraft.Visible(mouse, true) ||
                !window.PeripheralDraft.Visible(earbuds, false) || window.PeripheralDraft.Mode != "detailed" || applyCount != 0)
                throw new InvalidOperationException("Peripheral mode, visibility or reorder bypassed the ordinary settings draft.");
            var sections = window._page.Children.ToArray();
            var row = page.Rows[earbuds].Surface;
            renders = window.PageRenderCount;
            snapshot["revision"] = 3;
            snapshot["devices"]![1]!["batteries"]![0]!["percentage"] = 1;
            await window.ReadPeripheralBatteriesAsync(session, refresh: true);
            window.RenderPage();
            if (schemaReads != 2 || refreshes != 1 || window.PageRenderCount != renders ||
                !window._page.Children.SequenceEqual(sections) || !ReferenceEquals(row, page.Rows[earbuds].Surface) ||
                page.Mode.SelectedIndex != 1 || page.Rows[mouse].Visible.IsOn || !page.Rows[earbuds].Visible.IsOn)
                throw new InvalidOperationException("Snapshot refresh rebuilt editors or overwrote peripheral drafts.");

            saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            saveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var save = window.SaveAsync();
            await WaitPeripheralProbeAsync(() => saveStarted.Task.IsCompleted);
            page.Mode.SelectedIndex = 0; // New edit while the previous revision is saving.
            saveRelease.TrySetResult(true);
            await save;
            if (submittedMedia?["peripheralBattery"] is not JsonObject submitted || Text(submitted["mode"]) != "detailed" ||
                submittedMedia.Count != 1 || window.PeripheralDraft.Mode != "compact" || !window._media.Pending.ContainsKey("peripheralBattery"))
                throw new InvalidOperationException("Global Apply lost the whole-object payload or a newer nested edit.");
            saveRelease = null;
            await window.SaveAsync();
            if (window._media.Pending.Count != 0 || window.PeripheralDraft.Mode != "compact" || applyCount != 2)
                throw new InvalidOperationException("Second global Apply did not acknowledge the preserved draft.");

            foreach (var width in new[] { 720, 1180 })
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.AppWindow.Resize(new SizeInt32(width, 1100));
                window._root.RequestedTheme = theme;
                page.Mode.SelectedIndex = theme == ElementTheme.Dark ? 1 : 0;
                window._pageScroll!.ChangeView(null, 0, null, true);
                await Task.Delay(120);
                window._root.UpdateLayout();
                AssertPeripheralProbeRows(page);
                if (page.Mode.ActualWidth <= 0 || page.Mode.ActualHeight <= 0 || page.Options.Values.Any(toggle => toggle.ActualWidth <= 0))
                    throw new InvalidOperationException("Peripheral settings controls did not realize their templates.");
                var png = Path.Combine(outputDirectory, $"settings-peripheral-battery-{theme.ToString().ToLowerInvariant()}-{width}.png");
                await UiLayoutProbe.CaptureAsync(window._root, png);
                page.Rows[keyboard].Surface.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = .5 });
                await Task.Delay(120);
                window._root.UpdateLayout();
                var devicesPng = Path.Combine(outputDirectory, $"settings-peripheral-devices-{theme.ToString().ToLowerInvariant()}-{width}.png");
                await UiLayoutProbe.CaptureAsync(window._root, devicesPng);
                layouts.Add(new { requestedPhysicalWidth = width, theme = theme.ToString(), mode = window.PeripheralDraft.Mode,
                    rowCount = page.Rows.Count, rowWidth = row.ActualWidth, modeWidth = page.Mode.ActualWidth, png, devicesPng, systemBackdropCaptured = false });
                UiTemplateProbe.AssertOwnedWindowsOffscreen();
            }

            session.Timer.Stop();
            heldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnectReply = heldRead;
            readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = window.ReadPeripheralBatteriesAsync(session);
            await WaitPeripheralProbeAsync(() => readStarted.Task.IsCompleted);
            var generation = session.Generation;
            var oldSnapshot = session.Snapshot;
            active = false; // Simulate the server releasing the disconnected lease.
            window.OnConnectionChanged(false);
            await WaitPeripheralProbeAsync(() => session.Generation != generation);
            disconnectReply.TrySetResult(new JsonObject { ["version"] = 1, ["revision"] = 999, ["devices"] = new JsonArray() });
            await pending;
            if (!ReferenceEquals(session.Snapshot, oldSnapshot) || page.Rows.Count != 3)
                throw new InvalidOperationException("A disconnected request overwrote the last peripheral snapshot.");
            window.OnConnectionChanged(true);
            await WaitPeripheralProbeAsync(() => session.SubscriptionConfirmed && !session.Reading && subscriptions == 2);
            session.Timer.Stop();

            heldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var leaveReply = heldRead;
            readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = window.ReadPeripheralBatteriesAsync(session);
            await WaitPeripheralProbeAsync(() => readStarted.Task.IsCompleted);
            window.ShowPage("sources");
            await WaitPeripheralProbeAsync(() => releases == 1 && !active);
            var schemaBeforeLateReply = schemaReads;
            leaveReply.TrySetResult(new JsonObject { ["version"] = 1, ["revision"] = 1000, ["devices"] = new JsonArray() });
            await pending;
            if (window._peripheralSession is not null || window._peripheralPage is not null || schemaReads != schemaBeforeLateReply || activated)
                throw new InvalidOperationException("A late peripheral reply resumed sampling, changed schema, or activated the probe.");
            return new { taskbarDemandScoped = true, sharedPageDemand = true, snapshotPolling = true, schemaChangesOnly = true,
                modes = true, perDeviceVisibility = true, reorder = true, draftPreservation = true, globalApply = true,
                newerEditPreserved = true, noPollingRebuild = true, unknownBatteryPreserved = true,
                disconnectCallbacksIgnored = true, leaveCallbacksIgnored = true, unsubscribedOnLeave = true,
                realHardwareOrSettingsTouched = false, probeActivated = activated, layouts };
        }
        finally
        {
            saveRelease?.TrySetResult(true);
            heldRead?.TrySetCanceled();
            inFlightRead?.TrySetCanceled();
            window.LeavePeripheralBatteryPage();
            window.Shutdown();
        }
    }

    private static async Task WaitPeripheralProbeAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Peripheral settings probe timed out.");
            await Task.Delay(20);
        }
    }

    private static void AssertPeripheralProbeRows(PeripheralBatteryPage page)
    {
        var widths = page.Rows.Values.Select(row => row.Surface.ActualWidth).ToArray();
        if (widths.Length != 3 || widths.Any(width => width <= 0 || Math.Abs(width - widths[0]) > 1))
            throw new InvalidOperationException("Peripheral device rows are not equal-width realized controls.");
        foreach (var row in page.Rows.Values)
        {
            if (row.Surface.ActualHeight <= 0 || row.Icon.ActualWidth <= 0 || row.Name.ActualWidth <= 0 ||
                row.Battery.ActualHeight <= 0 || row.Visible.ActualWidth <= 0 || row.Handle.ActualWidth <= 0 ||
                string.IsNullOrEmpty(AutomationProperties.GetName(row.Visible)))
                throw new InvalidOperationException("Peripheral row is missing its realized icon, labels, toggle or reorder handle.");
            foreach (var control in WalkTheme(row.Surface).OfType<Button>().Cast<FrameworkElement>().Append(row.Visible))
            {
                var bounds = control.TransformToVisual(row.Surface).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
                if (bounds.X < -1 || bounds.Right > row.Surface.ActualWidth + 1)
                    throw new InvalidOperationException("A peripheral row control exceeds its horizontal bounds.");
            }
        }
    }
}
