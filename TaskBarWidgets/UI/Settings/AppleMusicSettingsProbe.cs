using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    // Real, already-offscreen WinUI controls; no player, backend, or playback action.
    internal static async Task<object> ProbeAppleMusicControlsAsync(SettingsWindow window, string output)
    {
        var originalPage = window._currentPage;
        var originalBase = window._media.Keys.ToDictionary(key => key, key => window._media.GetSaved(key));
        var originalDraft = window._media.Capture();
        var originalSessions = window._serviceEvents.GetValueOrDefault("playerSessions")?.DeepClone();
        try
        {
            string[] legacyOrder = ["Browser", "QQMusic", "Netease", "Kugou", "Spotify"];
            window._media.SetNode("sourceRecognitionOrder", new JsonArray(legacyOrder.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()));
            window._media.SetNode("customPlayerSources", new JsonArray());
            window._media.Set("enableAppleMusic", true);
            window._serviceEvents["playerSessions"] = new JsonObject { ["sessions"] = new JsonArray() };
            window.ShowPage("sources");
            await Task.Delay(120);
            window._root.UpdateLayout();
            var row = window._sourceRows.Single(item => item.Id == "AppleMusic");
            if (!row.Surface.IsLoaded || row.Surface.ActualWidth <= 0 || row.Surface.ActualHeight <= 0)
                throw new InvalidOperationException("The built-in Apple Music row was not realized while no players were running.");
            var toggle = WalkTheme(row.Surface).OfType<ToggleSwitch>().Single(control =>
                AutomationProperties.GetAutomationId(control) == "source-enabled-AppleMusic");
            if (!toggle.IsOn || !WalkTheme(row.Surface).OfType<TextBlock>().Any(control => control.Text == "Apple Music"))
                throw new InvalidOperationException("Apple Music default enablement or display name was incorrect.");
            toggle.IsOn = false;
            if (window._media.Bool("enableAppleMusic", true)) throw new InvalidOperationException("Apple Music disable did not reach its settings draft.");
            toggle.IsOn = true;
            if (!window._media.Bool("enableAppleMusic")) throw new InvalidOperationException("Apple Music enable did not reach its settings draft.");
            var order = window.SourceList().Select(item => item.Id).ToArray();
            if (!order.Take(legacyOrder.Length).SequenceEqual(legacyOrder) || order.Count(id => id == "AppleMusic") != 1)
                throw new InvalidOperationException("Adding the Apple Music row changed existing source order.");
            // Changed() may replace a row, and ToggleSwitch animates its thumb.
            // Capture the current realized control after the enabled state settles.
            await Task.Delay(240);
            window._root.UpdateLayout();
            row = window._sourceRows.Single(item => item.Id == "AppleMusic");
            toggle = WalkTheme(row.Surface).OfType<ToggleSwitch>().Single(control =>
                AutomationProperties.GetAutomationId(control) == "source-enabled-AppleMusic");
            if (!row.Surface.IsLoaded || !toggle.IsOn || !window._media.Bool("enableAppleMusic"))
                throw new InvalidOperationException("The current Apple Music row did not settle in its enabled state before capture.");
            var path = Path.Combine(output, "apple-music-player-settings.png");
            Directory.CreateDirectory(output);
            await UiLayoutProbe.CaptureAsync(row.Surface, path);
            string[] providerOrder = ["QQMusic", "Kugou", "Netease", "LRCLIB"];
            window._media.Set("playerLyricOffset:AppleMusic", 0);
            window._media.SetNode("playerLyricProviders:AppleMusic", new JsonArray(providerOrder.Select(id =>
                (JsonNode?)new JsonObject { ["providerId"] = id, ["enabled"] = true }).ToArray()));
            window.OpenSource("AppleMusic");
            await Task.Delay(120);
            window._root.UpdateLayout();
            var offset = WalkTheme(window._properties).OfType<NumberBox>().Single(control =>
                AutomationProperties.GetAutomationId(control) == "setting-playerLyricOffset:AppleMusic");
            if (!offset.IsLoaded || offset.ActualWidth <= 0 || offset.Minimum != -5000 || offset.Maximum != 5000)
                throw new InvalidOperationException("Apple Music lyric offset was not available in the source properties.");
            offset.Value = 250;
            if (window._media.Number("playerLyricOffset:AppleMusic") != 250)
                throw new InvalidOperationException("Apple Music lyric offset did not reach its settings draft.");
            const string providerPrefix = "lyric-provider-enabled-AppleMusic-";
            var providerToggles = WalkTheme(window._properties).OfType<ToggleSwitch>().Where(control =>
                AutomationProperties.GetAutomationId(control).StartsWith(providerPrefix, StringComparison.Ordinal)).ToArray();
            if (!providerToggles.Select(control => AutomationProperties.GetAutomationId(control)[providerPrefix.Length..]).SequenceEqual(providerOrder) ||
                providerToggles.Any(control => !control.IsLoaded || control.ActualWidth <= 0 || !control.IsOn))
                throw new InvalidOperationException("Apple Music did not expose all four lyric providers in their configured order.");
            providerToggles[1].IsOn = false;
            var providers = (JsonArray)window._media.Get("playerLyricProviders:AppleMusic")!;
            if (providers[1]!["enabled"]!.GetValue<bool>() || providers.Where((_, index) => index != 1).Any(item => !item!["enabled"]!.GetValue<bool>()))
                throw new InvalidOperationException("The Apple Music lyric provider switch did not remain independent.");
            var move = WalkTheme(window._properties).OfType<Button>().Single(control =>
                AutomationProperties.GetName(control) == "下移QQ 音乐");
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(move);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(100);
            window._root.UpdateLayout();
            providers = (JsonArray)window._media.Get("playerLyricProviders:AppleMusic")!;
            if (!providers.Select(item => Text(item!["providerId"])).SequenceEqual(new[] { "Kugou", "QQMusic", "Netease", "LRCLIB" }) ||
                providers[0]!["enabled"]!.GetValue<bool>() || window._media.Number("playerLyricOffset:AppleMusic") != 250)
                throw new InvalidOperationException("Reordering Apple Music lyric providers discarded their enablement or offset draft.");
            return new { visibleWhenStopped = true, enabledToggle = true, orderPreserved = true,
                displayName = true, canonicalSourceId = Players.Single(player => player.Id == "AppleMusic").Key == "enableAppleMusic",
                sourceId = "AppleMusic", isOn = toggle.IsOn, lyricsOffsetEditable = true, lyricProvidersEditable = true,
                lyricProviderOrderPreserved = true, providerCount = providerToggles.Length,
                width = row.Surface.ActualWidth, height = row.Surface.ActualHeight, png = path };
        }
        finally
        {
            window._media.Readback(originalBase);
            foreach (var item in originalDraft.Values) window._media.SetNode(item.Key, item.Value);
            if (originalSessions is null) window._serviceEvents.Remove("playerSessions");
            else window._serviceEvents["playerSessions"] = originalSessions;
            window._split.IsPaneOpen = false;
            window.ShowPage(originalPage);
        }
    }
}
