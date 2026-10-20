using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TaskBarWidgets.UI.Settings;

internal static class SettingsToggle
{
    internal static ToggleSwitch Compact(
        bool isOn,
        string name,
        string? automationId = null,
        string? description = null,
        Action<bool>? changed = null)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, name);
        if (!string.IsNullOrWhiteSpace(automationId)) AutomationProperties.SetAutomationId(toggle, automationId);
        if (!string.IsNullOrWhiteSpace(description)) ToolTipService.SetToolTip(toggle, description);
        if (changed is not null) toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    internal static FrameworkElement Row(
        string label,
        string? description,
        bool isOn,
        string name,
        string? automationId = null,
        Action<bool>? changed = null)
        => WithLabel(label, description, Compact(isOn, name, automationId, description, changed));

    internal static FrameworkElement WithLabel(string label, string? description, ToggleSwitch toggle)
    {
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = label, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(description)) labels.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Opacity = .72,
            TextWrapping = TextWrapping.Wrap
        });

        var row = new Grid
        {
            ColumnSpacing = 16,
            MinHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(labels);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }
}
