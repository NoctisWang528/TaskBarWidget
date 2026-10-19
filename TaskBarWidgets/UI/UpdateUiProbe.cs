using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace TaskBarWidgets.UI;

public partial class App
{
    internal object ProbeUpdateStateOrdering()
    {
        var original = _updateState;
        try
        {
            AcceptUpdateState(JsonSerializer.SerializeToElement(new { revision = 10, phase = "ready", canInstall = true }));
            AcceptUpdateState(JsonSerializer.SerializeToElement(new { payload = new { revision = 11, phase = "downloading", canInstall = false } }));
            AcceptUpdateState(JsonSerializer.SerializeToElement(new { revision = 10, phase = "ready", canInstall = true }));
            AcceptUpdateState(JsonSerializer.SerializeToElement(new { revision = 11, phase = "ready", canInstall = true }));
            if (_updateState.Revision != 11 || _updateState.CanInstall) throw new InvalidOperationException("Stale or equal-revision update state overwrote newer progress.");
            return new { payloadFallback = true, staleAndEqualRevisionsIgnored = true };
        }
        finally { _updateState = original; }
    }
}

public sealed partial class CardGroup
{
    internal static async Task<object> ProbeUpdateCardStateAsync(CardGroup cards)
    {
        // An update that is merely available must already surface the notice
        // card; only the install action waits for the downloaded/verified state.
        cards.SetUpdateState(new UpdateUiState(9, "available", LatestVersion: "991231-2359", CanDownload: true));
        if (cards.CardWindows.Count != 5 || cards._updateInstall.IsEnabled)
            throw new InvalidOperationException("An available update did not surface the update card.");
        var ready = new UpdateUiState(10, "ready", LatestVersion: "991231-2359", CanInstall: true);
        cards.SetUpdateState(ready);
        if (cards.CardWindows.Count != 5 || !ReferenceEquals(cards.CardWindows[^1], cards._updateCard) ||
            cards._updateCard.Height != 180 || IsWindowVisible(cards._updateCard.Handle) || !cards._updateInstall.IsEnabled)
            throw new InvalidOperationException("Ready update card must be the second column and stay hidden until the group is opened.");
        cards.Position(100, 100);
        var bounds = cards._updateCard.Window.AppWindow;
        // The update card is an independent rightmost column and bottom-aligns
        // with the tallest base column, not with the last card in flat order.
        var baseWindows = cards.EnabledCards.ToArray();
        var lastBase = baseWindows[0].Window.AppWindow;
        var baseBottom = baseWindows.Max(card => card.Window.AppWindow.Position.Y + card.Window.AppWindow.Size.Height);
        var expectedUpdateWidth = (int)Math.Floor(220 * (lastBase.Size.Width / (double)CardGroup.CardWidth));
        if (Math.Abs(bounds.Size.Width - expectedUpdateWidth) > 1 || bounds.Position.X <= lastBase.Position.X ||
            Math.Abs(bounds.Position.Y + bounds.Size.Height - baseBottom) > 2)
            throw new InvalidOperationException($"Optional update card did not participate in two-column placement: update={bounds.Position.X},{bounds.Position.Y},{bounds.Size.Width}x{bounds.Size.Height} last={lastBase.Position.X},{lastBase.Position.Y},{lastBase.Size.Width}x{lastBase.Size.Height}");
        cards.SetThemePreview(ElementTheme.Light);
        if (cards._updateCard.Root.RequestedTheme != ElementTheme.Light) throw new InvalidOperationException("Optional update card missed the settings theme preview.");
        cards.SetThemePreview(ElementTheme.Dark);
        UiTemplateProbe.PreventProbeActivation(cards._updateCard.Window);
        cards._updateCard.Window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-30000, -30000, 220, 180));
        cards._updateCard.Show();
        await Task.Delay(80);
        cards._updateCard.Root.UpdateLayout();
        if (!cards._updateCard.Root.IsLoaded || cards._updateCard.Root.ActualWidth <= 0)
            throw new InvalidOperationException("Optional update card template was not realized.");
        var position = cards._updateCard.Window.AppWindow.Position;
        if (cards.ShouldDismissClick(new Cards.OutsideClickMonitor.Click(position.X + 5, position.Y + 5,
            Cards.OutsideClickMonitor.Button.Left, 0, 0, 0, 0, false, false, false)))
            throw new InvalidOperationException("Clicking the optional update card would dismiss the group.");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) {
            cards.SetThemePreview(theme); await Task.Delay(40);
            if (cards._updateCard.Root.ActualTheme != theme) throw new InvalidOperationException("Optional update card actual theme did not refresh.");
        }
        var requested = 0;
        void Install() => requested++;
        cards.InstallUpdateRequested += Install;
        try {
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(cards._updateInstall).GetPattern(PatternInterface.Invoke);
            invoke.Invoke(); invoke.Invoke(); await Task.Delay(40);
            if (requested != 2) throw new InvalidOperationException("Update card did not route clicks to App confirmation.");
        }
        finally { cards.InstallUpdateRequested -= Install; }
        await cards._updateCard.HideAsync(animate: false);
        cards.SetUpdateState(ready with { Phase = "preparing", CanInstall = false });
        if (cards.CardWindows.Count != 4) throw new InvalidOperationException("Preparing update card did not disappear.");
        return new { optionalCardAppears = true, availableSurfacesCard = true, preparingCardRemoved = true, hiddenUntilOpened = true, secondColumn = true, themePreview = true };
    }
}

