using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskBarWidgets.UI.Settings;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    // Use the caller's already-offscreen window. No system-theme change, saved
    // settings, backend requests, activation or additional HWND is required.
    internal static async Task<object> ProbeThemeControlsAsync(SettingsWindow window, CardGroup cards)
    {
        var top = window._navigation.MenuItems.OfType<NavigationViewItem>().ToArray();
        var lyricsGroup = top.Single(item => Equals(item.Tag, "lyricsGroup"));
        var quickCards = top.Single(item => Equals(item.Tag, "quickCards"));
        if (top.Length != 7 || top.Any(item => Equals(item.Tag, "audio") || Equals(item.Tag, "shortcuts")) ||
            lyricsGroup.MenuItems.Count != 5 || quickCards.MenuItems.Count != 2)
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
        window.ThemePreviewChanged += cards.SetThemePreview;
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
                var cardCheck = await CardGroup.ProbeThemeAsync(cards, expected);
                checks.Add(new { cardCheck, requested, actual = window._root.ActualTheme.ToString(), surface = surface.ToString(), drawer = drawer.ToString(), unfinishedText = input.Text, retainedDrawer = true });
            }
        }
        finally
        {
            window._media.SetNode("toolWindowTheme", theme);
            window.ApplyTheme();
            window.ThemePreviewChanged -= cards.SetThemePreview;
            window._page.Children.Remove(sectionCard);
            window._properties.Children.Remove(pane);
            window._split.IsPaneOpen = false;
        }
        return new { checks, systemThemeModified = false, previewUserColorsUntouched = true };
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
