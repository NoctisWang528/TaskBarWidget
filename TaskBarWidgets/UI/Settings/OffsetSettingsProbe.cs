using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TaskBarWidgets.UI;

internal static class OffsetSettingsProbe
{
    internal static async Task<object> RunAsync(SettingsWindow window)
    {
        var commands = new List<string>();
        var queries = new List<(int RequestId, int Page, string Search)>();
        var activations = 0;
        var firstRequestRenderCount = -1;
        TaskCompletionSource<bool>? heldStarted = null;
        TaskCompletionSource<bool>? heldRelease = null;
        string? heldSearch = null;

        window.ProbeRequestHandler = async (request, payload) =>
        {
            if (request != "settingsCommand")
                throw new InvalidOperationException($"Unexpected settings request: {request}.");
            var command = payload?["action"]?.ToString() ?? "";
            var value = payload?["value"] as JsonObject;
            commands.Add(command);
            if (firstRequestRenderCount < 0) firstRequestRenderCount = window.PageRenderCount;
            var events = new JsonArray();
            if (command == "trackOffsetsPageActivated")
            {
                activations++;
                events.Add(new JsonObject
                {
                    ["type"] = "currentTrackOffset",
                    ["payload"] = new JsonObject
                    {
                        ["title"] = "Probe Song",
                        ["artist"] = "Probe Artist",
                        ["lyricSource"] = "Probe",
                        ["playerOffsetMilliseconds"] = 0,
                        ["trackOffsetMilliseconds"] = 120
                    }
                });
            }
            else if (command == "queryTrackOffsets")
            {
                var requestId = value?["requestId"]?.GetValue<int>() ?? -1;
                var page = value?["page"]?.GetValue<int>() ?? 1;
                var search = value?["search"]?.ToString() ?? "";
                queries.Add((requestId, page, search));
                if (heldSearch == search && heldRelease is not null)
                {
                    heldSearch = null;
                    heldStarted?.TrySetResult(true);
                    await heldRelease.Task;
                }
                var savedTitle = search switch
                {
                    "first" => "First Saved Track",
                    "second" => "Second Saved Track",
                    "stale" => "Stale Saved Track",
                    _ => "Saved Track"
                };
                events.Add(new JsonObject
                {
                    ["type"] = "trackOffsetEntries",
                    ["payload"] = new JsonObject
                    {
                        ["requestId"] = requestId,
                        ["entries"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["key"] = "probe-key-" + search,
                                ["title"] = savedTitle,
                                ["artist"] = "Saved Artist",
                                ["offsetMilliseconds"] = 80,
                                ["lyricSource"] = "Probe",
                                ["sourceApp"] = "Probe"
                            }
                        },
                        ["page"] = page,
                        ["pageCount"] = 2,
                        ["totalCount"] = 1
                    }
                });
            }
            else
            {
                throw new InvalidOperationException($"Unexpected settings command: {command}.");
            }
            return new JsonObject { ["events"] = events };
        };

