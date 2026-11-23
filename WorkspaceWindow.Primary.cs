using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Models;
using Agmen.Orchestration;
using Agmen.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Agmen;

/// <summary>
/// The primary session, and the messages Agmen types into tabs: starting the primary, typing
/// queued messages in when it is safe, and telling a parent when the turn its message started
/// is over.
/// </summary>
public partial class WorkspaceWindow
{
    /// <summary>Carries out the tool calls of every tab's tool server.</summary>
    public Orchestrator Orchestrator { get; private set; } = null!;
    /// <summary>Every live Claude Code session from the latest registry poll, including ones outside Agmen.</summary>
    public IReadOnlyList<SessionInfo> LastSnapshot { get; private set; } = Array.Empty<SessionInfo>();
    public HostedTab? Primary => _tabs.FirstOrDefault(t => t.IsPrimary);

    /// <summary>The primary asked Agmen to tell the user something, optionally about a tab.</summary>
    public event Action<string, HostedTab?>? NotifyRequested;

    private void InitPrimary() => Orchestrator = new Orchestrator(this);

    public void RaiseNotify(string text, HostedTab? tab) => NotifyRequested?.Invoke(text, tab);

    // ----------------------------------------------------------- starting the primary

    /// <summary>
    /// The agent the primary runs as: the one it runs as now, else the user's last choice, else
    /// Claude Code when it is installed, else the first agent that is.
    /// </summary>
    public AgentKind PrimaryAgent =>
        Primary?.Agent
        ?? AgentProfiles.Parse(_state.PrimaryAgent)
        ?? (AgentProfiles.IsInstalled(AgentKind.Claude) ? AgentKind.Claude
            : AgentProfiles.All.Cast<AgentKind?>().FirstOrDefault(a => AgentProfiles.IsInstalled(a!.Value)) ?? AgentKind.Claude);

    /// <summary>
    /// Show the primary, starting it first if needed. With an agent, the primary runs as that
    /// agent: a primary of another agent hands over to it. The very first start of an agent's
    /// primary asks, since it is a session of its own on that agent's plan.
    /// </summary>
    public void OpenPrimary(AgentKind? asAgent = null)
    {
        if (Primary is { } existing)
        {
            if (asAgent is { } other && other != existing.Agent)
                _ = ContinueFromMenuAsync(existing, other);
            else
            {
                if (existing.IsEnded) ResumeTab(existing);
                FocusTab(existing);
            }
            return;
        }

        var agent = asAgent ?? PrimaryAgent;
        var name = AgentProfiles.DisplayName(agent);
        if (!AgentProfiles.IsInstalled(agent))
        {
            Notice($"{name} isn't installed, so it can't run as the primary.");
            return;
        }
        // Kimi Code and Antigravity get Agmen's tools only through Connect, and a primary without them can do nothing.
        if (AgentConnect.Connectable.Contains(agent) && AgentConnect.StateOf(agent) != AgentConnect.State.Connected)
        {
            RunConnect($"{name} isn't connected to this copy of Agmen yet. The primary works through Agmen's tools, so connect {name} first.");
            if (AgentConnect.StateOf(agent) != AgentConnect.State.Connected)
            {
                Notice($"{name} isn't connected, so it can't run as the primary.");
                return;
            }
        }

        // Reopening a closed primary can resume that agent's last native conversation.
        var previous = SavedPrimarySession(agent) is { } id && CanResume(agent, id) ? id : null;
        Guid? inherited = _state.PrimaryHandoffs?.TryGetValue(AgentProfiles.Id(agent), out var handoff) == true ? handoff : null;
        var question = previous is null && inherited is null
            ? $"Start the primary session as {name}?\n\n"
              + $"It's {AgentProfiles.WithArticle(agent)} session of its own at the top of your session tree. It starts and directs child "
              + "sessions of any agent when you ask it to, and keeps an eye on all your sessions. "
              + $"It uses your {name} plan like any other session."
            : null;
        if (question is not null && !Confirm(question, "Primary session")) return;

        var tab = NewPrimaryTab(agent, previous, Guid.NewGuid());
        tab.HandoffId = inherited;
        if (previous is null) tab.InitialPrompt = inherited.HasValue ? HandoffPrompt() : PrimarySetup.FirstStartPrompt(agent, 0);
        _tabs.Insert(0, tab);
        _state.PrimaryAgent = AgentProfiles.Id(agent);
        RefreshTree();
        Select(tab);
        StartTab(tab);
        ShowWorkspace();
        Log.Write(previous is null ? $"primary: started as {name}" : $"primary: resuming {name} conversation {previous}");
    }

    private static HostedTab NewPrimaryTab(AgentKind agent, string? sessionId, Guid id, string? cwd = null) =>
        new(cwd ?? PrimarySetup.Folder, sessionId, id, "Primary", TabCategory.None, isPrimary: true, agent: agent)
        {
            LaunchName = agent == AgentKind.Claude ? PrimarySetup.SessionName : null,
        };

