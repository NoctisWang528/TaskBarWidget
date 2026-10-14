using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskBarWidgets.UI;
using Windows.Foundation;

namespace TaskBarWidgets.UI.Validation;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(UiConnection).Module.ModuleHandle);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new ValidationApp();
        });
    }
}

public sealed partial class ValidationApp : Application
{
    private SettingsWindow? _settings;
    private CardGroup? _cards;
    private readonly List<object> _checks = new();

    public ValidationApp()
    {
        UnhandledException += (_, args) =>
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-validation-error.log"), args.Exception.ToString());
            args.Handled = true;
            Environment.ExitCode = 1;
            Exit();
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, Validate);
    }

    private void Validate()
    {
        using var connection = new UiConnection("TaskBarWidgets.UI.validation.disconnected");
        var success = false;
        string? error = null;
        try
        {
            _settings = new SettingsWindow(connection);
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "settings-fixture.json")))!.AsObject();
            _settings.LoadValidationData(fixture["native"]!.AsObject(), fixture["media"]!.AsObject());
            _cards = new CardGroup(connection, () => throw new InvalidOperationException("Validation must not open settings from a card."));
            foreach (var width in new[] { 720d, 1280d })
            {
                foreach (var page in new[] { "taskbar", "hardware", "sources", "lyrics", "displayArea", "audio", "general", "shortcuts", "trackOffsets", "lyricDiagnostics", "advanced", "about" })
                {
                    _settings.ShowPage(page);
                    var content = (FrameworkElement)_settings.Content;
                    content.Measure(new Size(width, 900));
                    content.Arrange(new Rect(0, 0, width, 900));
                    content.UpdateLayout();
                    // Hidden WinUI windows may not create a visual root: zero is unavailable, never a layout pass.
                    var layoutAvailable = content.DesiredSize.Width > 0;
                    if (layoutAvailable) Assert(double.IsFinite(content.DesiredSize.Width) && content.DesiredSize.Width <= width + 1, "Settings content exceeds the measured width.");
                    Assert(!IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(_settings)), "Settings became visible.");
                    _checks.Add(new { kind = "settingsControlConstruction", page, requestedWidth = width, layoutAvailable, desiredWidth = content.DesiredSize.Width });
                    if (page == "taskbar") _settings.OpenModuleProperties("native_2");
                    Assert(!IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(_settings)), "Opening module properties made settings visible.");
                }
            }
            foreach (var point in new[] { (100d, 100d), (1200d, 900d), (100d, 100d) })
            {
                _cards.Position(point.Item1, point.Item2);
                var windows = _cards.CardWindows;
                Assert(windows.Count == 3, "Expected three card windows.");
                for (var index = 0; index < windows.Count; index++)
                {
                    var card = windows[index];
                    var size = card.Window.AppWindow.Size;
                    var position = card.Window.AppWindow.Position;
                    Assert(size.Width > 0 && size.Height > 0, "Card dimensions must be positive.");
                    Assert(size.Width == windows[0].Window.AppWindow.Size.Width, "Card widths differ.");
                    Assert(size.Width == (int)Math.Ceiling(CardGroup.CardWidth * GetDpiForWindow(card.Handle) / 96d), "Card physical width differs from its DPI-scaled design width.");
                    if (index > 0)
                    {
                        var previous = windows[index - 1].Window.AppWindow;
                        Assert(position.X == previous.Position.X && position.Y > previous.Position.Y + previous.Size.Height, "Card alignment or gap is invalid.");
                    }
                    Assert(!IsWindowVisible(card.Handle), "A card became visible.");
                    card.Root.Measure(new Size(CardGroup.CardWidth, card.Height));
                    card.Root.Arrange(new Rect(0, 0, CardGroup.CardWidth, card.Height));
                    card.Root.UpdateLayout();
                    _checks.Add(new { kind = "card", index, size.Width, size.Height, position.X, position.Y });
                }
            }
            var settingsHandle = WinRT.Interop.WindowNative.GetWindowHandle(_settings);
            SendMessage(settingsHandle, 0x0010, 0, 0); // Exercise the user's native close path without showing a window.
            Assert(IsWindow(settingsHandle) && !IsWindowVisible(settingsHandle), "Settings close must retain its hidden native window.");
            _settings.ShowPage("taskbar");
            _checks.Add(new { kind = "settingsHiddenReuse", retainedHandle = IsWindow(settingsHandle) });
            success = true;
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            _cards?.Dispose();
            _settings?.Shutdown();
            var arguments = Environment.GetCommandLineArgs();
            var reportIndex = Array.IndexOf(arguments, "--result");
            var report = reportIndex >= 0 && reportIndex + 1 < arguments.Length ? arguments[reportIndex + 1] : "ui-validation.json";
            File.WriteAllText(report, JsonSerializer.Serialize(new { success, error, checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1;
            Exit();
        }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
}
