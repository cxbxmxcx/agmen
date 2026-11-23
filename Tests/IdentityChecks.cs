using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agmen.Agents;
using Agmen.Control;
using Agmen.Hosting;
using Agmen.Services;

namespace Agmen.Tests;

internal static class IdentityChecks
{
    internal static void Run()
    {
        var now = DateTime.Now;
        var tab = new AgentTabRef(Guid.NewGuid(), AgentKind.Codex, AppPaths.DataDir, Environment.ProcessId, now, null);
        var monitor = new AgentMonitor();
        string thread = Guid.NewGuid().ToString();
        monitor.OnHook(tab, "notify", new JsonObject { ["thread-id"] = thread, ["last-assistant-message"] = "current turn" });
        var previous = tab with { Agent = AgentKind.Antigravity, StartedAt = now.AddMinutes(-1) };
        monitor.Poll([previous], previous.StartedAt);
        monitor.Poll([], previous.StartedAt);
        monitor.OnHook(previous, "Stop", new JsonObject { ["conversationId"] = Guid.NewGuid().ToString() });
        if (monitor.StateOf(tab.Id) is not { Agent: AgentKind.Codex, LastReply: "current turn" } tracked || tracked.SessionId != thread)
            throw new Exception("A delayed poll or hook changed the session identity after a provider switch.");

        string locks = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "locks", "sessions")).FullName;
        string index = AppPaths.PathOf("session_index.jsonl");
        var own = AddSession(Environment.ProcessId);
        AddSession(ProcessTree.ParentOf(Environment.ProcessId)); // Another session in the same working directory.
        var matched = AgentMonitor.KimiSessionForProcess(AppPaths.DataDir, Environment.ProcessId, AppPaths.DataDir);
        if (matched?.Id != own || matched.Value.Dir != Path.Combine(AppPaths.DataDir, own))
            throw new Exception("Kimi handoffs must use the tab process's own session lock.");
        if (AgentMonitor.KimiSessionForProcess(locks, Environment.ProcessId, AppPaths.DataDir) != null)
            throw new Exception("Kimi handoffs must match the indexed workdir.");

        if (HostedTab.PlainMessage("a\tb\r\nc\u0001") != "a    b\nc")
            throw new Exception("Message delivery did not preserve visible text and line breaks.");
        using var server = new ControlServer((tool, args, caller) => Task.FromResult(new JsonObject
        {
            ["ok"] = tool == "identify" && caller.FirstOrDefault() == Environment.ProcessId
        }));
        server.Start();
        var response = ControlChannel.CallAsync("identify", new JsonObject(), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if ((bool?)response["ok"] != true) throw new Exception("The control pipe did not identify its Windows client process.");

        string AddSession(int pid)
        {
            string sessionId = "session_" + Guid.NewGuid();
            File.AppendAllText(index, JsonSerializer.Serialize(new { sessionId, sessionDir = Path.Combine(AppPaths.DataDir, sessionId), workDir = AppPaths.DataDir }) + "\n");
            File.WriteAllText(Path.Combine(locks, sessionId + ".lock"), JsonSerializer.Serialize(new { pid }));
            return sessionId;
        }
    }
}
