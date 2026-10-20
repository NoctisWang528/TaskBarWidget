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
        var defaults = CardMicaTuning.Default;
        var clampedLow = CardMicaTuning.FromUserValues(-10, -10, -10, -10);
        var clampedHigh = CardMicaTuning.FromUserValues(110, 110, 110, 110);
        var lowerEndpoints = CardMicaTuning.FromUserValues(0, 100, 0, 100);
        var upperEndpoints = CardMicaTuning.FromUserValues(100, 0, 100, 0);
        var nonFinite = CardMicaTuning.FromThemeValues(
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            double.NaN);
        var legacyPayload = CardMicaTuning.FromJson(
            JsonSerializer.SerializeToElement(new { tintOpacity = 0.08, luminosityOpacity = 0.30 }));
        var mixedPayload = CardMicaTuning.FromJson(
            JsonSerializer.SerializeToElement(new { tintOpacity = 0.08, darkTintOpacity = 0.22 }));
        var explicitDarkPayload = CardMicaTuning.FromJson(
            JsonSerializer.SerializeToElement(new
            {
                tintOpacity = 0.08,
                luminosityOpacity = 0.30,
                darkTintOpacity = 0.22,
                darkLuminosityOpacity = 0.44
            }));
        if (!clampedLow.Matches(CardMicaTuning.FromThemeValues(0, 1, 0, 1)) ||
            !clampedHigh.Matches(CardMicaTuning.FromThemeValues(1, 0, 1, 0)) ||
            !lowerEndpoints.Matches(CardMicaTuning.FromThemeValues(0, 0, 0, 0)) ||
            !upperEndpoints.Matches(CardMicaTuning.FromThemeValues(1, 1, 1, 1)) ||
            !nonFinite.Matches(defaults) ||
            !defaults.Matches(CardMicaTuning.FromThemeValues(0.10, 0.85, 0.15, 0.90)) ||
            !CardMicaTuning.SystemProxy.Matches(CardMicaTuning.FromThemeValues(0.00, 0.85, 0.15, 0.96)) ||
            !legacyPayload.Matches(CardMicaTuning.FromThemeValues(0.08, 0.30, 0.06, 0.28)) ||
            !mixedPayload.Matches(CardMicaTuning.FromThemeValues(0.08, 0.85, 0.22, 0.90)) ||
            !explicitDarkPayload.Matches(CardMicaTuning.FromThemeValues(0.08, 0.30, 0.22, 0.44)) ||
            VividMicaBackdrop.KindFor(false) != MicaKind.Base ||
            VividMicaBackdrop.KindFor(true) != MicaKind.BaseAlt)
            throw new InvalidOperationException("Card Mica limits, legacy compatibility, defaults or theme kinds were not normalized.");
        if (window._cardMicaPreviewBackdrop.ForcesInputActive)
            throw new InvalidOperationException("The settings-window Mica preview must retain normal input activity.");

        var fixture = CardMicaTuning.FromThemeValues(0.34, 0.50, 0.22, 0.40);
        var original = window._media.Keys.ToDictionary(
            key => key,
            key => window._media.Get(key),
            StringComparer.Ordinal);
        var events = new List<CardMicaPreviewState>();
        void Preview(CardMicaPreviewState state)
        {
            events.Add(state);
            cards.SetMicaVivid(state.Vivid);
            cards.SetMicaPreview(state.Preview, state.Committed);
        }

        window.MicaPreviewChanged += Preview;
        try
        {
            window.NavigateTo("quickCards");
            await Task.Delay(80);

            var material = FindMicaControl<StackPanel>(window._page, "section-cardMica");
            var sliders = WalkTheme(material).OfType<Slider>().ToArray();
            if (sliders.Length != 4 ||
                WalkTheme(material).OfType<ComboBox>().Any() ||
                WalkTheme(material).OfType<Viewbox>().Any())
                throw new InvalidOperationException("The material section does not expose exactly the four light/dark sliders.");
            if (sliders.Any(slider => slider.Minimum != 0 || slider.Maximum != 100))
                throw new InvalidOperationException("Card Mica sliders do not expose the full 0-100% range.");

            var lightTint = FindMicaControl<Slider>(window._page, "setting-cardMicaTintOpacity");
            var lightTransparency = FindMicaControl<Slider>(window._page, "setting-cardMicaTransparency");
            var darkTint = FindMicaControl<Slider>(window._page, "setting-cardMicaDarkTintOpacity");
            var darkTransparency = FindMicaControl<Slider>(window._page, "setting-cardMicaDarkTransparency");
            var vivid = FindMicaControl<ToggleSwitch>(window._page, "setting-cardMicaVivid");
            if (Math.Abs(lightTint.Value - 34) > 0.01 ||
                Math.Abs(lightTransparency.Value - 50) > 0.01 ||
                Math.Abs(darkTint.Value - 22) > 0.01 ||
                Math.Abs(darkTransparency.Value - 60) > 0.01)
                throw new InvalidOperationException("Mica controls did not load the persisted four-value draft.");
            if (!vivid.IsOn || !cards.MicaVivid || cards.CardWindows.Any(card => !card.UsesVividMica))
                throw new InvalidOperationException("The vivid Mica switch did not load the persisted enabled state.");

            if (!window.CanPreviewCardMica)
            {
                if (sliders.Any(slider => slider.IsEnabled))
                    throw new InvalidOperationException("Unavailable Mica controls remained enabled.");
                return new
                {
                    available = false,
                    disabled = true,
                    fourSliders = true,
                    noPresets = true,
                    noMockPreview = true,
                    vividSwitch = true,
                    themeKinds = true,
                    settingsFocusOverride = false,
                    fixtureRoundTrip = true,
                    limitsClamped = true,
                    defaultsFallback = true,
                    legacyCompatible = true
                };
            }

            if (!window.UsesVividSettingsBackdrop || !window.SettingsMicaTuning.Matches(fixture))
                throw new InvalidOperationException("The settings window did not enter the saved four-value Mica preview.");
            if (sliders.Any(slider => !slider.IsEnabled))
                throw new InvalidOperationException("Mica sliders were disabled while vivid Mica was enabled.");
            if (events.Count == 0 || events[^1].Preview is null)
                throw new InvalidOperationException("Entering the quick-card page did not raise a Mica preview.");

            SetMicaSliders(lightTint, lightTransparency, darkTint, darkTransparency, 0, 100, 0, 100);
            await Task.Delay(80);
            if (!window.DraftCardMicaTuning.Matches(CardMicaTuning.FromUserValues(0, 100, 0, 100)))
                throw new InvalidOperationException("The Mica lower endpoints were not accepted.");
            SetMicaSliders(lightTint, lightTransparency, darkTint, darkTransparency, 100, 0, 100, 0);
            await Task.Delay(80);
            if (!window.DraftCardMicaTuning.Matches(CardMicaTuning.FromUserValues(100, 0, 100, 0)))
                throw new InvalidOperationException("The Mica upper endpoints were not accepted.");

            SetMicaSliders(lightTint, lightTransparency, darkTint, darkTransparency, 34, 50, 22, 60);
            await Task.Delay(80);
            var eventCount = events.Count;
            lightTint.Value = 8;
            lightTransparency.Value = 70;
            await Task.Delay(80);
            if (events.Count - eventCount != 1)
                throw new InvalidOperationException($"Rapid slider edits emitted {events.Count - eventCount} Mica updates instead of one frame-coalesced update.");
            if (!window.DraftCardMicaTuning.Matches(CardMicaTuning.FromThemeValues(0.08, 0.30, 0.22, 0.40)))
                throw new InvalidOperationException("The light Mica sliders did not leave the dark pair untouched.");
            AssertCardMica(cards, CardMicaTuning.FromThemeValues(0.08, 0.30, 0.22, 0.40));
            if (events[^1].Preview is not { LightTintOpacity: 0.08, DarkTintOpacity: 0.22 } preview ||
                Math.Abs(preview.LightLuminosityOpacity - 0.30) > 0.0001 ||
                Math.Abs(preview.DarkLuminosityOpacity - 0.40) > 0.0001)
                throw new InvalidOperationException("The Mica sliders did not publish the latest four-value preview.");
            AssertSavedMica(window, fixture);

            eventCount = events.Count;
            darkTint.Value = 31;
            darkTransparency.Value = 55;
            await Task.Delay(80);
            if (events.Count - eventCount != 1)
                throw new InvalidOperationException("Consecutive Mica drags were not frame-coalesced.");
            if (!window.DraftCardMicaTuning.Matches(CardMicaTuning.FromThemeValues(0.08, 0.30, 0.31, 0.45)))
                throw new InvalidOperationException("The dark Mica sliders changed the light pair.");
            AssertCardMica(cards, window.DraftCardMicaTuning);
            AssertSavedMica(window, fixture);

            // Re-read the saved values through the same draft path used by reload/discard.
            window._media.Pending.Clear();
            window._media.Readback(original);
            window.RenderPage();
            await Task.Delay(80);
            lightTint = FindMicaControl<Slider>(window._page, "setting-cardMicaTintOpacity");
            lightTransparency = FindMicaControl<Slider>(window._page, "setting-cardMicaTransparency");
            darkTint = FindMicaControl<Slider>(window._page, "setting-cardMicaDarkTintOpacity");
            darkTransparency = FindMicaControl<Slider>(window._page, "setting-cardMicaDarkTransparency");
            if (Math.Abs(lightTint.Value - 34) > 0.01 ||
                Math.Abs(lightTransparency.Value - 50) > 0.01 ||
                Math.Abs(darkTint.Value - 22) > 0.01 ||
                Math.Abs(darkTransparency.Value - 60) > 0.01)
                throw new InvalidOperationException("Discarding the Mica draft did not restore all four saved sliders.");
            AssertCardMica(cards, fixture);
            AssertSavedMica(window, fixture);

            // AppWindow.Hide leaves the page loaded. Reopening the same page must
            // re-arm preview even though NavigationView raises no selection change.
            window.DeactivateCardMicaPreview();
            window.ShowPage("quickCards");
            await Task.Delay(40);
            if (events[^1].Preview is null)
                throw new InvalidOperationException("Reopening the quick-card page did not restore its Mica preview.");
            AssertCardMica(cards, fixture);
            var reopenedEventCount = events.Count;
            lightTint.Value = 27;
            await Task.Delay(80);
            if (events.Count - reopenedEventCount != 1)
                throw new InvalidOperationException("Mica preview did not resume after reopening the quick-card page.");
            AssertCardMica(cards, window.DraftCardMicaTuning);

            // The switch selects between the tuned material and the plain system
            // one; the tuned values stay on the controller while it is off.
            FindMicaControl<ToggleSwitch>(window._page, "setting-cardMicaVivid").IsOn = false;
            await Task.Delay(120);
            if (window.DraftCardMicaVivid || events[^1].Vivid)
                throw new InvalidOperationException("Turning vivid Mica off did not reach the draft state.");
            if (cards.MicaVivid || cards.CardWindows.Any(card => card.UsesVividMica))
                throw new InvalidOperationException("Turning vivid Mica off did not switch the cards to the system material.");
            if (!window.SettingsMicaTuning.Matches(CardMicaTuning.SystemProxy))
                throw new InvalidOperationException("The settings preview did not follow the vivid switch.");
            var offSliders = WalkTheme(FindMicaControl<StackPanel>(window._page, "section-cardMica")).OfType<Slider>().ToArray();
            if (offSliders.Length != 4 || offSliders.Any(slider => slider.IsEnabled))
                throw new InvalidOperationException("Card Mica sliders stayed editable while vivid Mica was off.");
            AssertCardMica(cards, window.DraftCardMicaTuning);
            AssertSavedMica(window, fixture);
            if (window._media.GetSaved("cardMicaVivid") is System.Text.Json.Nodes.JsonValue savedVivid &&
                savedVivid.TryGetValue<bool>(out var savedFlag) && !savedFlag)
                throw new InvalidOperationException("The vivid switch changed the saved setting before Apply.");

            FindMicaControl<ToggleSwitch>(window._page, "setting-cardMicaVivid").IsOn = true;
            await Task.Delay(120);
            if (!window.DraftCardMicaVivid || !events[^1].Vivid)
                throw new InvalidOperationException("Turning vivid Mica back on did not reach the draft state.");
            if (!cards.MicaVivid || cards.CardWindows.Any(card => !card.UsesVividMica))
                throw new InvalidOperationException("Turning vivid Mica back on did not restore the tuned material.");
            var onSliders = WalkTheme(FindMicaControl<StackPanel>(window._page, "section-cardMica")).OfType<Slider>().ToArray();
            if (onSliders.Length != 4 || onSliders.Any(slider => !slider.IsEnabled))
                throw new InvalidOperationException("Card Mica sliders did not become editable again.");
            if (!window.SettingsMicaTuning.Matches(window.DraftCardMicaTuning))
                throw new InvalidOperationException("The settings preview did not return to the tuned material.");

            window.NavigateTo("taskbar");
            await Task.Delay(40);
            if (!window.SettingsMicaTuning.Matches(fixture))
                throw new InvalidOperationException("Leaving the quick-card page did not restore the saved settings backdrop.");
            AssertCardMica(cards, fixture);
            if (events[^1].Preview is not null)
                throw new InvalidOperationException("Leaving the quick-card page did not end the card Mica preview.");

            return new
            {
                available = true,
                fixtureRoundTrip = true,
                fourSliders = true,
                independentThemes = true,
                noPresets = true,
                noMockPreview = true,
                vividSwitch = true,
                vividRoundTrip = true,
                themeKinds = true,
                settingsFocusOverride = false,
                frameCoalesced = true,
                discardRestored = true,
                previewRestoredOnLeave = true,
                reopenRearmsPreview = true,
                limitsClamped = true,
                defaultsFallback = true,
                legacyCompatible = true
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

    private static void SetMicaSliders(
        Slider lightTint,
        Slider lightTransparency,
        Slider darkTint,
        Slider darkTransparency,
        double lightTintValue,
        double lightTransparencyValue,
        double darkTintValue,
        double darkTransparencyValue)
    {
        lightTint.Value = lightTintValue;
        lightTransparency.Value = lightTransparencyValue;
        darkTint.Value = darkTintValue;
        darkTransparency.Value = darkTransparencyValue;
    }

    private static void AssertCardMica(CardGroup cards, CardMicaTuning expected)
    {
        foreach (var card in cards.CardWindows)
        {
            if (!card.MicaTuning.Matches(expected))
                throw new InvalidOperationException($"Card Mica tuning did not update: {card.Window.Title}.");
        }
    }

    private static void AssertSavedMica(SettingsWindow window, CardMicaTuning expected)
    {
        if (Math.Abs(window._media.SavedNumber("cardMicaTintOpacity", -1) - expected.LightTintOpacity) > 0.0001 ||
            Math.Abs(window._media.SavedNumber("cardMicaLuminosityOpacity", -1) - expected.LightLuminosityOpacity) > 0.0001 ||
            Math.Abs(window._media.SavedNumber("cardMicaDarkTintOpacity", -1) - expected.DarkTintOpacity) > 0.0001 ||
            Math.Abs(window._media.SavedNumber("cardMicaDarkLuminosityOpacity", -1) - expected.DarkLuminosityOpacity) > 0.0001)
            throw new InvalidOperationException("A Mica preview changed the saved four-value settings before Apply.");
    }

    private static T FindMicaControl<T>(DependencyObject root, string automationId) where T : FrameworkElement =>
        WalkTheme(root).OfType<T>().Single(element =>
            string.Equals(AutomationProperties.GetAutomationId(element), automationId, StringComparison.Ordinal));
}
