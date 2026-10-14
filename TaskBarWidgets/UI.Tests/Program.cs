using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TaskBarWidgets.UI;

var pipeName = "TaskBarWidgets.UI.test." + Guid.NewGuid().ToString("N");
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var connection = new UiConnection(pipeName);
var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var eventReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
connection.ConnectionChanged += value => { if (value) connected.TrySetResult(); else disconnected.TrySetResult(); };
connection.EventReceived += value => eventReceived.TrySetResult(value);
connection.Start();
using var server = CreateServer(pipeName);
await server.WaitForConnectionAsync(lifetime.Token);
await connected.Task.WaitAsync(lifetime.Token);
var reader = new StreamReader(server, new UTF8Encoding(false), false, 8192, true);
var writer = new StreamWriter(server, new UTF8Encoding(false), 8192, true) { AutoFlush = true };

var successRequest = connection.RequestAsync("getState", new { sample = true }, lifetime.Token);
var first = await ReadRequest(reader);
Assert(first.GetProperty("version").GetInt32() == 1 && first.GetProperty("type").GetString() == "command", "Command envelope");
Assert(first.GetProperty("payload").GetProperty("sample").GetBoolean(), "Command payload");
await writer.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, type = "command", action = "uiResult", requestId = first.GetProperty("requestId").GetString(), success = true, payload = new { title = "test" } }));
Assert((await successRequest).GetProperty("title").GetString() == "test", "Reply payload");
await writer.WriteLineAsync("{\"version\":\"invalid\",\"action\":\"openSettings\"}");
await writer.WriteLineAsync("{\"version\":1,\"type\":\"command\",\"action\":\"openSettings\",\"payload\":{\"page\":\"audio\"}}");
Assert((await eventReceived.Task.WaitAsync(lifetime.Token)).GetProperty("action").GetString() == "openSettings", "Backend event");

var failedRequest = connection.RequestAsync("fail", cancellationToken: lifetime.Token);
var failed = await ReadRequest(reader);
await writer.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, action = "uiResult", requestId = failed.GetProperty("requestId").GetString(), success = false, error = "expected" }));
await Reject<InvalidOperationException>(failedRequest);

using var cancellation = new CancellationTokenSource();
var cancelled = connection.RequestAsync("cancel", cancellationToken: cancellation.Token);
await ReadRequest(reader);
cancellation.Cancel();
await Reject<OperationCanceledException>(cancelled);
await Reject<InvalidDataException>(connection.RequestAsync("oversized", new string('x', UiConnection.MaximumMessageBytes)));

using var pendingCancellation = new CancellationTokenSource();
var pending = new List<Task<JsonElement>>();
for (var index = 0; index < 64; index++)
{
    pending.Add(connection.RequestAsync("pending", cancellationToken: pendingCancellation.Token));
    await ReadRequest(reader);
}
await Reject<IOException>(connection.RequestAsync("over-capacity", cancellationToken: lifetime.Token));
Assert(connection.IsConnected, "Capacity rejection must preserve the connection");
pendingCancellation.Cancel();
foreach (var request in pending) await Reject<OperationCanceledException>(request);

var interrupted = connection.RequestAsync("disconnect", cancellationToken: lifetime.Token);
await ReadRequest(reader);
writer.Dispose();
reader.Dispose();
server.Dispose();
await Reject<IOException>(interrupted);
await disconnected.Task.WaitAsync(lifetime.Token);
connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
using var replacement = CreateServer(pipeName);
await replacement.WaitForConnectionAsync(lifetime.Token);
await connected.Task.WaitAsync(lifetime.Token);
// An oversized unterminated frame must disconnect before unbounded allocation.
await replacement.WriteAsync(new byte[UiConnection.MaximumMessageBytes + 1], lifetime.Token);
await replacement.FlushAsync(lifetime.Token);
await disconnected.Task.WaitAsync(lifetime.Token);
Console.WriteLine("UI pipe checks passed: replies, events, errors, cancellation, reconnect, message bounds.");

static NamedPipeServerStream CreateServer(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
static async Task<JsonElement> ReadRequest(StreamReader reader)
{
    using var document = JsonDocument.Parse(await reader.ReadLineAsync() ?? throw new IOException("Missing request"));
    return document.RootElement.Clone();
}
static void Assert(bool condition, string name) { if (!condition) throw new Exception(name); }
static async Task Reject<T>(Task request) where T : Exception
{
    try { await request; } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
