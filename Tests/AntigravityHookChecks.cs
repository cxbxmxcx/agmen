using System.Text.Json.Nodes;
using Agmen.Agents;

namespace Agmen.Tests;

/// <summary>
/// Antigravity's hooks for a turn that runs a subagent, in the order agy 1.5.15 sends them. The
/// subagent reports in a conversation of its own, and the tab's conversation stops while it waits.
/// </summary>
internal static class AntigravityHookChecks
{
    internal static void Run()
    {
        var monitor = new AgentMonitor();
        // Started long ago, so no log file of a real agy process is taken for this tab's.
        var tab = new AgentTabRef(Guid.NewGuid(), AgentKind.Antigravity, Environment.CurrentDirectory, Environment.ProcessId,
                                  new DateTime(2000, 1, 1), null);
        string main = Guid.NewGuid().ToString(), sub = Guid.NewGuid().ToString();

        Hook("SessionStart", main);
        Hook("PreInvocation", main);
        Hook("SessionStart", sub);
        Hook("PreInvocation", sub);
        Hook("Stop", main, fullyIdle: false);
        Expect("busy", "while its conversation waits on a subagent");
        Hook("Stop", sub, fullyIdle: true);
        Expect("busy", "when its subagent stops");
        Hook("PreInvocation", main);
        Hook("Stop", main, fullyIdle: true);
        Expect("idle", "when its own conversation is done");
        var state = monitor.StateOf(tab.Id);
        if (state?.SessionId != main) throw new Exception("A subagent's hooks took over an Antigravity tab's conversation.");
        if (state.TurnsCompleted != 1) throw new Exception($"An Antigravity turn with a subagent counted as {state.TurnsCompleted} turns.");

        // A conversation that reports while the tab is idle is one the user switched to.
        string next = Guid.NewGuid().ToString();
        Hook("SessionStart", next);
        Hook("PreInvocation", next);
        Expect("busy", "in a conversation the user switched to");
        if (monitor.StateOf(tab.Id)?.SessionId != next) throw new Exception("An Antigravity tab didn't follow a conversation the user switched to.");

        void Hook(string evt, string conversation, bool? fullyIdle = null)
        {
            var payload = new JsonObject { ["conversationId"] = conversation, ["hookEventName"] = evt };
            if (fullyIdle is { } idle) payload["fullyIdle"] = idle;
            monitor.OnHook(tab, evt, payload);
        }

        void Expect(string status, string when)
        {
            var seen = monitor.Poll([tab], tab.StartedAt)[tab.Id].Status;
            if (seen != status) throw new Exception($"An Antigravity tab showed {seen} {when}, not {status}.");
        }
    }
}
