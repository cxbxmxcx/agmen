using System.IO;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Services;

namespace Agmen.Tests;

internal static class WorkspaceSessionChecks
{
    internal static void Run()
    {
        var folder = Path.Combine(AppPaths.DataDir, "scope-check");
        var first = new WorkspaceSessions(folder, "Research", false);
        var second = new WorkspaceSessions(folder, "Writing", false);
        var dashboard = new WorkspaceSessions(folder, "Default", true);
        var id = Guid.NewGuid().ToString();
        if (first.Visible(AgentKind.Codex, id) || !dashboard.Visible(AgentKind.Codex, id))
            throw new Exception("Only the default dashboard discovers native sessions created outside Agmen.");
        first.Claim(AgentKind.Codex, id);
        first.Claim(AgentKind.Codex, id.ToUpperInvariant());
        if (!first.Visible(AgentKind.Codex, id) || second.Visible(AgentKind.Codex, id) || dashboard.Visible(AgentKind.Codex, id))
            throw new Exception("Named-workspace history escaped its workspace.");
        if (!dashboard.Visible(AgentKind.Claude, id)) throw new Exception("Provider identity must be part of a session key.");
        try { second.Claim(AgentKind.Codex, id); throw new Exception("A different workspace reopened a managed conversation."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Research")) { }

        string race = Guid.NewGuid().ToString();
        int owners = 0;
        Parallel.ForEach(new[] { first, second }, scope =>
        {
            try { scope.Claim(AgentKind.Kimi, race); Interlocked.Increment(ref owners); }
            catch (InvalidOperationException) { }
        });
        if (owners != 1) throw new Exception("Simultaneous launches must agree on the workspace that owns the session.");
    }
}