    /// <summary>The conversation an agent's primary last ran, if Agmen saved one.</summary>
    private string? SavedPrimarySession(AgentKind agent) =>
        _state.PrimarySessions is { } sessions && sessions.TryGetValue(AgentProfiles.Id(agent), out var id) ? id
        : agent == AgentKind.Claude ? _state.PrimarySessionId : null;

    /// <summary>Keep the primary's conversation under its agent, so that agent's primary can pick it up again.</summary>
    private void RememberPrimarySession(HostedTab primary)
    {
        var agent = AgentProfiles.Id(primary.Agent);
        if (primary.HandoffId is { } handoff) (_state.PrimaryHandoffs ??= new())[agent] = handoff;
        else _state.PrimaryHandoffs?.Remove(agent);
        if (primary.SessionId is { } id) (_state.PrimarySessions ??= new())[agent] = id;
        else _state.PrimarySessions?.Remove(agent);
        if (primary.Agent == AgentKind.Claude) _state.PrimarySessionId = primary.SessionId;
    }

    /// <summary>
    /// Whether an agent still has the conversation to resume. Antigravity keeps no record Agmen
    /// can check cheaply, so its primary is resumed and agy itself says if the conversation is gone.
    /// </summary>
    private static bool CanResume(AgentKind agent, string sessionId) => agent switch
    {
        AgentKind.Claude => RestoreService.HasTranscript(sessionId),
        AgentKind.Codex => AgentMonitor.FindCodexFile(sessionId) is not null,
        AgentKind.Kimi => AgentMonitor.KimiSessionDir(sessionId) is not null,
        _ => true,
    };

    /// <summary>The agents the primary can run as, for the ★ button and the primary's own menu; the current one is checked.</summary>
    private IEnumerable<MenuItem> PrimaryAgentItems()
    {
        foreach (var agent in AgentProfiles.All)
        {
            var a = agent;
            bool installed = AgentProfiles.IsInstalled(a);
            var item = new MenuItem
            {
                Header = installed ? AgentProfiles.DisplayName(a) : $"{AgentProfiles.DisplayName(a)} (not installed)",
                IsEnabled = installed,
                IsCheckable = true,
                IsChecked = a == PrimaryAgent,
            };
            item.Click += (_, _) => OpenPrimary(a);
            yield return item;
        }
    }

    /// <summary>The ★ button's menu: which agent runs the primary.</summary>
    private void ShowPrimaryMenu()
    {
        var menu = new ContextMenu { PlacementTarget = PrimaryButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Run the primary session as", IsEnabled = false });
        foreach (var item in PrimaryAgentItems()) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    // ----------------------------------------------------------- typed messages

    /// <summary>
    /// After each poll: tell parents about turns their messages started that are now over,
    /// then type whatever is queued and safe to type.
    /// </summary>
    private void PumpMessages()
    {
        foreach (var tab in _tabs.ToList())
            if (tab.TakeTurnReport() is { } report) ReportFinishedTurn(tab, report.Message, report.Outcome);
        foreach (var tab in _tabs.ToList())
            tab.TryDeliver();
    }

    /// <summary>
    /// Queue an "[Agmen]" note for the session that sent the message: the child finished the
    /// turn it started, with the start of its reply when the agent reports one, or the child
    /// ended. A child that already reported back with message_parent needs no extra note.
    /// </summary>
    private void ReportFinishedTurn(HostedTab tab, OutboxMessage sent, string outcome)
    {
        if (sent.ReportTo is not { } toId || tab.ParentId != toId || sent.CanDeliver?.Invoke() == false
            || _tabs.FirstOrDefault(t => t.Id == toId) is not { } to || to == tab || to.IsEnded) return;
        if (outcome == "idle" && tab.RepliedToParent && tab.ParentId == to.Id)
        {
            Log.Write($"tree: {tab.Title} already reported to {to.Title}");
            return;
        }
        var name = tab.Session?.Info.Name is { Length: > 0 } n ? n : tab.Title;
        var said = Orchestrator.WithoutPrefix(sent.Text).Replace('\n', ' ');
        if (said.Length > 70) said = said[..67] + "...";
        var state = Agents.StateOf(tab.Id);
        var reply = state?.StartedAt == tab.StartedAt && state.Agent == tab.Agent && state.LastReply is { Length: > 0 } r
            ? TranscriptDigest.Clip(r.Replace('\n', ' '), 400, keepEnd: false) : null;
        var text = outcome == "ended"
            ? $"[Agmen] {name} ended while working on your message (\"{said}\")."
            : $"[Agmen] {name} finished the turn your message started (\"{said}\")."
              + (reply is null ? " read_child shows its reply." : $" It replied: \"{reply}\" read_child shows more.");
        to.Enqueue(new OutboxMessage
        {
            Text = text,
            Origin = "agmen",
            Key = "report:" + tab.Id,
            CanDeliver = Agmen.Orchestration.Orchestrator.WatchParentRelationship(_tabs, to, tab),
            MinIdle = TimeSpan.FromSeconds(5),
        });
        Log.Write($"tree: told {to.Title} that {name} {(outcome == "ended" ? "ended" : "finished a turn")}");
    }
}
