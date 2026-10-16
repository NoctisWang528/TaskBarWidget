using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TaskBarWidgets.UI;

public sealed partial class SettingsWindow
{
    // Each column remains a complete labeled editor; narrow drawers always stack it.
    private static FrameworkElement ResponsiveFields(IEnumerable<FrameworkElement> fields)
    {
        var items = fields.ToList();
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "settings-responsive-fields" };
        foreach (var item in items) grid.Children.Add(item);
        var columns = 0;
        void Arrange(double width)
        {
            var next = width >= 820 ? 2 : 1;
            if (columns == next) return;
            columns = next; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            for (var index = 0; index < columns; index++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < (items.Count + columns - 1) / columns; index++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var index = 0; index < items.Count; index++) { Grid.SetRow(items[index], index / columns); Grid.SetColumn(items[index], index % columns); }
        }
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        Arrange(0);
        return grid;
    }

    private static void ArrangeField(Grid row, FrameworkElement labels, FrameworkElement control)
    {
        row.Children.Add(labels); row.Children.Add(control);
        control.VerticalAlignment = VerticalAlignment.Center;
        var stacked = false;
        var initialized = false;
        void Arrange(double width)
        {
            var next = width < 480;
            if (initialized && next == stacked) return;
            initialized = true; stacked = next; row.ColumnDefinitions.Clear(); row.RowDefinitions.Clear();
            if (stacked) {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(labels, 0); Grid.SetRow(labels, 0); Grid.SetColumn(control, 0); Grid.SetRow(control, 1);
                control.MaxWidth = double.PositiveInfinity; control.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            else {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 144, MaxWidth = 220 });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(labels, 0); Grid.SetRow(labels, 0); Grid.SetColumn(control, 1); Grid.SetRow(control, 0);
                control.MaxWidth = 220; control.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }
        row.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        Arrange(0);
    }
}
