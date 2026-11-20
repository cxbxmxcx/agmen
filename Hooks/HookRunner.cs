using System.Text.Json;
using System.Text.Json.Nodes;
using Agmen.Control;
using Agmen.Services;

namespace Agmen.Hooks;

/// <summary>
/// <c>Agmen.exe --hook &lt;agent&gt; &lt;event&gt; [payload]</c>: what an agent CLI runs when a session
/// starts, a model call begins, or a turn ends. It passes the signal and its payload to the
/// running Agmen, with its own process chain, so Agmen can tell which tab it came from.
/// Antigravity's hooks are global, so they also fire in sessions outside Agmen; then the
/// command finds no Agmen above it and leaves at once. It never holds the agent up: stdin and
/// the pipe get short time limits, and it always exits 0.
/// </summary>
public static class HookRunner
{
    public static int Run(string[] args)
    {
        string agent = args.Length > 1 ? args[1] : "?";
        string evt = args.Length > 2 ? args[2] : "?";

        // Codex's notify setting passes its JSON as one more argument; hooks send JSON on stdin.
        string? payloadArg = args.Length > 3 ? string.Join(" ", args[3..]) : null;
        string? stdin = null;
        if (Console.IsInputRedirected)
        {
            var read = Task.Run(() => Console.In.ReadToEnd());
            if (read.Wait(TimeSpan.FromSeconds(3))) stdin = read.Result;
        }

        var chain = ProcessTree.Ancestry(Environment.ProcessId);
        bool insideAgmen = chain.Skip(1).Any(n => n.Name.Equals("Agmen", StringComparison.OrdinalIgnoreCase));
        if (insideAgmen)
        {
            var request = new JsonObject
            {
                ["agent"] = agent,
                ["event"] = evt,
                ["payload"] = Parse(stdin) ?? Parse(payloadArg),
            };
            try
            {
                ControlChannel.CallAsync("agmen:hook", request, TimeSpan.FromSeconds(3), chain.Select(n => n.Pid).ToList())
                    .Wait(TimeSpan.FromSeconds(4));
            }
            catch (Exception) { /* Agmen is busy or closing; the next signal will do */ }
        }

        if (ProbeLog.Enabled)
            ProbeLog.Write(new JsonObject
            {
                ["kind"] = "hook",
                ["agent"] = agent,
                ["event"] = evt,
                ["arg"] = payloadArg,
                ["stdin"] = stdin,
                ["ancestry"] = string.Join(" <- ", chain.Select(n => $"{n.Pid} {n.Name}")),
            });

        // Antigravity reads JSON back from every hook, and a Stop hook must say whether to stop.
        if (agent == "agy") Console.Out.Write(evt == "Stop" ? "{\"decision\":\"stop\"}" : "{}");
        return 0;
    }

    private static JsonObject? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
