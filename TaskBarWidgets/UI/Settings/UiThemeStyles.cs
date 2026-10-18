using Microsoft.UI.Xaml;

namespace TaskBarWidgets.UI.Settings;

internal static class UiThemeStyles
{
    private static readonly ResourceDictionary Styles = new() { Source = new Uri("ms-appx:///Settings/SettingsThemeResources.xaml") };

    internal static T Apply<T>(T element, string key) where T : FrameworkElement
    {
        element.Style = (Style)Styles[key];
        return element;
    }
}
