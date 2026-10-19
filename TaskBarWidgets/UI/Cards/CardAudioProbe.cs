using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

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
        var muted = false;
        object Snapshot() => new {
            outputs = new[] {
                new { id = "speaker-a", name = "Probe speaker A", isDefault = selected == "speaker-a", isCommunications = false, volume = .38, isMuted = muted },
                new { id = "speaker-b", name = "Probe speaker B", isDefault = selected == "speaker-b", isCommunications = false, volume = .38, isMuted = muted } },
            inputs = new[] { new { id = "microphone", name = "Probe microphone", isDefault = true, isCommunications = false, volume = .50, isMuted = false } }
        };
        cards.RenderAudio(JsonSerializer.SerializeToElement(Snapshot()));
        var audioCardForProbe = cards._cardsById["device"];
        audioCardForProbe.Window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-30000, -30000, 480, 340));
        audioCardForProbe.Show();
        await Task.Delay(200, timeout.Token);
        while (!connection.IsConnected) await Task.Delay(10, timeout.Token);

        async Task RespondAsync(string expectedAction, bool? expectedMute = null)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Audio probe disconnected.");
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            var payload = root.GetProperty("payload");
            if (root.GetProperty("action").GetString() != expectedAction) throw new InvalidOperationException("Audio action performed an unnecessary refresh or unexpected command.");
            if (payload.GetProperty("flow").GetString() != "output") throw new InvalidOperationException("Output command targeted the wrong flow.");
            if (expectedAction == "selectDevice") selected = payload.GetProperty("deviceId").GetString()!;
            else {
                if (payload.GetProperty("deviceId").GetString() != selected || payload.TryGetProperty("volume", out _))
                    throw new InvalidOperationException("Mute must target the current endpoint without sending a volume write.");
                muted = payload.GetProperty("muted").GetBoolean();
                if (muted != expectedMute) throw new InvalidOperationException("Mute button did not toggle the read-back state.");
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, type = "command", action = "uiResult",
                requestId = root.GetProperty("requestId").GetString(), success = true, payload = Snapshot() }));
        }
        var switchReply = RespondAsync("selectDevice");
        var switching = cards.SelectDeviceAsync("output", "speaker-b");
        if (!cards._inputMute.IsEnabled || cards._outputMute.IsEnabled) throw new InvalidOperationException("Device switch must disable only its own flow.");
        await switching.WaitAsync(timeout.Token);
        await switchReply;
        if (cards._outputId != "speaker-b" || !cards._outputMute.IsEnabled) throw new InvalidOperationException("Switch reply did not immediately update the output endpoint.");

        foreach (var expected in new[] { true, false }) {
            var response = RespondAsync("setMute", expected);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(cards._outputMute) ?? new ButtonAutomationPeer(cards._outputMute);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await response;
            while (!cards._outputMute.IsEnabled) await Task.Delay(10, timeout.Token);
            // Invoke may be dispatched asynchronously; wait for the readback, not just the reply write.
            while (cards._outputMuted != expected) await Task.Delay(10, timeout.Token);
            if (Math.Abs(cards._outputVolume.Value - 38) > .01 || ((FontIcon)cards._outputMute.Content).Glyph != (expected ? "\uE74F" : "\uE767"))
                throw new InvalidOperationException("Mute changed volume or failed to update the icon.");
        }
        var older = JsonSerializer.SerializeToElement(Snapshot());
        muted = true;
        cards.ApplyAudioReply(JsonSerializer.SerializeToElement(Snapshot()), 100);
        cards.ApplyAudioReply(older, 99);
        if (!cards._outputMuted) throw new InvalidOperationException("An old audio reply overwrote the latest mute state.");
        if (activated) throw new InvalidOperationException("Audio probe unexpectedly activated a window.");
        var audioCard = cards._cardsById["device"];
        cards._status.Text = ""; audioCard.MeasureHeight(); var compactHeight = audioCard.Height;
        cards._status.Text = "Synthetic device error with enough detail to wrap onto a second line and verify the measured card grows with content.";
        audioCard.MeasureHeight(); var errorHeight = audioCard.Height;
        cards._status.Text = ""; audioCard.MeasureHeight();
        if (errorHeight <= compactHeight || audioCard.Height != compactHeight)
            throw new InvalidOperationException("Audio card did not grow and shrink with its visible status content.");
        return new { switchReadback = true, independentFlowControls = true, muteButtonRoundTrip = true, volumePreserved = true, staleReplyIgnored = true,
            compactHeight, errorHeight, contentHeightRestored = true, realAudioDevicesTouched = false };
    }
}
