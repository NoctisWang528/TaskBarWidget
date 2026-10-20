using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using TaskBarWidgets.UI.Cards;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    internal static async Task<object> ProbeMicaControlsAsync(SettingsWindow window, CardGroup cards)
    {
        var clampedLow = CardMicaTuning.FromUserValues(-10, -10);
        var clampedHigh = CardMicaTuning.FromUserValues(110, 110);
        var darkEdge = CardMicaTuning.FromValues(
            CardMicaTuning.MinimumTintOpacity,
            CardMicaTuning.MinimumLuminosityOpacity);
        var missingLuminosity = CardMicaTuning.FromJson(
            JsonSerializer.SerializeToElement(new { tintOpacity = 0.08 }), dark: true);
        if (!clampedLow.Matches(CardMicaTuning.FromValues(
                CardMicaTuning.MinimumTintOpacity,
                CardMicaTuning.MaximumLuminosityOpacity)) ||
            !clampedHigh.Matches(CardMicaTuning.FromValues(
                CardMicaTuning.MaximumTintOpacity,
                CardMicaTuning.MinimumLuminosityOpacity)) ||
            Math.Abs(darkEdge.TintOpacityFor(true) - CardMicaTuning.MinimumTintOpacity) > 0.0001 ||
            Math.Abs(darkEdge.LuminosityOpacityFor(true) - CardMicaTuning.MinimumLuminosityOpacity) > 0.0001 ||
            Math.Abs(missingLuminosity.TintOpacity - 0.08) > 0.0001 ||
            Math.Abs(missingLuminosity.LuminosityOpacity - CardMicaTuning.DefaultDarkLuminosityOpacity) > 0.0001 ||
            Math.Abs(CardMicaTuning.DefaultFor(false).TintOpacity - 0.45) > 0.0001 ||
            Math.Abs(CardMicaTuning.DefaultFor(false).LuminosityOpacity - 0.85) > 0.0001 ||
            Math.Abs(CardMicaTuning.DefaultFor(true).TintOpacityFor(true) - 0.43) > 0.0001 ||
            Math.Abs(CardMicaTuning.DefaultFor(true).LuminosityOpacityFor(true) - 0.48) > 0.0001 ||
            VividMicaBackdrop.KindFor(false) != MicaKind.Base ||
            VividMicaBackdrop.KindFor(true) != MicaKind.BaseAlt)
            throw new InvalidOperationException("Card Mica limits, fallbacks or theme kinds were not normalized.");
        if (window._cardMicaPreviewBackdrop.ForcesInputActive)
            throw new InvalidOperationException("The settings-window Mica preview must retain normal input activity.");

        var original = window._media.Keys.ToDictionary(
            key => key,
            key => window._media.Get(key),
            StringComparer.Ordinal);
        var events = new List<CardMicaPreviewState>();
        void Preview(CardMicaPreviewState state)
        {
            events.Add(state);
            cards.SetMicaPreview(state.Preview, state.Committed);
        }

        window.MicaPreviewChanged += Preview;
        try
        {
            window.NavigateTo("quickCards");
            await Task.Delay(80);

            var material = FindMicaControl<StackPanel>(window._page, "section-cardMica");
            var tint = FindMicaControl<Slider>(window._page, "setting-cardMicaTintOpacity");
            var transparency = FindMicaControl<Slider>(window._page, "setting-cardMicaTransparency");
            if (WalkTheme(material).OfType<Slider>().Count() != 2 ||
                WalkTheme(material).OfType<ComboBox>().Any() ||
                WalkTheme(material).OfType<Viewbox>().Any())
                throw new InvalidOperationException("The material section still contains preset or mock-preview UI.");
            if (Math.Abs(tint.Value - 34) > 0.01 || Math.Abs(transparency.Value - 50) > 0.01)
                throw new InvalidOperationException("Mica controls did not load the persisted draft values.");

            if (!window.CanPreviewCardMica)
            {
                if (tint.IsEnabled || transparency.IsEnabled)
                    throw new InvalidOperationException("Unavailable Mica controls remained enabled.");
                return new
                {
                    available = false,
                    disabled = true,
                    noPresets = true,
                    noMockPreview = true,
                    themeKinds = true,
                    settingsFocusOverride = false,
                    fixtureRoundTrip = true,
                    limitsClamped = true,
                    defaultsFallback = true
                };
            }

            if (!window.UsesVividSettingsBackdrop ||
                !window.SettingsMicaTuning.Matches(CardMicaTuning.FromValues(0.34, 0.50)))
                throw new InvalidOperationException("The settings window did not enter the saved Mica preview.");
            if (events.Count == 0 || events[^1].Preview is null)
                throw new InvalidOperationException("Entering the quick-card page did not raise a Mica preview.");

            var eventCount = events.Count;
            tint.Value = 8;
            transparency.Value = 70;
            await Task.Delay(80);
            if (events.Count - eventCount != 1)
                throw new InvalidOperationException($"Rapid slider edits emitted {events.Count - eventCount} Mica updates instead of one frame-coalesced update.");
            if (window.DraftCardMicaTuning.TintOpacity != 0.08 ||
                Math.Abs(window.DraftCardMicaTuning.LuminosityOpacity - 0.30) > 0.0001)
                throw new InvalidOperationException("The Mica sliders did not update the draft values.");
            AssertCardMica(cards, CardMicaTuning.FromUserValues(8, 70));
            if (events[^1].Preview is not { TintOpacity: 0.08 } preview ||
                Math.Abs(preview.LuminosityOpacity - 0.30) > 0.0001)
                throw new InvalidOperationException("The Mica sliders did not publish the latest preview.");
            AssertSavedMica(window, 0.34, 0.50);

            eventCount = events.Count;
            tint.Value = 31;
            transparency.Value = 55;
            await Task.Delay(80);
            if (events.Count - eventCount != 1)
                throw new InvalidOperationException("Consecutive Mica drags were not frame-coalesced.");
            AssertCardMica(cards, window.DraftCardMicaTuning);
            AssertSavedMica(window, 0.34, 0.50);

            // Re-read the saved values through the same draft path used by reload/discard.
            window._media.Pending.Clear();
            window._media.Readback(original);
            window.RenderPage();
            await Task.Delay(80);
            tint = FindMicaControl<Slider>(window._page, "setting-cardMicaTintOpacity");
            transparency = FindMicaControl<Slider>(window._page, "setting-cardMicaTransparency");
            if (Math.Abs(tint.Value - 34) > 0.01 || Math.Abs(transparency.Value - 50) > 0.01)
                throw new InvalidOperationException("Discarding the Mica draft did not restore the saved sliders.");
            AssertCardMica(cards, CardMicaTuning.FromValues(0.34, 0.50));

            // AppWindow.Hide leaves the page loaded. Reopening the same page must
            // re-arm preview even though NavigationView raises no selection change.
            window.DeactivateCardMicaPreview();
            window.ShowPage("quickCards");
            await Task.Delay(40);
            if (events[^1].Preview is null)
                throw new InvalidOperationException("Reopening the quick-card page did not restore its Mica preview.");
            AssertCardMica(cards, CardMicaTuning.FromValues(0.34, 0.50));
            var reopenedEventCount = events.Count;
            tint.Value = 27;
            await Task.Delay(80);
            if (events.Count - reopenedEventCount != 1)
                throw new InvalidOperationException("Mica preview did not resume after reopening the quick-card page.");
            AssertCardMica(cards, window.DraftCardMicaTuning);

            window.NavigateTo("taskbar");
            await Task.Delay(40);
            if (!window.SettingsMicaTuning.Matches(CardMicaTuning.FromValues(0.34, 0.50)))
                throw new InvalidOperationException("Leaving the quick-card page did not restore the saved settings backdrop.");
            AssertCardMica(cards, CardMicaTuning.FromValues(0.34, 0.50));
            if (events[^1].Preview is not null)
                throw new InvalidOperationException("Leaving the quick-card page did not end the card Mica preview.");

            return new
            {
                available = true,
                fixtureRoundTrip = true,
                noPresets = true,
                noMockPreview = true,
                themeKinds = true,
                settingsFocusOverride = false,
                frameCoalesced = true,
                discardRestored = true,
                previewRestoredOnLeave = true,
                reopenRearmsPreview = true,
                limitsClamped = true,
                defaultsFallback = true
            };
        }
        finally
        {
            window.MicaPreviewChanged -= Preview;
            window._media.Pending.Clear();
            window._media.Readback(original);
            window.NavigateTo("taskbar");
        }
    }

    private static void AssertCardMica(CardGroup cards, CardMicaTuning expected)
    {
        foreach (var card in cards.CardWindows)
        {
            if (!card.MicaTuning.Matches(expected))
                throw new InvalidOperationException($"Card Mica tuning did not update: {card.Window.Title}.");
        }
    }

    private static void AssertSavedMica(SettingsWindow window, double tint, double luminosity)
    {
        if (Math.Abs(window._media.SavedNumber("cardMicaTintOpacity", -1) - tint) > 0.0001 ||
            Math.Abs(window._media.SavedNumber("cardMicaLuminosityOpacity", -1) - luminosity) > 0.0001)
            throw new InvalidOperationException("A Mica preview changed the saved settings before Apply.");
    }

    private static T FindMicaControl<T>(DependencyObject root, string automationId) where T : FrameworkElement =>
        WalkTheme(root).OfType<T>().Single(element =>
            string.Equals(AutomationProperties.GetAutomationId(element), automationId, StringComparison.Ordinal));
}
