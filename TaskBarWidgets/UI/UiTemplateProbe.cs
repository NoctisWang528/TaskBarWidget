using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using TaskBarWidgets.UI.Cards;
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
        var activated = false;
        void TrackActivation(Window window, bool allowCodeActivation = false) => window.Activated += (_, eventArgs) => {
            if (eventArgs.WindowActivationState == WindowActivationState.PointerActivated ||
                eventArgs.WindowActivationState == WindowActivationState.CodeActivated && !allowCodeActivation)
                activated = true;
        };
        var checks = new List<object>();
        var cardLifecycle = new Dictionary<string, bool>();
        object? layoutChecks = null;
        object? audioControls = null;
        object? heatmapChecks = null;
        object? weatherControls = null;
        object? weatherCardControls = null;
        object? themeControls = null;
        object? micaControls = null;
        object? cardLyricsControls = null;
        object? sourceSelector = null;
        object? offsetSettings = null;
        object? reorderControls = null;
        object? updateCard = null, updateControls = null, updateOrdering = null;
        string? error = null;
        try
        {
            updateOrdering = ((App)app).ProbeUpdateStateOrdering();
            settings = new SettingsWindow(connection);
            // Opening the in-process overlay drawer raises CodeActivated even for
            // a WS_EX_NOACTIVATE probe HWND. Pointer activation and the final
            // foreground-process check still verify that the probe stays hidden.
            TrackActivation(settings, allowCodeActivation: true);
            settings.AppWindow.IsShownInSwitchers = false;
            ShowOffscreen(settings, 1180, 820);
            await Task.Delay(400);
            var content = (FrameworkElement)settings.Content;
            content.UpdateLayout();
            if (!content.IsLoaded || content.ActualWidth <= 0) throw new InvalidOperationException("Settings templates were not realized.");
            checks.Add(new { surface = "settings", width = content.ActualWidth, loaded = content.IsLoaded });
            var fixtureIndex = Array.IndexOf(args, "--fixture");
            if (fixtureIndex >= 0 && fixtureIndex + 1 < args.Length)
                layoutChecks = await UiLayoutProbe.RunAsync(settings, args[fixtureIndex + 1], Path.Combine(AppContext.BaseDirectory, "..", "ui-layout-check"));
            if (layoutChecks is not null)
            {
                reorderControls = await SettingsWindow.ProbeReorderControlsAsync(settings);
                offsetSettings = await OffsetSettingsProbe.RunAsync(settings);
            }
            cardLyricsControls = await CardLyricsProbe.RunAsync();
            cards = new CardGroup(connection, _ => throw new InvalidOperationException("Probe must not invoke settings."));
            cards.SetQuickCardEnabled("weather", true);
            cards.ApplyBackendTheme(JsonSerializer.SerializeToElement(new { toolWindowTheme = 2 }));
            updateCard = await CardGroup.ProbeUpdateCardStateAsync(cards);
            if (cards.CardWindows.Any(card => card.Root.RequestedTheme != ElementTheme.Dark)) throw new InvalidOperationException("Initial panel theme did not apply before Show.");
            cardLifecycle["initialThemeBeforeShow"] = true;
            foreach (var card in cards.CardWindows.Append(cards._updateCard))
                if (!card.MicaBackdrop.ForcesInputActive)
                    throw new InvalidOperationException("A card window did not force active Mica rendering.");
            cardLifecycle["micaAlwaysInputActive"] = true;
            foreach (var point in new[] { (100d, 100d), (1200d, 900d), (100d, 100d) })
            {
                cards.Position(point.Item1, point.Item2);
                var windows = cards.CardWindows;
                var baseWindows = windows.Where(card => !ReferenceEquals(card, cards._updateCard)).ToArray();
                var baseWidth = baseWindows.Length > 0 ? baseWindows[0].Window.AppWindow.Size.Width : 0;
                foreach (var card in windows)
                {
                    var bounds = card.Window.AppWindow;
                    var expectedWidth = ReferenceEquals(card, cards._updateCard) ? 220 : baseWidth;
                    if (IsWindowVisible(card.Handle) || bounds.Size.Width <= 0 || bounds.Size.Height <= 0 ||
                        bounds.Size.Width != expectedWidth)
                        throw new InvalidOperationException("Hidden card geometry is invalid.");
                }
                // Cards choose their column freely, so group the result by X.
                var baseColumns = baseWindows
                    .GroupBy(card => card.Window.AppWindow.Position.X)
                    .OrderBy(group => group.Key)
                    .ToArray();
                if (baseWindows.Length > 0 && baseColumns.Length > 2)
                    throw new InvalidOperationException("Quick cards overflow the two available columns.");
                foreach (var column in baseColumns)
                {
                    var ordered = column.OrderBy(card => card.Window.AppWindow.Position.Y).ToArray();
                    for (var index = 1; index < ordered.Length; index++)
                    {
                        var bounds = ordered[index].Window.AppWindow;
                        var previous = ordered[index - 1].Window.AppWindow;
                        if (bounds.Position.X != previous.Position.X || bounds.Position.Y <= previous.Position.Y + previous.Size.Height)
                            throw new InvalidOperationException("Cards in the same column overlap or are misaligned.");
                    }
                }
                // Every populated column bottom-aligns to the tallest column.
                if (baseColumns.Length >= 2)
                {
                    var bottoms = baseColumns
                        .Select(column => column.Max(card => card.Window.AppWindow.Position.Y + card.Window.AppWindow.Size.Height))
                        .ToArray();
                    if (Math.Abs(bottoms[0] - bottoms[1]) > 1)
                        throw new InvalidOperationException("Base columns are not bottom-aligned.");
                }
            }
            cardLifecycle["hiddenMeasuredPlacement"] = true;
            {
                var preview = CardMicaTuning.FromValues(0.41, 0.21);
                cards.SetMicaPreview(preview);
                cards.SetQuickCardEnabled("weather", false);
                cards.SetQuickCardEnabled("weather", true);
                if (cards.CardWindows.Any(card => !card.MicaTuning.Matches(preview)))
                    throw new InvalidOperationException("A card created during Mica preview did not inherit the preview tuning.");
                cards.SetMicaPreview(null);
                cardLifecycle["micaNewCardInherits"] = true;
            }
            // Prewarm must build exactly the cards the media settings payload
            // enables, not a hard-coded set.
            var settingsShaped = JsonSerializer.SerializeToElement(new
            {
                quickCardOrder = new[] { "traffic", "device", "media", "weather" },
                quickCardColumns = new Dictionary<string, int> { ["traffic"] = 0, ["device"] = 0, ["media"] = 0, ["weather"] = 1 },
                quickCardEnabled = new Dictionary<string, bool> { ["weather"] = false, ["traffic"] = true, ["device"] = true, ["media"] = true },
                quickCardResourceMode = "OnDemand",
                cardMicaTintOpacity = 0.08,
                cardMicaLuminosityOpacity = 0.30
            });
            cards.ApplyQuickCardsFromSettings(settingsShaped);
            if (cards.CardWindows.Any(card => card.Window.Title.Contains("天气", StringComparison.Ordinal)))
                throw new InvalidOperationException("Quick-card settings did not release the disabled card.");
            foreach (var card in cards.CardWindows)
                if (!card.MicaTuning.Matches(CardMicaTuning.FromValues(0.08, 0.30)))
                    throw new InvalidOperationException("Quick-card settings did not apply the persisted Mica tuning.");
            cardLifecycle["prewarmFollowsQuickCardSettings"] = true;
            cards.SetQuickCardEnabled("weather", true);
            // The shipped startup prewarm must render each card once with no
            // visible window and no layout drift, so the first click only shows
            // already-committed windows.
            var prewarmStart = GetCardBounds(cards);
            {
                await cards.PrewarmAsync();
                foreach (var card in cards.CardWindows)
                {
                    await card.VisibleFrame.WaitAsync(TimeSpan.FromMilliseconds(600));
                    if (!card.Prewarmed) throw new InvalidOperationException("Card prewarm did not commit a frame.");
                    if (IsWindowVisible(card.Handle)) throw new InvalidOperationException("Prewarm left a card visible.");
                }
                var prewarmEnd = GetCardBounds(cards);
                if (prewarmStart.Length != prewarmEnd.Length) throw new InvalidOperationException("Prewarm changed the card set.");
                for (var index = 0; index < prewarmStart.Length; index++)
                    if (prewarmStart[index] != prewarmEnd[index])
                        throw new InvalidOperationException("Prewarm resized or moved a hidden card.");
            }
            cardLifecycle["prewarmRendersHidden"] = true;
            foreach (var card in cards.CardWindows)
            {
                TrackActivation(card.Window);
                PositionOffscreen(card.Window, 480, 400);
                card.Show(); // Exercise the actual first-show path, without activation.
                AssertOffscreen(card.Window);
                // The card must stay invisible until the frame after Show commits,
                // so the first pixels on screen are the real surface, not a blank
                // window: the fade has to start on a composition frame.
                if (ElementCompositionPreview.GetElementVisual(card.Root).Opacity != 0)
                    throw new InvalidOperationException("Card became visible before its first frame.");
                await card.VisibleFrame.WaitAsync(TimeSpan.FromMilliseconds(600));
                await Task.Delay(200);
                card.Root.UpdateLayout();
                if (!card.Root.IsLoaded || card.Root.ActualWidth <= 0) throw new InvalidOperationException("Card templates were not realized.");
                if (card.SurfaceBackground is null) throw new InvalidOperationException("Card surface lost its themed fill.");
                if (VividMicaBackdrop.IsSupported && !card.MicaBackdrop.HasInputActiveConfiguration)
                    throw new InvalidOperationException("Connected card Mica did not stay input-active.");
                if (ElementCompositionPreview.GetElementVisual(card.Root).Opacity < 1)
                    throw new InvalidOperationException("Card frame gate did not hand over to the fade.");
                var bounds = card.Window.AppWindow.Position;
                var size = card.Window.AppWindow.Size;
                var pendingHide = card.HideAsync(animate: true);
                if (pendingHide.IsCompleted) throw new InvalidOperationException("Animated hide did not suspend; the race was not exercised.");
                card.Show();
                await pendingHide;
                if (!IsWindowVisible(card.Handle)) throw new InvalidOperationException("An older hide completion hid the reopened card.");
                var reopenedPosition = card.Window.AppWindow.Position;
                var reopenedSize = card.Window.AppWindow.Size;
                if (reopenedPosition.X != bounds.X || reopenedPosition.Y != bounds.Y || reopenedSize.Width != size.Width || reopenedSize.Height != size.Height)
                    throw new InvalidOperationException("Content animation moved the native card bounds.");
                AssertOffscreen(card.Window);
                checks.Add(new { surface = card.Window.Title, width = card.Root.ActualWidth, loaded = card.Root.IsLoaded, hideShowRacePassed = true });
            }
            themeControls = await SettingsWindow.ProbeThemeControlsAsync(settings, cards);
            micaControls = await SettingsWindow.ProbeMicaControlsAsync(settings, cards);
            updateControls = await SettingsWindow.ProbeUpdateControlsAsync(settings);
            cardLifecycle["firstShowWithoutActivation"] = true;
            sourceSelector = await CardGroup.ProbeSourceSelectorAsync(cards);
            cardLifecycle["hideThenImmediateShow"] = true;
            ValidateOutsideClickPolicy(cards);
            cardLifecycle["externalClickPolicy"] = true;
            weatherCardControls = await CardGroup.ProbeWeatherCardAsync(cards, Path.Combine(AppContext.BaseDirectory, "..", "ui-weather-check"));
            heatmapChecks = await CardGroup.ProbeTrafficCardAsync(cards, Path.Combine(AppContext.BaseDirectory, "..", "ui-heatmap-check"));
            var settingsHandle = WinRT.Interop.WindowNative.GetWindowHandle(settings);
            SendMessage(settingsHandle, 0x0010, 0, 0);
            settings.ShowPage("taskbar");
            if (!IsWindow(settingsHandle) || IsWindowVisible(settingsHandle)) throw new InvalidOperationException("Closing settings did not retain its reusable hidden window.");
            cardLifecycle["settingsHiddenReuse"] = true;
            var handles = cards.CardWindows.Select(card => card.Handle).ToArray();
            var closingHides = cards.CardWindows.Select(card => card.HideAsync(animate: true)).ToArray();
            if (closingHides.Any(task => task.IsCompleted)) throw new InvalidOperationException("Dispose race did not start with pending animated hides.");
            cards.Dispose();
            await Task.WhenAll(closingHides);
            if (handles.Any(IsWindow)) throw new InvalidOperationException("Disposed cards retained native windows.");
            cardLifecycle["disposeDuringHide"] = true;
            cards = null;
            audioControls = await CardGroup.ProbeAudioControlsAsync();
            weatherControls = await SettingsWindow.ProbeWeatherControlsAsync();
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess);
            if (activated || foregroundProcess == Environment.ProcessId) throw new InvalidOperationException("Probe unexpectedly activated one of its windows.");
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            cards?.Dispose(); settings?.Shutdown();
            var resultIndex = Array.IndexOf(args, "--result");
            var result = resultIndex >= 0 && resultIndex + 1 < args.Length ? args[resultIndex + 1] : Path.Combine(AppContext.BaseDirectory, "template-probe.json");
            var winUiModule = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                .FirstOrDefault(module => string.Equals(module.ModuleName, "Microsoft.ui.xaml.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
            File.WriteAllText(result, JsonSerializer.Serialize(new { success = error is null, error, runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(), winUiModule, checks, cardLifecycle, audioControls, weatherControls, weatherCardControls, themeControls, micaControls, cardLyricsControls, sourceSelector, offsetSettings, reorderControls, heatmapChecks, updateCard, updateControls, updateOrdering, layoutChecks, probeActivated = activated, externalForegroundChanged = GetForegroundWindow() != before }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = error is null ? 0 : 1;
            app.Exit();
        }
    }
    private static void ValidateOutsideClickPolicy(CardGroup cards)
    {
        // Feed captured-click records to the production classifier. No mouse input
        // is injected and no global hook is installed by this component probe.
        var outside = new OutsideClickMonitor.Click(100, 100, OutsideClickMonitor.Button.Left,
            (nint)123, (nint)123, (nint)123, 0, false, false, false);
        foreach (var card in cards.CardWindows)
        {
            var position = card.Window.AppWindow.Position;
            if (cards.ShouldDismissClick(outside with { X = position.X + 5, Y = position.Y + 5 }))
                throw new InvalidOperationException("Clicking inside an actual card HWND would dismiss the group.");
        }
        if (!cards.ShouldDismissClick(outside) ||
            !cards.ShouldDismissClick(outside with { MouseButton = OutsideClickMonitor.Button.Right }) ||
            cards.ShouldDismissClick(outside with { IsEntryTarget = true }) ||
            !cards.ShouldDismissClick(outside with { IsEntryTarget = true, MouseButton = OutsideClickMonitor.Button.Right }))
            throw new InvalidOperationException("External clicks and left-only native entry exclusion were not distinguished.");
        var ownPopup = outside with { ProcessId = (uint)Environment.ProcessId, IsPopup = true, FlyoutWasOpen = true };
        if (cards.ShouldDismissClick(ownPopup) ||
            cards.ShouldDismissClick(ownPopup with { FlyoutWasOpen = false, RootOwner = cards.CardWindows[0].Handle }) ||
            !cards.ShouldDismissClick(ownPopup with { IsPopup = false }) ||
            !cards.ShouldDismissClick(ownPopup with { ProcessId = 0 }))
            throw new InvalidOperationException("Own flyout snapshots, unrelated own windows, and external popups were not distinguished.");
    }
    private static void ShowOffscreen(Window window, int width, int height)
    {
        PositionOffscreen(window, width, height);
        window.AppWindow.Show(false); // Never calls Window.Activate or requests focus.
        AssertOffscreen(window);
    }
    private static void PositionOffscreen(Window window, int width, int height)
    {
        PreventProbeActivation(window);
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, width, height));
    }

    // Geometry snapshot used to prove prewarm never disturbs a hidden card.
    private static (int X, int Y, int Width, int Height)[] GetCardBounds(CardGroup cards) =>
        cards.CardWindows.Select(card =>
        {
            var window = card.Window.AppWindow;
            return (window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
        }).ToArray();

    internal static void PreventProbeActivation(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // Automation peers may ask WinUI to focus a control. Isolate test HWNDs
        // at the OS boundary as well as using Show(false); production is unchanged.
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, (nint)(style | 0x08000000L)); // WS_EX_NOACTIVATE
    }
    private static void AssertOffscreen(Window window)
    {
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        if (position.X + size.Width >= -20000 || position.Y + size.Height >= -20000)
        {
            window.AppWindow.Hide();
            throw new InvalidOperationException("Probe window did not stay offscreen.");
        }
    }
    internal static void AssertOwnedWindowsOffscreen()
    {
        string? error = null;
        EnumWindows((handle, _) => {
            GetWindowThreadProcessId(handle, out var process);
            if (process == Environment.ProcessId && IsWindowVisible(handle) && GetWindowRect(handle, out var bounds) &&
                (bounds.Right >= -20000 || bounds.Bottom >= -20000))
            {
                error = $"Probe popup escaped offscreen bounds: {bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}.";
                return false;
            }
            return true;
        }, 0);
        if (error is not null) throw new InvalidOperationException(error);
    }
    private delegate bool EnumWindowCallback(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowBounds bounds);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
