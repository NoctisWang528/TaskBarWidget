using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.UI.Settings;
using TaskBarWidgets.UI.Cards;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    // Use the caller's already-offscreen window. No system-theme change, saved
    // settings, backend requests, activation or additional HWND is required.
    internal static async Task<object> ProbeThemeControlsAsync(SettingsWindow window)
    {
        var top = window._navigation.MenuItems.OfType<NavigationViewItem>().ToArray();
        var lyricsGroup = top.Single(item => Equals(item.Tag, "lyricsGroup"));
        var quickCards = top.Single(item => Equals(item.Tag, "quickCards"));
        if (top.Length != 7 || top.Any(item => Equals(item.Tag, "audio") || Equals(item.Tag, "shortcuts")) ||
            lyricsGroup.MenuItems.Count != 5 || quickCards.MenuItems.Count != 3)
            throw new InvalidOperationException("Simplified settings navigation did not group lyrics or remove obsolete pages.");
        foreach (var child in lyricsGroup.MenuItems.OfType<NavigationViewItem>())
        {
            window.NavigateTo((string)child.Tag);
            if (window._currentPage != (string)child.Tag || !lyricsGroup.IsExpanded)
                throw new InvalidOperationException("Nested lyrics navigation failed.");
        }
        foreach (var child in quickCards.MenuItems.OfType<NavigationViewItem>())
        {
            window.NavigateTo((string)child.Tag);
            if (window._currentPage != (string)child.Tag || !quickCards.IsExpanded)
                throw new InvalidOperationException("Nested quick-card navigation failed.");
        }
        window.NavigateTo("general");
        var theme = window._media.Get("toolWindowTheme");
        window.ApplyTheme();
        var selector = WalkTheme(window._page).OfType<ComboBox>().Single(box => box.Items.OfType<ComboBoxItem>().Any(item => (item.Tag as JsonNode)?.ToString() == "Dark"));
        var checks = new List<object>();
        var section = window.Section("主题切换验证");
        var sectionCard = (Border)window._page.Children.Last();
        var colorEditor = window.FieldEditor(new JsonObject { ["key"] = "theme-probe.color", ["label"] = "未完成颜色", ["type"] = "color" }, window._native);
        section.Children.Add(colorEditor);
        var input = WalkTheme(colorEditor).OfType<TextBox>().Single();
        input.Text = "#12"; // An intentionally incomplete value must survive.
        var pane = UiThemeStyles.Apply(new Border { Width = 24, Height = 24, BorderThickness = new Thickness(1) }, "SettingsSourceSurface");
        window._properties.Children.Add(pane);
        window._split.IsPaneOpen = true;
        var lightColor = Microsoft.UI.Colors.Transparent;
        try
        {
            foreach (var requested in new[] { "Light", "Dark", "Light", "System" })
            {
                selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => (item.Tag as JsonNode)?.ToString() == requested);
                await Task.Delay(80);
                window._root.UpdateLayout();
                if (input.Text != "#12" || !section.Children.Contains(colorEditor) || !window._split.IsPaneOpen)
                    throw new InvalidOperationException("Theme change rebuilt an editor or dismissed the property drawer.");
                var expected = requested == "Light" ? ElementTheme.Light : requested == "Dark" ? ElementTheme.Dark : ElementTheme.Default;
                if (window._root.RequestedTheme != expected || window._dragOverlay.ActualTheme != window._root.ActualTheme || pane.ActualTheme != window._root.ActualTheme)
                    throw new InvalidOperationException("Theme did not reach the settings root, property drawer and drag overlay.");
                var surface = ((SolidColorBrush)sectionCard.Background).Color;
                var drawer = ((SolidColorBrush)pane.Background).Color;
                if (requested == "Light") lightColor = drawer;
                else if (requested == "Dark" && drawer == lightColor && !window._accessibility.HighContrast)
                    throw new InvalidOperationException("Existing drawer ThemeResource did not refresh for the new theme.");
                var navigation = WalkTheme(window._navigation).OfType<Grid>()
                    .Single(grid => grid.Name == "ContentGrid" && WalkTheme(grid).Contains(window._split));
                var navigationColor = ((SolidColorBrush)navigation.Background).Color;
                if (window._accessibility.HighContrast
                    ? surface.A != 255 || navigationColor.A != 255
                    : surface.A != 0x0A || navigationColor.A != 0)
                    throw new InvalidOperationException("Settings Mica is covered by a duplicate content layer, or high-contrast fallback is transparent.");
                checks.Add(new { requested, actual = window._root.ActualTheme.ToString(), surface = surface.ToString(), drawer = drawer.ToString(), unfinishedText = input.Text, retainedDrawer = true });
            }
        }
        finally
        {
            window._media.SetNode("toolWindowTheme", theme);
            window.ApplyTheme();
                window._page.Children.Remove(sectionCard);
            window._properties.Children.Remove(pane);
            window._split.IsPaneOpen = false;
        }
        return new { checks, systemThemeModified = false, previewUserColorsUntouched = true };
    }

    internal static async Task<object> ProbeCardMaterialControlsAsync(SettingsWindow window)
    {
        var originalMaterial = window._media.Get("cardMaterial")?.DeepClone();
        var originalBlur = window._media.Get("cardGlassBlur")?.DeepClone();
        var originalPage = window._currentPage;
        var originalHighContrast = window._probeCardHighContrast;
        var originalTheme = window._media.Get("toolWindowTheme")?.DeepClone();
        ElementTheme? lastTheme = null;
        void OnTheme(ElementTheme theme) => lastTheme = theme;
        window.ThemePreviewChanged += OnTheme;
        CardMicaPreviewState? lastPreview = null;
        void OnPreview(CardMicaPreviewState state) => lastPreview = state;
        window.MicaPreviewChanged += OnPreview;
        T? Find<T>(string id) where T : FrameworkElement => WalkTheme(window._page).OfType<T>()
            .SingleOrDefault(element => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == id);
        try
        {
            window.NavigateTo("quickCards");
            foreach (var id in new[] { "LiquidGlass", "Solid", "Mica" })
            {
                var picker = Find<ComboBox>("setting-cardMaterial") ?? throw new InvalidOperationException("Material picker missing.");
                picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, id));
                window._root.UpdateLayout();
                var glass = Find<Slider>("setting-cardGlassBlur");
                var mica = Find<Slider>("setting-cardMicaTintOpacity");
                if ((glass is not null) != (id == "LiquidGlass") || (mica is not null) != (id == "Mica"))
                    throw new InvalidOperationException("Material-specific controls were not disclosed correctly.");
                if (glass is not null)
                {
                    glass.Value = 72;
                    await DrainDispatcherAsync();
                    if (window.DraftCardGlassBlur != 0.72 ||
                        lastPreview?.Preview is not { Material: "LiquidGlass", GlassBlur: 0.72 })
                        throw new InvalidOperationException("Glass slider did not reach native preview state.");
                }
                if (lastPreview?.Preview?.Material != id)
                    throw new InvalidOperationException("Material selection did not reach native preview state.");
            }
            // Exercise the same dispatched accessibility callback without changing
            // the user's OS setting. Native owns the actual visual fallback.
            window._probeCardHighContrast = true;
            window.OnSettingsHighContrastChanged(window._accessibility, new object());
            await DrainDispatcherAsync();
            window._root.UpdateLayout();
            foreach (var key in new[] { "cardMicaTintOpacity", "cardMicaLuminosityOpacity", "cardMicaDarkTintOpacity", "cardMicaDarkLuminosityOpacity" })
                if (Find<Slider>("setting-" + key) is not { IsEnabled: false })
                    throw new InvalidOperationException("High contrast did not disable Mica tuning.");
            foreach (var id in new[] { "Solid", "LiquidGlass" })
            {
                var picker = Find<ComboBox>("setting-cardMaterial")!;
                picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, id));
                window._root.UpdateLayout();
                if (lastPreview?.Preview?.Material != id)
                    throw new InvalidOperationException("High contrast suppressed material draft preview.");
            }
            var blurInContrast = Find<Slider>("setting-cardGlassBlur")!;
            if (!blurInContrast.IsEnabled) throw new InvalidOperationException("High contrast disabled glass draft editing.");
            blurInContrast.Value = 63;
            await DrainDispatcherAsync();
            if (lastPreview?.Preview is not { Material: "LiquidGlass", GlassBlur: 0.63 })
                throw new InvalidOperationException("High contrast suppressed glass adjustment preview.");

            window._media.Set("toolWindowTheme", "Dark");
            window.ApplyTheme();
            window.OnConnectionChanged(false);
            await DrainDispatcherAsync();
            // Simulate Media discarding previews on connection close.
            lastPreview = null;
            lastTheme = null;
            window.OnConnectionChanged(true);
            await DrainDispatcherAsync();
            if (lastPreview?.Preview is not { Material: "LiquidGlass", GlassBlur: 0.63 } || lastTheme != ElementTheme.Dark ||
                window.DraftCardMaterial != "LiquidGlass" || window.DraftCardGlassBlur != 0.63)
                throw new InvalidOperationException("Reconnect did not restore current material and theme drafts.");

            window.NavigateTo("general");
            if (lastPreview?.Preview is not null)
                throw new InvalidOperationException("Leaving material settings did not reset native preview.");
            lastPreview = null;
            window.OnConnectionChanged(true);
            await DrainDispatcherAsync();
            if (lastPreview?.Preview is not null || window.ActiveMicaPreview is not null)
                throw new InvalidOperationException("Reconnect outside card settings activated material preview.");
            window.NavigateTo("quickCards");
            if (window.DraftCardGlassBlur != 0.63)
                throw new InvalidOperationException("Switching materials lost the glass draft.");
        }
        finally
        {
            window._probeCardHighContrast = originalHighContrast;
            window._media.SetNode("cardMaterial", originalMaterial);
            window._media.SetNode("cardGlassBlur", originalBlur);
            window._media.SetNode("toolWindowTheme", originalTheme);
            window.ApplyTheme();
            window.NavigateTo(originalPage);
            window.MicaPreviewChanged -= OnPreview;
            window.ThemePreviewChanged -= OnTheme;
        }

        return new { materialSpecificControls = true, glassAdjustmentPreview = true,
            highContrastDraftPreview = true, highContrastMicaDisabled = true,
            reconnectRestoresDraftAndTheme = true, leaveResetsPreview = true,
            reconnectOutsidePageDoesNotActivate = true, draftPreserved = true,
            systemHighContrastModified = false };

        Task DrainDispatcherAsync()
        {
            var completion = new TaskCompletionSource<bool>();
            if (!window.DispatcherQueue.TryEnqueue(() => completion.SetResult(true)))
                throw new InvalidOperationException("Probe dispatcher is unavailable.");
            return completion.Task;
        }
    }

    private static IEnumerable<DependencyObject> WalkTheme(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in WalkTheme(child)) yield return descendant;
        }
    }
}
