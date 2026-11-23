using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
        _ = Task.Run(() => AcceptLoopAsync(ControlChannel.PipeName));
        _ = Task.Run(() => AcceptLoopAsync(ControlChannel.HostPipe(Environment.ProcessId)));
        Log.Write($"control: listening on pipe {ControlChannel.PipeName}");
    }

    private async Task AcceptLoopAsync(string name)
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut,
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
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer == 0 || peer > int.MaxValue)
                        throw new IOException("Could not identify the connected process.");
                    var chain = ProcessTree.Ancestry((int)peer);
                    if (peer != Environment.ProcessId && ControlChannel.NearestHost(chain) is { } nearest && nearest != Environment.ProcessId)
                        throw new IOException("The connected process belongs to another Agmen workspace.");
                    var caller = peer == Environment.ProcessId ? new[] { (int)peer }
                        : chain.TakeWhile(n => n.Pid != Environment.ProcessId).Select(n => n.Pid).ToArray();
                    response = await _handle(tool, args, caller);
                    if (ProbeLog.Enabled && tool == "agmen:hook" && (bool?)response["ok"] == true)
                        ProbeLog.Write(new JsonObject { ["kind"] = "hook", ["agent"] = args["agent"]?.DeepClone(),
                            ["event"] = args["event"]?.DeepClone(), ["payload"] = args["payload"]?.DeepClone(),
                            ["ancestry"] = string.Join(" <- ", chain.Select(n => $"{n.Pid} {n.Name}")) });
                    if (ProbeLog.Enabled && tool == "agmen:role")
                        ProbeLog.Write(new JsonObject { ["kind"] = "mcp-initialize", ["client"] = args["client"]?.DeepClone(),
                            ["role"] = response["role"]?.DeepClone(), ["ancestry"] = string.Join(" <- ", chain.Select(n => $"{n.Pid} {n.Name}")) });
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
