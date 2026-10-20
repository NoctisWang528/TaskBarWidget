using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using TaskBarWidgets.UI.Cards;

namespace TaskBarWidgets.UI;

public sealed partial class CardGroup
{
    // Uses real WinUI button invocation and the real UI pipe, but only a local
    // synthetic backend. Never enumerates or changes the user's audio devices.
    internal static async Task<object> ProbeAudioControlsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var pipeName = "TaskBarWidgets.UI.audio-probe." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = server.WaitForConnectionAsync(timeout.Token);
        using var connection = new UiConnection(pipeName);
        connection.Start();
        await accepted;
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using var cards = new CardGroup(connection, _ => throw new InvalidOperationException("Audio probe must not open settings."));
        var activated = false;
        foreach (var card in cards.CardWindows) {
            UiTemplateProbe.PreventProbeActivation(card.Window);
            card.Window.Activated += (_, args) => {
                if (args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated) activated = true;
            };
        }
        var selected = "speaker-a";
        var outputMuted = false;
        var inputMuted = false;
        object Snapshot() => new {
            outputs = new[] {
                new { id = "speaker-a", name = "Probe speaker A", isDefault = selected == "speaker-a", isCommunications = false, volume = .38, isMuted = outputMuted },
                new { id = "speaker-b", name = "Probe speaker B", isDefault = selected == "speaker-b", isCommunications = false, volume = .38, isMuted = outputMuted } },
            inputs = new[] { new { id = "microphone", name = "Probe microphone", isDefault = true, isCommunications = false, volume = .50, isMuted = inputMuted } }
        };
        cards.RenderAudio(JsonSerializer.SerializeToElement(Snapshot()));
        var audioCardForProbe = cards._cardsById["device"];
        audioCardForProbe.Window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-30000, -30000, 480, 340));
        audioCardForProbe.Show();
        await Task.Delay(200, timeout.Token);
        while (!connection.IsConnected) await Task.Delay(10, timeout.Token);

        async Task RespondAsync(string expectedAction, string expectedFlow, bool? expectedMute = null)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Audio probe disconnected.");
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            var payload = root.GetProperty("payload");
            if (root.GetProperty("action").GetString() != expectedAction) throw new InvalidOperationException("Audio action performed an unnecessary refresh or unexpected command.");
            if (payload.GetProperty("flow").GetString() != expectedFlow) throw new InvalidOperationException("Audio command targeted the wrong flow.");
            if (expectedAction == "selectDevice") selected = payload.GetProperty("deviceId").GetString()!;
            else {
                var expectedDevice = expectedFlow == "input" ? "microphone" : selected;
                if (payload.GetProperty("deviceId").GetString() != expectedDevice || payload.TryGetProperty("volume", out _))
                    throw new InvalidOperationException("Mute must target the current endpoint without sending a volume write.");
                var muted = payload.GetProperty("muted").GetBoolean();
                if (muted != expectedMute) throw new InvalidOperationException("Mute button did not toggle the read-back state.");
                if (expectedFlow == "input") inputMuted = muted;
                else outputMuted = muted;
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, type = "command", action = "uiResult",
                requestId = root.GetProperty("requestId").GetString(), success = true, payload = Snapshot() }));
        }
        var switchReply = RespondAsync("selectDevice", "output");
        var switching = cards.SelectDeviceAsync("output", "speaker-b");
        if (!cards._inputMute.IsEnabled || cards._outputMute.IsEnabled) throw new InvalidOperationException("Device switch must disable only its own flow.");
        await switching.WaitAsync(timeout.Token);
        await switchReply;
        if (cards._outputId != "speaker-b" || !cards._outputMute.IsEnabled) throw new InvalidOperationException("Switch reply did not immediately update the output endpoint.");

        foreach (var flow in new[] { "output", "input" })
        {
            var button = flow == "input" ? cards._inputMute : cards._outputMute;
            var volume = flow == "input" ? cards._inputVolume : cards._outputVolume;
            var expectedVolume = flow == "input" ? 50d : 38d;
            foreach (var expected in new[] { true, false })
            {
                var response = RespondAsync("setMute", flow, expected);
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await response;
                while (!button.IsEnabled) await Task.Delay(10, timeout.Token);
                // Invoke may be dispatched asynchronously; wait for the readback, not just the reply write.
                while ((flow == "input" ? cards._inputMuted : cards._outputMuted) != expected)
                    await Task.Delay(10, timeout.Token);
                var expectedKind = (flow, expected) switch
                {
                    ("input", true) => CardIconKind.MicrophoneMuted,
                    ("input", false) => CardIconKind.Microphone,
                    ("output", true) => CardIconKind.SpeakerMuted,
                    _ => CardIconKind.Speaker
                };
                if (Math.Abs(volume.Value - expectedVolume) > .01 ||
                    button.Content is not CardIconHost { Kind: var actualKind } ||
                    actualKind != expectedKind)
                    throw new InvalidOperationException("Mute changed volume or failed to update its vector state.");
            }
        }
        var older = JsonSerializer.SerializeToElement(Snapshot());
        outputMuted = true;
        cards.ApplyAudioReply(JsonSerializer.SerializeToElement(Snapshot()), 100);
        cards.ApplyAudioReply(older, 99);
        if (!cards._outputMuted) throw new InvalidOperationException("An old audio reply overwrote the latest mute state.");
        if (activated) throw new InvalidOperationException("Audio probe unexpectedly activated a window.");
        var audioCard = cards._cardsById["device"];
        var mediaCard = cards._cardsById["media"];
        mediaCard.Window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-30000, -30000, 480, 340));
        mediaCard.Show();
        await Task.Delay(200, timeout.Token);
        audioCard.Root.UpdateLayout();
        mediaCard.Root.UpdateLayout();
        var centerErrors = new List<double>();
        var centerDiagnostics = new List<string>();
        foreach (var button in cards.CardWindows.SelectMany(card => Walk(card.Root)).OfType<Button>()
                     .Where(button => button.Content is CardIconHost))
        {
            var host = (CardIconHost)button.Content;
            var center = host.TransformToVisual(button).TransformPoint(
                new Windows.Foundation.Point(host.ActualWidth / 2, host.ActualHeight / 2));
            var target = new Windows.Foundation.Point(button.ActualWidth / 2, button.ActualHeight / 2);
            var error = Math.Sqrt(Math.Pow(center.X - target.X, 2) + Math.Pow(center.Y - target.Y, 2));
            centerErrors.Add(error);
            var chain = new List<string>();
            for (DependencyObject? current = host; current is not null && !ReferenceEquals(current, button); current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement element)
                    chain.Add($"{current.GetType().Name}[{element.ActualWidth:0.###}x{element.ActualHeight:0.###};m={element.Margin};a={element.HorizontalAlignment}/{element.VerticalAlignment}]");
                else chain.Add(current.GetType().Name);
            }
            centerDiagnostics.Add($"{Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button)}={error:0.###} " +
                $"host={host.ActualWidth:0.###}x{host.ActualHeight:0.###}@{center.X:0.###},{center.Y:0.###} " +
                $"button={button.ActualWidth:0.###}x{button.ActualHeight:0.###}@{target.X:0.###},{target.Y:0.###} " +
                $"chain={string.Join(" > ", chain)}");
        }
        if (centerErrors.Count < 6 || centerErrors.Any(error => error > 0.5))
            throw new InvalidOperationException($"Circular icon hosts were not centered within 0.5 DIP: {string.Join("; ", centerDiagnostics)}.");

        var screenshots = Path.Combine(AppContext.BaseDirectory, "..", "ui-audio-check");
        Directory.CreateDirectory(screenshots);
        cards.ApplyBackendTheme(JsonSerializer.SerializeToElement(new { toolWindowTheme = 2 }));
        await Task.Delay(80, timeout.Token);
        audioCard.Root.UpdateLayout();
        await UiLayoutProbe.CaptureAsync(audioCard.Root, Path.Combine(screenshots, "audio-light.png"));
        cards.ApplyBackendTheme(JsonSerializer.SerializeToElement(new { toolWindowTheme = 1 }));
        await Task.Delay(80, timeout.Token);
        audioCard.Root.UpdateLayout();
        await UiLayoutProbe.CaptureAsync(audioCard.Root, Path.Combine(screenshots, "audio-dark.png"));

        cards._status.Text = ""; audioCard.MeasureHeight(); var compactHeight = audioCard.Height;
        cards._status.Text = "Synthetic device error with enough detail to wrap onto a second line and verify the measured card grows with content.";
        audioCard.MeasureHeight(); var errorHeight = audioCard.Height;
        cards._status.Text = ""; audioCard.MeasureHeight();
        if (errorHeight <= compactHeight || audioCard.Height != compactHeight)
            throw new InvalidOperationException("Audio card did not grow and shrink with its visible status content.");
        return new { switchReadback = true, independentFlowControls = true, muteButtonRoundTrip = true, volumePreserved = true, staleReplyIgnored = true,
            inputMuteButtonRoundTrip = true, centeredIconHosts = centerErrors.Count,
            maximumCenterError = centerErrors.Max(), compactHeight, errorHeight, contentHeightRestored = true,
            lightScreenshot = "ui-audio-check/audio-light.png", darkScreenshot = "ui-audio-check/audio-dark.png",
            realAudioDevicesTouched = false };
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index)))
                yield return child;
    }
}
