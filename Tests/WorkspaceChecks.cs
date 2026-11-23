using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Models;
using Agmen.Orchestration;

namespace Agmen.Tests;

internal static class WorkspaceChecks
{
    internal static void Run()
    {
        var history = Guid.NewGuid();
        var source = new HostedTab(@"C:\project", "original-conversation", knownTitle: "design",
            category: TabCategory.Planning, agent: AgentKind.Claude)
        {
            Note = "check the rendering", ParentId = Guid.NewGuid(), IsCollapsed = true,
            LaunchModel = "opus", LaunchEffort = "high"
        };
        var replacement = WorkspaceWindow.NewContinuation(source, AgentKind.Kimi, history);
        if (replacement.Id != source.Id || replacement.ParentId != source.ParentId || replacement.Title != "design"
            || replacement.Cwd != source.Cwd || replacement.Note != source.Note || !replacement.IsCollapsed
            || replacement.Category != TabCategory.Planning || replacement.Agent != AgentKind.Kimi)
            throw new Exception("Switching providers lost the tab's place or metadata.");
        if (replacement.HandoffId != history || replacement.SessionId != null || source.SessionId != "original-conversation"
            || replacement.LaunchModel != null || replacement.LaunchEffort != null)
            throw new Exception("The replacement did not get a fresh conversation with its own agent settings.");

        var primary = new HostedTab(PrimarySetup.Folder, "old-primary", isPrimary: true, agent: AgentKind.Kimi);
        var next = WorkspaceWindow.NewContinuation(primary, AgentKind.Claude, history);
        if (!next.IsPrimary || next.Id != primary.Id || next.LaunchName != PrimarySetup.SessionName
            || next.SessionId != null || next.HandoffId != history)
            throw new Exception("A primary switch must keep its tree identity and launch with the target's primary configuration.");

        foreach (var agent in AgentProfiles.All)
        {
            var fork = WorkspaceWindow.NewChildTab(source, agent, source.Cwd, "review", "check the conclusion",
                null, null, null, history);
            if (fork.ParentId != source.Id || fork.Id == source.Id || fork.SessionId != null || fork.HandoffId != history
                || fork.Agent != agent || fork.IsPrimary || source.HandoffId != null)
                throw new Exception($"The {agent} fork did not get an independent conversation under its source.");
            if (agent == AgentKind.Claude ? !fork.HasQueuedKey("handoff") : fork.InitialPrompt?.Contains("read_handoff") != true
                || fork.InitialPrompt?.Contains("check the conclusion") != true || fork.ReportFirstPromptTo != source.Id)
                throw new Exception($"The {agent} fork did not receive its context instruction and task.");
        }

        source.MarkEnded(null);
        if (source.SwapBlockReason() != null) throw new Exception("An ended session should still be able to hand off.");
        source.NoteUserKey(clearsInput: false);
        if (source.SwapBlockReason() == null) throw new Exception("A switch would discard a draft.");
        source.NoteUserKey(clearsInput: true);
        long version = source.ActivityVersion;
        source.Enqueue(new OutboxMessage { Text = "a new instruction" });
        if (source.SwapBlockReason() == null || source.ActivityVersion == version)
            throw new Exception("An arriving message must invalidate a switch that is capturing history.");
        source.ClearQueue();
        if (source.SwapBlockReason() != null) throw new Exception("A discarded message should no longer block a switch.");
    }
}
