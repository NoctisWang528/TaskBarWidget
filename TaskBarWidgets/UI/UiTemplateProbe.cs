using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace TaskBarWidgets.UI;

// Exercise the shipped application's lazy template loader without a native/media
// host, activation, taskbar button, audio operation, or on-screen window.
internal static class UiTemplateProbe
{
    internal static async Task RunAsync(Application app, string[] args)
    {
        SettingsWindow? settings = null;
        CardGroup? cards = null;
        using var connection = new UiConnection("TaskBarWidgets.UI.template-probe.disconnected");
        var before = GetForegroundWindow();
        var checks = new List<object>();
        string? error = null;
        try
        {
            settings = new SettingsWindow(connection);
            settings.AppWindow.IsShownInSwitchers = false;
            ShowOffscreen(settings, 1180, 820);
            await Task.Delay(400);
            var content = (FrameworkElement)settings.Content;
            content.UpdateLayout();
            if (!content.IsLoaded || content.ActualWidth <= 0) throw new InvalidOperationException("Settings templates were not realized.");
            checks.Add(new { surface = "settings", width = content.ActualWidth, loaded = content.IsLoaded });
            cards = new CardGroup(connection, () => throw new InvalidOperationException("Probe must not invoke settings."));
            foreach (var card in cards.CardWindows)
            {
                ShowOffscreen(card.Window, 480, 400);
                await Task.Delay(200);
                card.Root.UpdateLayout();
                if (!card.Root.IsLoaded || card.Root.ActualWidth <= 0) throw new InvalidOperationException("Card templates were not realized.");
                checks.Add(new { surface = card.Window.Title, width = card.Root.ActualWidth, loaded = card.Root.IsLoaded });
            }
            if (GetForegroundWindow() != before) throw new InvalidOperationException("Probe unexpectedly changed the foreground window.");
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            cards?.Dispose(); settings?.Shutdown();
            var resultIndex = Array.IndexOf(args, "--result");
            var result = resultIndex >= 0 && resultIndex + 1 < args.Length ? args[resultIndex + 1] : Path.Combine(AppContext.BaseDirectory, "template-probe.json");
            File.WriteAllText(result, JsonSerializer.Serialize(new { success = error is null, error, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = error is null ? 0 : 1;
            app.Exit();
        }
    }
    private static void ShowOffscreen(Window window, int width, int height)
    {
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, width, height));
        window.AppWindow.Show(false); // Never calls Window.Activate or requests focus.
        var position = window.AppWindow.Position;
        if (position.X + width >= -20000 || position.Y + height >= -20000)
        {
            window.AppWindow.Hide();
            throw new InvalidOperationException("Probe window did not stay offscreen.");
        }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
