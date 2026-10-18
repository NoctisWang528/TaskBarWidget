using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    internal static async Task<object> ProbeSourceSelectorAsync(CardGroup cards)
    {
        const string icon = "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='20'%20height='20'%3E%3Crect%20width='20'%20height='20'%20fill='%230078d4'/%3E%3C/svg%3E";
        var state = JsonSerializer.SerializeToElement(new { title = "Source selector probe", artist = "", source = "Browser", selectedSourceId = "edge",
            sources = new[] { new { id = "qq", label = "QQ 音乐", iconDataUrl = icon }, new { id = "edge", label = "Microsoft Edge · 较长名称", iconDataUrl = icon } } });
        cards.RenderMedia(state);
        await cards.RenderSelectedSourceIconAsync(icon);
        cards._cards[2].Root.UpdateLayout();
        if (cards._sourceImage.Source is null || !cards._sourceLabel.Text.StartsWith("Microsoft Edge") || !cards._source.IsEnabled || cards._source.ActualHeight < 33)
            throw new InvalidOperationException("Source selector label, icon or click target failed.");
        var menu = new MenuFlyoutItem(); await cards.SetSourceMenuIconAsync(menu, icon);
        if (menu.Icon is not ImageIcon { Source: not null }) throw new InvalidOperationException("Source flyout icon failed to decode.");
        if (await cards.SourceImageAsync("data:image/png;base64,invalid") is not null) throw new InvalidOperationException("Invalid source icon was accepted.");
        return new { applicationIconDecoded = true, selectedName = cards._sourceLabel.Text, buttonHeight = cards._source.ActualHeight,
            menuIconDecoded = true, malformedIconUsesFallback = true, realSessionsTouched = false };
    }
}
