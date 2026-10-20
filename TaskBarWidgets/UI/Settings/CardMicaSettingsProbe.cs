using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
            JsonSerializer.SerializeToElement(new { tintOpacity = 0.08 }));
        var expectedPresets = new[]
        {
            ("柔和", CardMicaTuning.FromValues(0.12, 0.72)),
            ("标准", CardMicaTuning.Default),
            ("鲜艳", CardMicaTuning.FromValues(0.34, 0.50)),
            ("极透", CardMicaTuning.FromValues(0.08, 0.30))
        };
        if (!clampedLow.Matches(CardMicaTuning.FromValues(
                CardMicaTuning.MinimumTintOpacity,
                CardMicaTuning.MaximumLuminosityOpacity)) ||
            !clampedHigh.Matches(CardMicaTuning.FromValues(
                CardMicaTuning.MaximumTintOpacity,
                CardMicaTuning.MinimumLuminosityOpacity)) ||
            Math.Abs(darkEdge.TintOpacityFor(true) - CardMicaTuning.MinimumTintOpacity) > 0.0001 ||
            Math.Abs(darkEdge.LuminosityOpacityFor(true) - CardMicaTuning.MinimumLuminosityOpacity) > 0.0001 ||
            Math.Abs(missingLuminosity.TintOpacity - 0.08) > 0.0001 ||
            Math.Abs(missingLuminosity.LuminosityOpacity - CardMicaTuning.DefaultLuminosityOpacity) > 0.0001 ||
            Math.Abs(CardMicaTuning.Default.TintOpacityFor(true) - 0.20) > 0.0001 ||
            Math.Abs(CardMicaTuning.Default.LuminosityOpacityFor(true) - 0.56) > 0.0001 ||
            CardMicaPresets.Length != expectedPresets.Length ||
            expectedPresets.Where((expected, index) =>
                CardMicaPresets[index].Label != expected.Item1 ||
                !CardMicaPresets[index].Tuning.Matches(expected.Item2)).Any())
            throw new InvalidOperationException("Card Mica limits or JSON fallbacks were not normalized.");

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

            var preset = FindMicaControl<ComboBox>(window._page, "setting-cardMicaPreset");
            var tint = FindMicaControl<Slider>(window._page, "setting-cardMicaTintOpacity");
            var transparency = FindMicaControl<Slider>(window._page, "setting-cardMicaTransparency");
            if (Math.Abs(tint.Value - 34) > 0.01 || Math.Abs(transparency.Value - 50) > 0.01)
                throw new InvalidOperationException("Mica controls did not load the persisted draft values.");
            if (preset.Items.Count != expectedPresets.Length)
                throw new InvalidOperationException("Mica controls did not expose all four presets.");
            if (preset.SelectedItem is not ComboBoxItem { Tag: "鲜艳" })
                throw new InvalidOperationException("The Mica preset did not match the persisted values.");

            if (!window.CanPreviewCardMica)
            {
                if (tint.IsEnabled || transparency.IsEnabled || preset.IsEnabled)
                    throw new InvalidOperationException("Unavailable Mica controls remained enabled.");
                return new
                {
                    available = false,
                    disabled = true,
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

            preset.SelectedItem = preset.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "极透"));
            await Task.Delay(80);
            if (Math.Abs(tint.Value - 8) > 0.01 || Math.Abs(transparency.Value - 70) > 0.01 ||
                window.DraftCardMicaTuning.TintOpacity != 0.08 ||
                Math.Abs(window.DraftCardMicaTuning.LuminosityOpacity - 0.30) > 0.0001)
                throw new InvalidOperationException("The Mica preset did not update both the controls and draft.");
            AssertCardMica(cards, CardMicaTuning.FromUserValues(8, 70));
            if (events[^1].Preview is not { TintOpacity: 0.08 } preview ||
                Math.Abs(preview.LuminosityOpacity - 0.30) > 0.0001)
                throw new InvalidOperationException("The Mica preset did not publish the latest preview.");
            AssertSavedMica(window, 0.34, 0.50);

            var eventCount = events.Count;
            tint.Value = 31;
            transparency.Value = 55;
            await Task.Delay(80);
            if (events.Count - eventCount != 1)
                throw new InvalidOperationException($"Rapid slider edits emitted {events.Count - eventCount} Mica updates instead of one frame-coalesced update.");
            if (preset.SelectedItem is not null || preset.PlaceholderText != "自定义")
                throw new InvalidOperationException("Manual Mica values did not switch the preset selector to custom.");
            AssertCardMica(cards, window.DraftCardMicaTuning);
            AssertSavedMica(window, 0.34, 0.50);

            // A slider edit that happens to land on preset values still remains
            // a manual "custom" selection until the preset is chosen explicitly.
            tint.Value = 34;
            transparency.Value = 50;
            await Task.Delay(80);
            if (preset.SelectedItem is not null || preset.PlaceholderText != "自定义")
                throw new InvalidOperationException("Matching preset values did not retain the custom selection state.");

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
            if (!window.SettingsMicaTuning.Matches(CardMicaTuning.Default))
                throw new InvalidOperationException("Leaving the quick-card page did not restore the settings backdrop.");
            AssertCardMica(cards, CardMicaTuning.FromValues(0.34, 0.50));
            if (events[^1].Preview is not null)
                throw new InvalidOperationException("Leaving the quick-card page did not end the card Mica preview.");

            return new
            {
                available = true,
                fixtureRoundTrip = true,
                presets = 4,
                frameCoalesced = true,
                customState = true,
                discardRestored = true,
                previewResetOnLeave = true,
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