public sealed partial class SettingsWindow
{
    internal static async Task<object> ProbeUpdateControlsAsync(SettingsWindow window)
    {
        var page = window._currentPage;
        var original = window._updateState;
        var nativeDraft = window._native.Get("update-probe.draft");
        var ready = new UpdateUiState(11, "ready", LatestVersion: "991231-2359", Notes: "Synthetic release notes", CanInstall: true);
        var requested = 0;
        void Install() => requested++;
        window.InstallUpdateRequested += Install;
        try
        {
            window.ShowPage("about");
            window.SetUpdateState(ready);
            window._root.UpdateLayout();
            if (window._automaticUpdates?.IsChecked != true || window._installUpdate?.IsEnabled != true)
                throw new InvalidOperationException("Updater defaults and ready actions were not rendered.");
            window._native.Set("update-probe.draft", "unfinished");
            if (!window.HasPendingChanges) throw new InvalidOperationException("Pending settings did not block update installation.");
            var notes = window._updateNotes;
            window.SetUpdateState(ready with { Revision = 12, Phase = "downloading", CanInstall = false, BytesDownloaded = 1024, TotalBytes = 4096 });
            if (!ReferenceEquals(notes, window._updateNotes) || window._native.Text("update-probe.draft") != "unfinished" || window._updateProgress?.Value != 25)
                throw new InvalidOperationException("Update progress rebuilt settings or discarded a draft.");
            window.SetUpdateState(ready with { Revision = 13 });
            var peer = new ButtonAutomationPeer(window._installUpdate!);
            var invoke = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
            invoke.Invoke(); invoke.Invoke(); await Task.Delay(40);
            if (requested != 2) throw new InvalidOperationException("Install actions must route through the App-owned confirmation event.");
            window._native.SetNode("update-probe.draft", nativeDraft);
            var nativePending = window._native.Pending.ToArray(); var mediaPending = window._media.Pending.ToArray();
            window._native.Pending.Clear(); window._media.Pending.Clear();
            try {
                window._weatherPage = new WeatherEditor(window._lifetime.Token) { Committing = true };
                if (!window.HasPendingChanges) throw new InvalidOperationException("Weather operations were omitted from install guard.");
                window._weatherPage.Committing = false;
                window._weatherPage.ApiKey.Password = "synthetic-unsaved-key";
                if (!window.HasPendingChanges) throw new InvalidOperationException("Unsaved weather credentials were omitted from install guard.");
            }
            finally {
                window.CancelWeatherPage();
                foreach (var pair in nativePending) window._native.Pending[pair.Key] = pair.Value;
                foreach (var pair in mediaPending) window._media.Pending[pair.Key] = pair.Value;
            }
            if (UpdateUiState.Parse(JsonSerializer.SerializeToElement(new { revision = "invalid" })) is not null)
                throw new InvalidOperationException("Malformed update revision was accepted.");
            return new { automaticOnByDefault = true, inPlaceProgress = true, draftRetained = true, installDelegatesToApp = true, weatherOperationGuard = true };
        }
        finally
        {
            window.CancelWeatherPage();
            window.InstallUpdateRequested -= Install;
            window._native.SetNode("update-probe.draft", nativeDraft);
            window.SetUpdateState(original);
            window.ShowPage(page);
        }
    }
}
