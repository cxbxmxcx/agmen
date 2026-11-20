using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agmen.Control;
using Agmen.Services;

namespace Agmen.Mcp;

/// <summary>
/// A small Model Context Protocol server over stdio: newline-delimited JSON-RPC 2.0. An agent
/// CLI starts it as <c>Agmen.exe --mcp</c>. It serves the tool list itself and relays every tool
/// call to the running Agmen over the control pipe, with its own process chain, so Agmen knows
/// which tab is calling. It holds no state beyond its role and exits when the CLI closes stdin.
/// </summary>
public static class McpServer
{
    private static readonly string[] KnownVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    private static readonly object WriteGate = new();
    private static StreamWriter? _out;
    private static List<int> _chain = new();
    /// <summary>"primary", "tab" or "none", asked of Agmen when the session connects.</summary>
    private static string _role = "none";

    public static int Run()
    {
        var ancestry = ProcessTree.Ancestry(Environment.ProcessId);
        _chain = ancestry.Select(n => n.Pid).ToList();
        if (ProbeLog.Enabled)
            ProbeLog.Write(new JsonObject
            {
                ["kind"] = "mcp-start",
                ["ancestry"] = string.Join(" <- ", ancestry.Select(n => $"{n.Pid} {n.Name}")),
            });

        var input = new StreamReader(Console.OpenStandardInput(), ControlChannel.Utf8);
        _out = new StreamWriter(Console.OpenStandardOutput(), ControlChannel.Utf8) { NewLine = "\n", AutoFlush = true };
        var inFlight = new List<Task>();

        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException)
            {
                Send(Error(null, -32700, "Parse error"));
                continue;
            }
            if (message is null) continue;

            var method = Str(message["method"]);
            var id = message["id"]?.DeepClone();
            // Notifications (no id) need no answer, and this server never sends requests of its own.
            if (method is null || id is null) continue;
            var parameters = message["params"] as JsonObject ?? new JsonObject();

            switch (method)
            {
                case "initialize":
                    _role = AskRole(ancestry);
                    if (ProbeLog.Enabled)
                        ProbeLog.Write(new JsonObject
                        {
                            ["kind"] = "mcp-initialize",
                            ["client"] = parameters["clientInfo"]?.DeepClone(),
                            ["role"] = _role,
                        });
                    Send(Result(id, Initialize(parameters)));
                    break;
                case "ping":
                    Send(Result(id, new JsonObject()));
                    break;
                case "tools/list":
                    Send(Result(id, new JsonObject { ["tools"] = McpTools.Catalog(_role) }));
                    break;
                case "tools/call":
                    inFlight.RemoveAll(t => t.IsCompleted);
                    inFlight.Add(Task.Run(() => CallToolAsync(id, parameters)));
                    break;
                default:
                    Send(Error(id, -32601, $"Method not found: {method}"));
                    break;
            }
        }

        // The CLI closed stdin: let calls already under way answer, then exit.
        Task.WaitAll(inFlight.ToArray(), TimeSpan.FromSeconds(5));
        return 0;
    }

    /// <summary>
    /// Which session this server serves. Kimi and Antigravity read the server from their global
    /// config, so it also starts in their sessions outside Agmen; with no Agmen above it in the
    /// process tree it is "none" at once, and offers no tools.
    /// </summary>
    private static string AskRole(List<ProcessTree.Node> ancestry)
    {
        if (!ancestry.Skip(1).Any(n => n.Name.Equals("Agmen", StringComparison.OrdinalIgnoreCase))) return "none";
        try
        {
            var answer = ControlChannel.CallAsync("agmen:role", new JsonObject(), TimeSpan.FromSeconds(5), _chain)
                .GetAwaiter().GetResult();
            return Str(answer["role"]) ?? "none";
        }
        catch (Exception) { return "none"; }
    }

    private static JsonObject Initialize(JsonObject parameters)
    {
        var requested = Str(parameters["protocolVersion"]);
        var version = requested is not null && KnownVersions.Contains(requested) ? requested : KnownVersions[1];
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "agmen", ["title"] = "Agmen", ["version"] = appVersion },
            ["instructions"] = McpTools.InstructionsFor(_role),
        };
    }

    private static async Task CallToolAsync(JsonNode id, JsonObject parameters)
    {
        var name = Str(parameters["name"]) ?? "";
        var args = parameters["arguments"] as JsonObject ?? new JsonObject();
        JsonObject result;
        if (!McpTools.Allowed(_role, name))
            result = ToolResult(McpTools.Names.Contains(name)
                ? $"'{name}' isn't available to this session."
                : $"Agmen has no tool called '{name}'.", isError: true);
        else
        {
            try
            {
                var answer = await ControlChannel.CallAsync(name, args, TimeSpan.FromSeconds(90), _chain);
                bool ok = answer["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                result = ToolResult(Str(answer["text"]) ?? "", isError: !ok);
            }
            catch (TimeoutException)
            {
                result = ToolResult("Agmen isn't running, or isn't answering. Ask the user to start Agmen from the Start menu.", isError: true);
            }
            catch (OperationCanceledException)
            {
                result = ToolResult("Agmen took too long to answer. Try again in a moment.", isError: true);
            }
            catch (Exception ex)
            {
                result = ToolResult($"Couldn't reach Agmen: {ex.Message}", isError: true);
            }
        }
        Send(Result(id, result));
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static void Send(JsonObject message)
    {
        var text = message.ToJsonString(ControlChannel.Json);
        lock (WriteGate) _out!.WriteLine(text);
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
