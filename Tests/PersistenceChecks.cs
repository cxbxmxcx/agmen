using System.IO;
using System.Text.Json;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Services;

namespace Agmen.Tests;

internal static class PersistenceChecks
{
    internal static void Run()
    {
        var id = Guid.NewGuid();
        var cwd = AppPaths.DataDir;
        var legacy = new { Tabs = new[] { new { Id = id, Cwd = cwd, SessionId = "native", Title = "old", Ended = true } } };
        File.WriteAllText(AppPaths.PathOf("workspace.json"), JsonSerializer.Serialize(legacy));
        var state = WorkspaceStore.Load();
        if (state.Tabs.Single() is not { HandoffId: null, SessionId: "native" } old || old.Id != id)
            throw new Exception("Existing workspaces must open without a handoff.");

        Guid history = Guid.NewGuid();
        var continuation = old with { HandoffId = history, Agent = "kimi", SessionId = null };
        var child = new SavedTab(Guid.NewGuid(), cwd, null, "fork", true, ParentId: id, HandoffId: history);
        state.Tabs = [continuation, child];
        state.PrimaryHandoffs = new() { ["kimi"] = history };
        WorkspaceStore.Save(state);
        var restored = WorkspaceStore.Load();
        if (!restored.Tabs.SequenceEqual(state.Tabs) || restored.PrimaryHandoffs?["kimi"] != history)
            throw new Exception("Workspace restore lost a handoff or its place in the tree.");

        var closed = new HistoryEntry(id, "kimi", cwd, "", "continuation", DateTimeOffset.Now, HandoffId: history);
        var alternate = closed with { TabId = child.Id, ParentId = id };
        SessionHistory.Record([closed, alternate]);
        var reopened = SessionHistory.Load();
        if (!reopened.SequenceEqual(new[] { closed, alternate }) || !SessionHistory.ClosedUnder(closed, reopened).SequenceEqual([alternate]))
            throw new Exception("Closed tabs lost their independent identities before their first native session IDs arrived.");
        SessionHistory.Forget([closed]);
        if (!SessionHistory.Load().SequenceEqual([alternate])) throw new Exception("Reopening one tab removed another handoff.");

        var owner = new HostedTab(cwd, null);
        var branch = new HostedTab(cwd, "native-kimi", agent: AgentKind.Kimi) { ParentId = owner.Id, HandoffId = history };
        restored.RememberNativeHandoff(branch);
        WorkspaceStore.Save(restored);
        var later = WorkspaceStore.Load();
        if (later.HandoffForNative(branch.Agent, branch.SessionId, owner) != history
            || later.HandoffForNative(branch.Agent, branch.SessionId, null) != history
            || later.HandoffForNative(AgentKind.Claude, branch.SessionId, null) != null)
            throw new Exception("Reopening a native session must reconnect its own provider's original handoff.");
        try { later.HandoffForNative(branch.Agent, branch.SessionId, new HostedTab(cwd, null)); }
        catch (InvalidOperationException) { return; }
        throw new Exception("A different branch must not inherit a closed sibling's handoff through native resume.");
    }
}
