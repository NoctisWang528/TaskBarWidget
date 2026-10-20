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
        var queries = new List<(int Page, string Search)>();
        var firstRequestRenderCount = -1;

        window.ProbeRequestHandler = async (request, payload) =>
        {
            await Task.Yield();
            if (request != "settingsCommand") throw new InvalidOperationException($"Unexpected settings request: {request}.");
            var command = payload?["action"]?.ToString() ?? "";
            commands.Add(command);
            if (firstRequestRenderCount < 0) firstRequestRenderCount = window.PageRenderCount;
            var value = payload?["value"] as JsonObject;
            var events = new JsonArray();
            if (command == "trackOffsetsPageActivated")
            {
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
                var page = value?["page"]?.GetValue<int>() ?? 1;
                var search = value?["search"]?.ToString() ?? "";
                queries.Add((page, search));
                events.Add(new JsonObject
                {
                    ["type"] = "trackOffsetEntries",
                    ["payload"] = new JsonObject
                    {
                        ["entries"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["key"] = "probe-key",
                                ["title"] = "Saved Track",
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
            var beforeEntry = window.PageRenderCount;
            window.NavigateTo("trackOffsets");
            await WaitUntilAsync(() => commands.Count >= 2 && window.PageRenderCount >= beforeEntry + 2);
            if (!commands.SequenceEqual(["trackOffsetsPageActivated", "queryTrackOffsets"]))
                throw new InvalidOperationException("Track offsets did not issue activation followed by one list query.");
            if (firstRequestRenderCount != beforeEntry + 1 || window.PageRenderCount != beforeEntry + 2)
                throw new InvalidOperationException("The initial offsets batch rendered more than one final page update.");
            AssertOffsetContent(window, "Probe Song", "Saved Track");

            var beforeSearch = window.PageRenderCount;
            var commandCount = commands.Count;
            await window.ProbeLoadOffsetsAsync("needle", 1);
            if (window.PageRenderCount != beforeSearch + 1 || commands.Count != commandCount + 2)
                throw new InvalidOperationException("Offset search did not commit as one two-command batch with one final render.");

            var beforePage = window.PageRenderCount;
            commandCount = commands.Count;
            await window.ProbeLoadOffsetsAsync("needle", 2);
            if (window.PageRenderCount != beforePage + 1 || commands.Count != commandCount + 2)
                throw new InvalidOperationException("Offset paging did not commit as one two-command batch with one final render.");
            if (!queries.Contains((2, "needle")))
                throw new InvalidOperationException("Offset paging did not send the selected page and search text.");
            AssertOffsetContent(window, "Probe Song", "Saved Track");

            return new
            {
                initialEntries = 2,
                initialFinalRenders = 1,
                searchFinalRenders = 1,
                pageFinalRenders = 1,
                bothPayloadsVisible = true,
                batchedCommands = commands.Count / 3
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
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Offset probe timed out waiting for its batched responses.");
            await Task.Delay(20);
        }
    }

    private static void AssertOffsetContent(SettingsWindow window, string currentTitle, string entryTitle)
    {
        var content = (FrameworkElement)window.Content;
        var text = Walk(content).OfType<TextBlock>().Select(block => block.Text).Where(value => value.Length > 0).ToArray();
        var headers = Walk(content).OfType<Expander>().Select(expander => expander.Header?.ToString() ?? "").ToArray();
        if (!text.Any(value => value.Contains(currentTitle, StringComparison.Ordinal)) ||
            !headers.Any(value => value.Contains(entryTitle, StringComparison.Ordinal)))
            throw new InvalidOperationException("Both track-offset response payloads were not realized on the page.");
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