        try
        {
            window.NavigateTo("taskbar");
            await Task.Delay(20);

            var beforeEntry = window.PageRenderCount;
            window.NavigateTo("trackOffsets");
            await WaitUntilAsync(() => activations == 1 && queries.Count == 1 && window.PageRenderCount >= beforeEntry + 2);
            if (!commands.SequenceEqual(["trackOffsetsPageActivated", "queryTrackOffsets"]))
                throw new InvalidOperationException("Initial offsets entry did not issue exactly one activation followed by one query.");
            if (firstRequestRenderCount != beforeEntry + 1 || window.PageRenderCount != beforeEntry + 2)
                throw new InvalidOperationException("Initial offsets entry did not render once before querying and once after both responses.");
            if (queries[0].RequestId <= 0)
                throw new InvalidOperationException("Track-offset queries did not carry a positive requestId.");
            AssertOffsetContent(window, "Probe Song", "Saved Track");

            var beforeRedraw = window.PageRenderCount;
            var commandCount = commands.Count;
            window.ProbeRenderPage();
            await Task.Delay(100);
            if (window.PageRenderCount != beforeRedraw + 1 || commands.Count != commandCount)
                throw new InvalidOperationException("A normal page render issued an activation or offset query.");

            var beforeReopen = window.PageRenderCount;
            commandCount = commands.Count;
            var activationsBeforeReopen = activations;
            var queriesBeforeReopen = queries.Count;
            window.NavigateTo("trackOffsets");
            await WaitUntilAsync(() => commands.Count >= commandCount + 2 && window.PageRenderCount >= beforeReopen + 2);
            if (commands.Count != commandCount + 2 ||
                commands[^2] != "trackOffsetsPageActivated" ||
                commands[^1] != "queryTrackOffsets" ||
                activations != activationsBeforeReopen + 1 ||
                queries.Count != queriesBeforeReopen + 1 ||
                window.PageRenderCount != beforeReopen + 2)
                throw new InvalidOperationException("Reopening the same offsets page did not issue exactly one activation and one final render.");

            var beforeSearch = window.PageRenderCount;
            commandCount = commands.Count;
            var activationsBeforeSearch = activations;
            await window.ProbeLoadOffsetsAsync("needle", 1);
            if (window.PageRenderCount != beforeSearch + 1 ||
                commands.Count != commandCount + 1 ||
                commands[^1] != "queryTrackOffsets" ||
                activations != activationsBeforeSearch ||
                queries[^1] is not { Page: 1, Search: "needle" })
                throw new InvalidOperationException("Offset search did not issue one query without re-activating the page.");
            AssertOffsetContent(window, "Probe Song", "Saved Track");

            var beforePage = window.PageRenderCount;
            commandCount = commands.Count;
            var pagedRequestId = queries[^1].RequestId;
            await window.ProbeLoadOffsetsAsync("needle", 2);
            if (window.PageRenderCount != beforePage + 1 ||
                commands.Count != commandCount + 1 ||
                commands[^1] != "queryTrackOffsets" ||
                queries[^1] is not { Page: 2, Search: "needle" } ||
                queries[^1].RequestId <= pagedRequestId)
                throw new InvalidOperationException("Offset paging did not issue one latest query or requestId was not monotonic.");

            var beforeStorm = window.PageRenderCount;
            commandCount = commands.Count;
            var queryCount = queries.Count;
            var activationCount = activations;
            window.NavigateTo("trackOffsets");
            window.NavigateTo("lyrics");
            window.NavigateTo("taskbar");
            window.NavigateTo("trackOffsets");
            var stormRenderCount = window.PageRenderCount;
            await WaitUntilAsync(() => commands.Count >= commandCount + 2 && window.PageRenderCount >= stormRenderCount + 1);
            if (commands.Count != commandCount + 2 ||
                activations != activationCount + 1 ||
                queries.Count != queryCount + 1 ||
                commands[^2] != "trackOffsetsPageActivated" ||
                commands[^1] != "queryTrackOffsets" ||
                queries[^1] is not { Page: 2, Search: "needle" } ||
                window.PageRenderCount != stormRenderCount + 1)
                throw new InvalidOperationException("A navigation storm was not coalesced into one latest activation batch.");

            heldStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            heldRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            heldSearch = "stale";
            var staleTask = window.ProbeQueueOffsetsAsync("stale", 1);
            await WaitUntilAsync(() => heldStarted.Task.IsCompleted);
            commandCount = commands.Count;
            // Restore the page's previous search before leaving so the next
            // explicit entry cannot legitimately return the held title.
            window.ProbeSetOffsetState("needle", 2);
            window.NavigateTo("lyrics");
            var renderAfterLeave = window.PageRenderCount;
            heldRelease.TrySetResult(true);
            if (await staleTask)
                throw new InvalidOperationException("A superseded offset query reported success.");
            await Task.Delay(120);
            if (window.PageRenderCount != renderAfterLeave || commands.Count != commandCount)
                throw new InvalidOperationException("A response from an offsets page that was left still rendered or issued commands.");

            var beforeReturn = window.PageRenderCount;
            commandCount = commands.Count;
            var returnActivationCount = activations;
            var returnQueryCount = queries.Count;
            window.NavigateTo("trackOffsets");
            await WaitUntilAsync(() => commands.Count >= commandCount + 2 && window.PageRenderCount >= beforeReturn + 2);
            if (activations != returnActivationCount + 1 ||
                queries.Count != returnQueryCount + 1 ||
                window.PageRenderCount != beforeReturn + 2)
                throw new InvalidOperationException("Returning to the offsets page did not start a fresh activation batch.");
            AssertOffsetContent(window, "Probe Song", "Saved Track");
            if (ContainsText(window, "Stale Saved Track"))
                throw new InvalidOperationException("A stale response was applied after leaving and reopening the page: " +
                    window.ProbeOffsetState + " | " +
                    string.Join(" | ", Walk((FrameworkElement)window.Content).OfType<TextBlock>().Select(block => block.Text).Where(text => text.Length > 0)));

            heldStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            heldRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            heldSearch = "first";
            var firstTask = window.ProbeQueueOffsetsAsync("first", 1);
            await WaitUntilAsync(() => heldStarted.Task.IsCompleted);
            var firstRequestId = queries[^1].RequestId;
            var beforeSecond = window.PageRenderCount;
            var secondTask = window.ProbeQueueOffsetsAsync("second", 1);
            heldRelease.TrySetResult(true);
            if (await firstTask)
                throw new InvalidOperationException("An expired requestId response was applied.");
            if (!await secondTask)
                throw new InvalidOperationException("The latest trailing offset query did not apply.");
            if (window.PageRenderCount != beforeSecond + 1)
                throw new InvalidOperationException("Overlapping offset queries rendered more than once.");
            if (queries[^2] is not { Search: "first" } || queries[^1] is not { Search: "second" } ||
                queries[^2].RequestId != firstRequestId || queries[^1].RequestId <= firstRequestId)
                throw new InvalidOperationException("Overlapping offset queries did not serialize in latest-wins order.");
            AssertOffsetContent(window, "Probe Song", "Second Saved Track");

            var requestIdsMonotonic = true;
            for (var index = 1; index < queries.Count; index++)
                requestIdsMonotonic &= queries[index].RequestId > queries[index - 1].RequestId;
            if (!requestIdsMonotonic)
                throw new InvalidOperationException("Offset requestIds were not monotonic.");

            return new
            {
                initialActivations = 1,
                initialBatches = 1,
                initialFinalRenders = 1,
                redrawQueries = 0,
                samePageReopenActivations = 1,
                stormActivations = 1,
                staleEntryIgnored = true,
                searchWithoutActivation = true,
                pagingWithoutActivation = true,
                staleRequestIgnored = true,
                requestIdsMonotonic = true,
                validBatchFinalRenders = 1,
                bothPayloadsVisible = true
            };
        }
        finally
        {
            window.ProbeRequestHandler = null;
            window.ShowPage("taskbar");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("Offset probe timed out waiting for its lifecycle response.");
            await Task.Delay(20);
        }
    }

    private static void AssertOffsetContent(SettingsWindow window, string currentTitle, string entryTitle)
    {
        var content = (FrameworkElement)window.Content;
        // RenderPage replaces the logical page immediately, but the visual tree
        // is refreshed on the next layout pass. Force that pass before walking it.
        content.UpdateLayout();
        if (!ContainsText(window, currentTitle) || !ContainsText(window, entryTitle))
            throw new InvalidOperationException($"Both track-offset response payloads were not realized on the page: {string.Join(" | ", Walk(content).OfType<TextBlock>().Select(block => block.Text).Where(text => text.Length > 0))}");
        var headers = Walk(content).OfType<Expander>()
            .Select(expander => expander.Header?.ToString() ?? "").ToArray();
        if (!headers.Any(value => value.Contains(entryTitle, StringComparison.Ordinal)))
            throw new InvalidOperationException("The saved offset entry was not realized in the offsets list.");
    }

    private static bool ContainsText(SettingsWindow window, string value) =>
        Walk((FrameworkElement)window.Content).OfType<TextBlock>()
            .Any(block => block.Text.Contains(value, StringComparison.Ordinal));

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
