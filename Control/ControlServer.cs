using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agmen.Services;

namespace Agmen.Control;

/// <summary>
/// Listens on the control pipe inside the running app and hands each request to the
/// orchestrator, with the caller's process chain. Requests arrive from the tool servers and
/// hook commands that agent CLIs run in Agmen's tabs.
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly Func<string, JsonObject, IReadOnlyList<int>, Task<JsonObject>> _handle;
    private readonly CancellationTokenSource _stop = new();

    public ControlServer(Func<string, JsonObject, IReadOnlyList<int>, Task<JsonObject>> handle) => _handle = handle;

    public void Start()
    {
        _ = Task.Run(AcceptLoopAsync);
        Log.Write($"control: listening on pipe {ControlChannel.PipeName}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(ControlChannel.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var connection = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connection));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Write($"control: accept failed: {ex.Message}");
                try { await Task.Delay(2000, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                string? line;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var reader = new StreamReader(pipe, ControlChannel.Utf8, false, 4096, leaveOpen: true);
                    line = await reader.ReadLineAsync(timeout.Token);
                }
                if (string.IsNullOrWhiteSpace(line)) return;

                JsonObject response;
                try
                {
                    var request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not a JSON object");
                    var tool = request["tool"] is JsonValue v && v.TryGetValue<string>(out var name) ? name : "";
                    var args = request["args"] as JsonObject ?? new JsonObject();
                    var caller = (request["caller"] as JsonArray)?
                        .Select(n => n is JsonValue p && p.TryGetValue<int>(out var pid) ? pid : 0)
                        .Where(pid => pid > 0).ToList() ?? new List<int>();
                    response = await _handle(tool, args, caller);
                }
                catch (Exception ex)
                {
                    response = new JsonObject { ["ok"] = false, ["text"] = "Bad request: " + ex.Message };
                }

                await using var writer = new StreamWriter(pipe, ControlChannel.Utf8, 4096, leaveOpen: true) { NewLine = "\n" };
                await writer.WriteLineAsync(response.ToJsonString(ControlChannel.Json));
                await writer.FlushAsync();
            }
            catch (Exception ex) { Log.Write($"control: request failed: {ex.Message}"); }
        }
    }

    public void Dispose() => _stop.Cancel();
}
