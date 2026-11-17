using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    internal static async Task<object> ProbeSettingsAutoSyncAsync()
    {
        using var connection = new UiConnection("TaskBarWidgets.UI.autosync-probe.disconnected");
        var window = new SettingsWindow(connection);
        var native = new JsonObject { ["groups"] = new JsonArray(), ["fields"] = new JsonArray() };
        var media = new JsonObject { ["toolWindowTheme"] = "System", ["showLyricTranslation"] = false };
        var writes = 0;
        var fail = false;
        TaskCompletionSource? release = null;
        window.ProbeRequestHandler = async (action, payload) =>
        {
            if (action == "getNativeSettings") return (JsonObject)native.DeepClone();
            if (action == "getMediaSettings") return (JsonObject)media.DeepClone();
            if (action != "applyMediaSettings") throw new InvalidOperationException("Unexpected autosync request: " + action);
            writes++;
            if (fail) throw new IOException("synthetic save failure");
            if (release is not null) await release.Task;
            foreach (var pair in payload!.AsObject()) media[pair.Key] = pair.Value?.DeepClone();
            return (JsonObject)media.DeepClone();
        };
        try
        {
            window.NavigateTo("displayArea");
            window.LoadValidationData(native, media);
            UiTemplateProbe.ShowOffscreen(window, 1180, 820);
            await Task.Delay(100);
            window._settingsSyncEnabled = true;
            var renders = window.PageRenderCount;
            var page = window._page.Children.First();
            var editor = WalkTheme(window._page).OfType<NumberBox>()
                .Single(box => AutomationProperties.GetAutomationId(box) == "setting-coverSize");
            window._split.IsPaneOpen = true;
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editor.Value = 44;
            editor.Value = 45;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (writes == 0) await Task.Delay(20, timeout.Token);
            if (writes != 1) throw new InvalidOperationException("Autosave did not coalesce edits.");
            editor.Value = 52; // A second edit arrives while the first save awaits readback.
            release.SetResult();
            while (window._media.Pending.Count > 0 || !window._settingsSyncTask.IsCompleted)
                await Task.Delay(20, timeout.Token);
            release = null;
            if (writes != 2 || editor.Value != 52 || media["coverSize"]?.GetValue<double>() != 52 ||
                window.PageRenderCount != renders || !ReferenceEquals(page, window._page.Children.First()) || !window._split.IsPaneOpen)
                throw new InvalidOperationException("Save/readback lost newer edits or rebuilt the current page/drawer.");
            media["coverSize"] = 60;
            window.QueueSettingsSync(read: true);
            while (editor.Value != 60) await Task.Delay(20, timeout.Token);
            if (window.PageRenderCount != renders || window._media.Pending.Count != 0 || writes != 2)
                throw new InvalidOperationException("Automatic readback rebuilt controls or generated a write.");
            fail = true;
            editor.Value = 70;
            while (!window._syncFailed) await Task.Delay(20, timeout.Token);
            if (editor.Value != 70 || !window._media.Pending.ContainsKey("coverSize") || window._retrySync.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Failed autosave lost the edit or retry feedback.");
            fail = false;
            await window.FlushSettingsAsync();
            if (window._syncFailed || window._media.Pending.Count != 0 || media["coverSize"]?.GetValue<double>() != 70)
                throw new InvalidOperationException("Autosave retry did not persist the retained edit.");
            editor.Value = 80;
            await window.CloseWithPendingChangesAsync();
            if (!window._shutdown || media["coverSize"]?.GetValue<double>() != 80)
                throw new InvalidOperationException("Closing did not flush the last debounced edit.");
            return new { coalescedEdits = true, newerEditsPreserved = true, inPlaceReadback = true,
                failureRetainsDraft = true, closeFlushesChanges = true, realSettingsTouched = false };
        }
        finally { if (!window._closed) window.Shutdown(); }
    }
}
