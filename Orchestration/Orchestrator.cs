using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agmen.Agents;
using Agmen.Control;
using Agmen.Hosting;
using Agmen.Models;
using Agmen.Services;

namespace Agmen.Orchestration;

/// <summary>
/// Carries out the tool calls sessions make from their tabs, and Agmen's own requests from tool
/// servers and hook commands. Every request carries the caller's process chain, which leads to
/// the tab it comes from. A session directs only its own children and reports to its parent;
/// the primary also keeps a read-only overview of every session. Calls arrive on pipe threads;
/// anything that touches tabs runs on the UI thread.
/// </summary>
public sealed class Orchestrator
{
    private const int StartsPerHour = 12;
    private const int MaxQueued = 10;

    private static readonly HashSet<string> PrimaryOnly = Mcp.McpTools.PrimaryOnly;

    private static readonly Regex MessagePrefix = new(@"^\[From your (parent|child|sibling), [^\]]*\]\s*", RegexOptions.Compiled);

    private readonly WorkspaceWindow _ws;
    private readonly Dictionary<Guid, List<DateTime>> _starts = new();
    // Snapshot capture yields the UI thread. Reserve the name and slot until the child is attached.
    private readonly Dictionary<string, Guid> _pendingForks = new(StringComparer.OrdinalIgnoreCase);

    public Orchestrator(WorkspaceWindow ws) => _ws = ws;

    /// <summary>
    /// Carry out one request from the control pipe. Two are Agmen's own: agmen:role, which a tool
    /// server asks when it starts, and agmen:hook, a signal from an agent CLI. Every other
    /// request is an MCP tool call from a session in a tab.
    /// </summary>
    public async Task<JsonObject> HandleAsync(string tool, JsonObject args, IReadOnlyList<int> caller)
    {
        if (tool == "agmen:role") return await OnUi(() => Role(caller));
        if (tool == "agmen:hook") return await OnUi(() => Hook(args, caller));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string who = "?";
        try
        {
            var from = await OnUi(() => CallerTab(caller)) ?? throw new ToolError("This session doesn't run in an Agmen tab.");
            HostedTab ActiveCaller() => !from.IsEnded && CallerTab(caller) == from ? from
                : throw new ToolError("The calling session has ended or restarted.");
            who = from.Title;
            if (PrimaryOnly.Contains(tool) && !from.IsPrimary) throw new ToolError($"'{tool}' is only for the primary session.");
            string text = tool switch
            {
                "whoami" => await OnUi(() => WhoAmI(from)),
                "list_children" => await OnUi(() => ListChildren(from)),
                "spawn_child" => await OnUi(() => SpawnChildAsync(ActiveCaller(), args)).Unwrap(),
                "list_siblings" => await OnUi(() => ListSiblings(ActiveCaller(), args)),
                "message_sibling" => await OnUi(() => MessageSibling(ActiveCaller(), args)),
                "read_handoff" => await ReadHandoffAsync(await OnUi(() => ActiveCaller().HandoffId), args),
                "message_child" => await OnUi(() => MessageChild(from, args)),
                "read_child" => await ReadAsync(() => Target.Of(ResolveChild(from, Str(args, "child"))), args),
                "message_parent" => await OnUi(() => MessageParent(from, args)),
                "update_child" => await OnUi(() => UpdateChild(from, args)),
                "list_projects" => await Task.Run(() => ListProjects(args)),
                "list_sessions" => await OnUi(() => ListSessions(args)),
                "read_session" => await ReadAsync(() => Resolve(Str(args, "session")), args),
                "focus_session" => await OnUi(() => FocusSession(args)),
                "notify" => await OnUi(() => Notify(args)),
                _ => throw new ToolError($"Agmen has no tool called '{tool}'."),
            };
            Log.Write($"tools: {who} {tool} ({watch.ElapsedMilliseconds} ms)");
            return new JsonObject { ["ok"] = true, ["text"] = text };
        }
        catch (ToolError e)
        {
            Log.Write($"tools: {who} {tool} refused: {e.Message}");
            return new JsonObject { ["ok"] = false, ["text"] = e.Message };
        }
        catch (Exception e)
        {
            Log.Write($"tools: {who} {tool} failed: {e}");
            return new JsonObject { ["ok"] = false, ["text"] = $"Agmen couldn't do that: {e.Message}" };
        }
    }

    /// <summary>Run on the UI thread, passing any exception back to the caller instead of the dispatcher.</summary>
    private async Task<T> OnUi<T>(Func<T> work)
    {
        ExceptionDispatchInfo? error = null;
        var result = await _ws.Dispatcher.InvokeAsync(() =>
        {
            try { return work(); }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
                return default!;
            }
        });
        error?.Throw();
        return result;
    }

    internal sealed class ToolError(string message) : Exception(message);

    // ------------------------------------------------------------ who is calling

    /// <summary>The tab whose process is in the caller's chain: the tool server or hook runs below the agent it serves.</summary>
    private HostedTab? CallerTab(IReadOnlyList<int> caller)
    {
        _ws.ActivateStartingCaller(caller);
        foreach (var pid in caller)
            if (_ws.Tabs.FirstOrDefault(t => t.CurrentPid == pid && pid > 0) is { } tab) return tab;
        return null;
    }

    private JsonObject Role(IReadOnlyList<int> caller)
    {
        var tab = CallerTab(caller);
        tab?.NoteToolServer();
        var role = tab is null ? "none" : tab.IsPrimary ? "primary" : "tab";
        Log.Write($"tool server: {role}{(tab is null ? "" : $" for {tab.Title} ({AgentProfiles.DisplayName(tab.Agent)})")}");
        return new JsonObject { ["ok"] = true, ["role"] = role, ["text"] = role };
    }

    /// <summary>A signal from an agent CLI, such as a finished turn, passed to the agent monitor for its tab.</summary>
    private JsonObject Hook(JsonObject args, IReadOnlyList<int> caller)
    {
        var tab = CallerTab(caller);
        var evt = Str(args, "event") ?? "";
        if (tab is null)
        {
            Log.Write($"hook: {Str(args, "agent")} {evt} from no Agmen tab");
            return new JsonObject { ["ok"] = false, ["text"] = "No Agmen tab runs this session." };
        }
        var previousId = tab.SessionId;
        _ws.Agents.OnHook(tab.AgentRef(), evt, args["payload"] as JsonObject);
        tab.BindConfirmedSession(_ws.Agents.StateOf(tab.Id));
        if (tab.SessionId != previousId) _ws.RequestSave();
        return new JsonObject { ["ok"] = true, ["text"] = "noted" };
    }

    // ------------------------------------------------------------ the tree

    private string WhoAmI(HostedTab tab)
    {
        var sb = new StringBuilder();
        sb.Append($"You are {NameOf(tab)}, {AgentProfiles.WithArticle(tab.Agent)} session in an Agmen tab, working in {tab.Cwd}. ");
        if (tab.IsPrimary) sb.Append("You are the primary session, at the top of the user's session tree. ");
        if (tab.HandoffId.HasValue) sb.Append("Your inherited conversation is available through read_handoff. ");
        sb.AppendLine(_ws.ParentOf(tab) is { } parent
            ? $"Your parent is {NameOf(parent)}, {AgentProfiles.WithArticle(parent.Agent)} session; report to it with message_parent."
            : "You have no parent session: you work for the user directly.");
        var children = _ws.ChildrenOf(tab);
        sb.Append(children.Count == 0
            ? "You have no child sessions. spawn_child starts one."
            : $"Your children: {string.Join(", ", children.Select(c => $"{NameOf(c)} ({AgentProfiles.DisplayName(c.Agent)}, {StatusOf(Target.Of(c))})"))}.");
        return sb.ToString();
    }

    private string ListChildren(HostedTab parent)
    {
        var children = _ws.ChildrenOf(parent);
        if (children.Count == 0) return "You have no child sessions. spawn_child starts one.";
        var lines = children.Select(c => Describe(Target.Of(c)).ToJsonString(ControlChannel.Json));
        return $"{children.Count} child session{(children.Count == 1 ? "" : "s")}. Message one with message_child, "
               + $"and read what it has done with read_child.\n[\n{string.Join(",\n", lines)}\n]";
    }

    internal static IEnumerable<HostedTab> SiblingsOf(IReadOnlyList<HostedTab> tabs, HostedTab sender)
    {
        if (!tabs.Contains(sender) || sender.ParentId is not { } parentId
            || !tabs.Any(t => t.Id == parentId && t.ParentId is null))
            return [];
        return tabs.Where(t => t != sender && t.ParentId == parentId);
    }

    internal static Func<bool> WatchSiblingRelationship(IReadOnlyList<HostedTab> tabs, HostedTab sender, HostedTab recipient)
    {
        var parent = tabs.FirstOrDefault(t => t.Id == sender.ParentId && t.ParentId is null);
        if (parent is null || sender == recipient || recipient.ParentId != parent.Id) return () => false;
        var parentState = (parent.StartedAt, parent.ParentVersion);
        var senderState = (sender.StartedAt, sender.ParentVersion);
        var recipientState = (recipient.StartedAt, recipient.ParentVersion);
        return () => !sender.IsEnded && !recipient.IsEnded && tabs.Contains(parent) && tabs.Contains(sender) && tabs.Contains(recipient)
            && (parent.StartedAt, parent.ParentVersion) == parentState && (sender.StartedAt, sender.ParentVersion) == senderState
            && (recipient.StartedAt, recipient.ParentVersion) == recipientState;
    }

    internal static Func<bool> WatchParentRelationship(IReadOnlyList<HostedTab> tabs, HostedTab parent, HostedTab child)
    {
        var version = child.ParentVersion;
        return () => tabs.Contains(parent) && tabs.Contains(child) && child.ParentId == parent.Id && child.ParentVersion == version;
    }

    private string ListSiblings(HostedTab from, JsonObject args)
    {
        if (args.Count != 0) throw new ToolError("list_siblings takes no arguments.");
        var peers = SiblingsOf(_ws.Tabs, from).Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id.ToString(),
            ["name"] = NameOf(t),
            ["agent"] = AgentProfiles.Id(t.Agent),
            ["status"] = StatusOf(Target.Of(t)),
        }).ToArray();
        return new JsonArray(peers).ToJsonString(ControlChannel.Json);
    }

    internal static (HostedTab Recipient, OutboxMessage Message) PrepareSiblingMessage(
        IReadOnlyList<HostedTab> tabs, HostedTab sender, string? query, string? text)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ToolError("Choose one sibling by tab ID or full name from list_siblings.");
        if (string.IsNullOrWhiteSpace(text))
            throw new ToolError("Provide the message or selected excerpt to share.");
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
            throw new ToolError("Share plain text without terminal control characters.");

        query = query.Trim();
        var siblings = SiblingsOf(tabs, sender).ToList();
        var recipient = siblings.FirstOrDefault(t => Same(t.Id.ToString(), query));
        if (recipient is null)
        {
            var matches = siblings.Where(t => Same(NameOf(t), query)).ToList();
            if (matches.Count != 1) throw new ToolError("Choose a unique sibling from list_siblings using its tab ID.");
            recipient = matches[0];
        }
        if (sender.IsEnded || recipient.IsEnded) throw new ToolError("Both sibling sessions must be open and active.");

        var label = new string(NameOf(sender).Where(c => !char.IsControl(c) && c != ']').ToArray());
        return (recipient, new OutboxMessage
        {
            Text = $"[From your sibling, {label}] " + text.Replace("\r\n", "\n").Replace('\r', '\n'),
            Origin = "sibling",
            CanDeliver = WatchSiblingRelationship(tabs, sender, recipient),
        });
    }

    private string MessageSibling(HostedTab sender, JsonObject args)
    {
        if (args.Any(p => p.Key is not ("sibling" or "message")))
            throw new ToolError("message_sibling takes only sibling and message.");
        static string? Input(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        return QueueSiblingMessage(sender, Input(args["sibling"]), Input(args["message"]));
    }

    internal string QueueSiblingMessage(HostedTab sender, string? address, string? text)
    {
        var (recipient, message) = PrepareSiblingMessage(_ws.Tabs, sender, address, text);
        if (recipient.QueuedCount >= MaxQueued) throw new ToolError($"The queue for {NameOf(recipient)} is full.");
        recipient.Enqueue(message);
        recipient.TryDeliver();
        return recipient.HasQueued ? $"Queued for {NameOf(recipient)}. {recipient.HoldReason}."
            : $"Sent to {NameOf(recipient)}.";
    }

    private static Task<string> ReadHandoffAsync(Guid? handoffId, JsonObject args)
    {
        if (args.Any(p => p.Key is not ("offset" or "count"))) throw new ToolError("read_handoff takes only offset and count.");
        int PageArgument(string name, int fallback, int min, int max)
        {
            if (!args.ContainsKey(name)) return fallback;
            if (args[name] is JsonValue v && v.TryGetValue<int>(out var number) && number >= min && number <= max)
                return number;
            throw new ToolError($"{name} must be an integer between {min} and {max}.");
        }
        int offset = PageArgument("offset", 0, 0, int.MaxValue);
        int count = PageArgument("count", 8192, 1, 16384);
        return Task.Run(() => ConversationHistory.Read(handoffId, offset, count));
    }

    private async Task<string> SpawnChildAsync(HostedTab parent, JsonObject args)
    {
        var resume = Str(args, "resume_session_id")?.Trim();
        if (string.IsNullOrEmpty(resume)) resume = null;
        bool inherit = args.ContainsKey("inherit_context")
            ? Bool(args, "inherit_context") ?? throw new ToolError("inherit_context must be true or false.")
            : false;
        if (inherit && resume is not null)
            throw new ToolError("A contextual fork starts a new conversation. Choose either inherit_context or resume_session_id.");
        TabCategory? color = args.ContainsKey("color")
            ? ParseColor(Str(args, "color") ?? throw new ToolError("Give a color code for the child's card."))
            : null;
        var agent = Str(args, "agent") is { Length: > 0 } agentText
            ? AgentProfiles.Parse(agentText) ?? throw new ToolError($"'{agentText}' isn't an agent Agmen runs. Use claude, codex, kimi or antigravity.")
            : parent.Agent;
        if (!AgentProfiles.IsInstalled(agent))
            throw new ToolError($"{AgentProfiles.DisplayName(agent)} isn't installed on this computer.");
        // Kimi and Antigravity read Agmen's tools from their global config. Unconnected, a child of
        // theirs couldn't report back, and Antigravity's tab couldn't even follow its turns.
        if (AgentConnect.Connectable.Contains(agent) && AgentConnect.StateOf(agent) != AgentConnect.State.Connected)
        {
            _ = _ws.Dispatcher.BeginInvoke(() => _ws.OfferConnect(agent));
            throw new ToolError($"{AgentProfiles.DisplayName(agent)} isn't connected to Agmen yet, so a child of that agent couldn't use "
                                + "Agmen's tools or report to you. Agmen has asked the user to connect it; they can also use Connect "
                                + "agents… in Agmen's + menu. Tell the user, and start the child again once they have.");
        }
        if (_ws.DepthOf(parent) >= WorkspaceWindow.MaxDepth)
            throw new ToolError($"The session tree may go only {WorkspaceWindow.MaxDepth} levels deep, so you can't start children. Do the work yourself, or tell your parent.");
        int pending = _pendingForks.Values.Count(id => id == parent.Id);
        if (_ws.ChildrenOf(parent).Count(c => !c.IsEnded) + pending >= WorkspaceWindow.MaxChildren)
            throw new ToolError($"You already have {WorkspaceWindow.MaxChildren} running children. Wait for one to finish, or ask the user to close one.");
        var starts = _starts.TryGetValue(parent.Id, out var list) ? list : _starts[parent.Id] = new List<DateTime>();
        starts.RemoveAll(at => DateTime.UtcNow - at > TimeSpan.FromHours(1));
        if (starts.Count + pending >= StartsPerHour)
            throw new ToolError($"You've started {StartsPerHour} children in the last hour. Check with the user before starting more.");

        var asked = Str(args, "name")?.Trim();
        if (!string.IsNullOrEmpty(asked) && !Regex.IsMatch(asked, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,47}$"))
            throw new ToolError("A session name may use letters, digits, dots, dashes and underscores, up to 48 characters.");
        // A name already in use gets the next free variant rather than costing the parent another call.
        var reserved = _pendingForks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = string.IsNullOrEmpty(asked) ? _ws.UniqueName(agent, reserved) : _ws.FreeName(asked, reserved);
        var renamed = string.IsNullOrEmpty(asked) || name == asked ? "" : $" Another session was already called {asked}, so this one is {name}.";

        // A model and effort of the child's own; its permissions are always the user's setting.
        var model = Str(args, "model")?.Trim();
        if (string.IsNullOrEmpty(model)) model = null;
        else if (!AgentSettings.IsValidModel(model))
            throw new ToolError("A model name may use letters, digits and . _ : / @ + - [ ], such as opus or kimi-code/k3.1-pro.");
        var effort = Str(args, "effort")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(effort)) effort = null;
        else if (!AgentSettings.Efforts(agent).Contains(effort))
            throw new ToolError($"{AgentProfiles.DisplayName(agent)} takes these efforts: {string.Join(", ", AgentSettings.Efforts(agent))}.");

        var folder = FolderOf(Str(args, "folder"));
        if (resume is not null)
        {
            if (_ws.Tabs.Any(t => t.Agent == agent && Same(t.SessionId, resume)))
                throw new ToolError("That conversation is already open in an Agmen tab.");
            if (agent == AgentKind.Claude)
            {
                var entry = RestoreService.FindSession(resume) ?? throw new ToolError($"No saved Claude Code conversation has the id {resume}.");
                if (SessionMonitor.LiveSessionIds().Contains(entry.SessionId))
                    throw new ToolError("That conversation is running outside Agmen right now. Ask the user to close it there first.");
                folder ??= entry.Cwd;
            }
            folder ??= agent switch
            {
                AgentKind.Codex => AgentMonitor.FindCodexFile(resume) is { } file ? FolderOf(CodexFolder(file)) : null,
                AgentKind.Kimi => FolderOf(AgentMonitor.KimiSessionDir(resume)?.WorkDir),
                _ => null,
            } ?? throw new ToolError($"Agmen can't find where {AgentProfiles.DisplayName(agent)} session {resume} ran. Give its folder too.");
        }
        folder ??= parent.Cwd;

        // The schema calls it message. Models also send the first task as task or prompt, and a
        // child started without it sits idle while its parent waits for a report.
        var message = (Str(args, "message") ?? Str(args, "task") ?? Str(args, "prompt"))?.Trim();
        var parentName = NameOf(parent);
        var first = string.IsNullOrEmpty(message)
            ? null
            : $"{FromParent(parentName)}{message}\n\n{AgentGuide.ChildFooter(name, parentName)}";
        HostedTab tab;
        if (inherit)
        {
            _pendingForks.Add(name, parent.Id);
            try { tab = await _ws.ForkWithContextAsync(parent, agent, folder, name, first, model: model, effort: effort, requireRecordedTurn: true); }
            finally { _pendingForks.Remove(name); }
        }
        else tab = _ws.OpenChild(parent, agent, folder, name, first, resume, model: model, effort: effort);
        starts.Add(DateTime.UtcNow);
        if (color is { } category) _ws.SetCategory(tab, category);

        var trust = agent != AgentKind.Claude || (first is null && tab.HandoffId is null) || ClaudeConfig.IsTrusted(folder) ? ""
            : ClaudeConfig.IsHome(folder)
                ? " Claude asks the user to trust the home folder every time it starts there, so your task waits until they answer. A project folder avoids that."
                : " Claude hasn't been used in this folder before, so it first asks the user to trust the folder; your task waits until then.";
        var settings = AgentSettings.Checked(agent, AgentSettings.For(agent));
        var runsOn = (model ?? settings.Model, effort ?? settings.Effort) switch
        {
            ({ } m, { } e) => $", on {m} at {e} effort",
            ({ } m, null) => $", on {m}",
            (null, { } e) => $", at {e} effort",
            _ => "",
        };
        string history = inherit ? (tab.HandoffId.HasValue ? ", with a snapshot of your recorded conversation"
            : ", with no recorded conversation to inherit") : "";
        return $"Started {name}, {AgentProfiles.WithArticle(agent)} child session, in {folder}{runsOn}"
               + (resume is null ? "" : $", resuming conversation {resume}")
               + history
               + (first is null
                   ? ". You gave it no first task. Send the task with message_child. "
                   : ", with your task. Agmen tells you when it finishes the turn your task starts. ")
               + "It takes a few seconds to start; list_children shows its status." + renamed + trust;
    }

    private string MessageChild(HostedTab parent, JsonObject args)
    {
        var child = ResolveChild(parent, Str(args, "child"));
        var message = Str(args, "message")?.Trim();
        if (string.IsNullOrEmpty(message)) throw new ToolError("The message is empty.");
        if (child.QueuedCount >= MaxQueued)
            throw new ToolError($"{MaxQueued} messages are already waiting for {NameOf(child)}: {child.HoldReason}.");
        bool report = Bool(args, "report_back") ?? true;
        child.Enqueue(new OutboxMessage { Text = FromParent(NameOf(parent)) + message, Origin = "parent",
            ReportTo = report ? parent.Id : null, CanDeliver = WatchParentRelationship(_ws.Tabs, parent, child) });
        child.RepliedToParent = false;
        // A child whose session ended starts again, resuming its conversation, and takes the message when ready.
        bool restarted = false;
        if (child.IsEnded)
        {
            _ws.ResumeTab(child);
            restarted = true;
        }
        bool delivered = !restarted && child.TryDeliver();
        var tail = report ? " Agmen will tell you when it finishes the turn your message starts." : "";
        return delivered ? $"Sent to {NameOf(child)}.{tail}"
            : restarted ? $"{NameOf(child)}'s session had ended, so Agmen is starting it again; the message waits until it is ready.{tail}"
            : $"Queued for {NameOf(child)}: {child.HoldReason}.{tail}";
    }

    private string MessageParent(HostedTab child, JsonObject args)
    {
        var parent = _ws.ParentOf(child)
                     ?? throw new ToolError("You have no parent session: you work for the user directly, so tell them in your reply.");
        var message = Str(args, "message")?.Trim();
        if (string.IsNullOrEmpty(message)) throw new ToolError("The message is empty.");
        if (parent.IsEnded) throw new ToolError($"Your parent, {NameOf(parent)}, has ended. Tell the user in your reply instead.");
        if (parent.QueuedCount >= MaxQueued)
            throw new ToolError($"{MaxQueued} messages are already waiting for {NameOf(parent)}: {parent.HoldReason}.");
        parent.Enqueue(new OutboxMessage { Text = $"[From your child, {NameOf(child)}] {message}", Origin = "child",
            CanDeliver = WatchParentRelationship(_ws.Tabs, parent, child) });
        child.RepliedToParent = true;
        return parent.TryDeliver() ? $"Sent to {NameOf(parent)}." : $"Queued for {NameOf(parent)}: {parent.HoldReason}.";
    }

    private string UpdateChild(HostedTab parent, JsonObject args)
    {
        var child = ResolveChild(parent, Str(args, "child"));
        var done = new List<string>();
        if (args.ContainsKey("color"))
        {
            var c = ParseColor(Str(args, "color"));
            _ws.SetCategory(child, c);
            done.Add($"color {ColorName(c)}");
        }
        if (args.ContainsKey("summary"))
        {
            var s = Str(args, "summary")?.Trim() ?? "";
            _ws.SetNote(child, s.Length == 0 ? null : TranscriptDigest.Clip(s, 160, keepEnd: false));
            done.Add(s.Length == 0 ? "summary cleared" : "summary");
        }
        if (done.Count == 0) throw new ToolError("Nothing to change. Pass color or summary.");
        return $"Updated {NameOf(child)}: {string.Join(", ", done)}.";
    }

    /// <summary>One of the caller's own children, by name, id, or agent when that is unambiguous.</summary>
    private HostedTab ResolveChild(HostedTab parent, string? query)
    {
        var children = _ws.ChildrenOf(parent);
        if (children.Count == 0) throw new ToolError("You have no child sessions. spawn_child starts one.");
        var q = query?.Trim() ?? "";
        if (q.Length == 0)
        {
            if (children.Count == 1) return children[0];
            throw new ToolError($"Say which child: {string.Join(", ", children.Select(NameOf))}.");
        }
        var key = q.StartsWith("tab-", StringComparison.OrdinalIgnoreCase) ? q[4..] : q;
        IEnumerable<HostedTab>[] tries =
        {
            children.Where(c => NameOf(c).Equals(q, StringComparison.OrdinalIgnoreCase) || c.Title.Equals(q, StringComparison.OrdinalIgnoreCase)),
            key.Length >= 6
                ? children.Where(c => IdMatches(c.SessionId, key) || c.Id.ToString().StartsWith(key, StringComparison.OrdinalIgnoreCase))
                : Enumerable.Empty<HostedTab>(),
            children.Where(c => NameOf(c).Contains(q, StringComparison.OrdinalIgnoreCase)),
            AgentProfiles.Parse(q) is { } agent ? children.Where(c => c.Agent == agent) : Enumerable.Empty<HostedTab>(),
        };
        foreach (var hits in tries.Select(t => t.Distinct().ToList()))
        {
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) throw new ToolError($"'{q}' matches {string.Join(", ", hits.Select(NameOf))}. Use the full name.");
        }
        throw new ToolError($"You have no child called '{q}'. Your children: {string.Join(", ", children.Select(NameOf))}.");
    }

    /// <summary>A session id without Kimi's "session_" prefix, so its first characters tell sessions apart.</summary>
    private static string? IdCore(string? sessionId) =>
        sessionId is not null && sessionId.StartsWith(KimiIdPrefix, StringComparison.Ordinal) ? sessionId[KimiIdPrefix.Length..] : sessionId;

    private const string KimiIdPrefix = "session_";

    /// <summary>True when a session id starts with what the caller typed, with or without Kimi's prefix.</summary>
    private static bool IdMatches(string? sessionId, string key) =>
        (sessionId?.StartsWith(key, StringComparison.OrdinalIgnoreCase) ?? false)
        || (IdCore(sessionId)?.StartsWith(key, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>A session's name as others see it: its registered name, or its tab's title.</summary>
    private static string NameOf(HostedTab tab) =>
        tab.IsPrimary ? "primary" : tab.Session?.Info.Name is { Length: > 0 } n ? n : tab.Title;

    private static string FromParent(string parentName) => $"[From your parent, {parentName}] ";

    /// <summary>A typed message without its "[From your …]" label, for notes that quote it.</summary>
    public static string WithoutPrefix(string text) => MessagePrefix.Replace(text, "");

    // ------------------------------------------------------------ the primary's overview

    private sealed record Target(HostedTab? Tab, SessionInfo? Info)
    {
        public static Target Of(HostedTab tab) => new(tab, null);
        public string? SessionId => Tab is not null ? Tab.SessionId : string.IsNullOrEmpty(Info?.SessionId) ? null : Info.SessionId;
        /// <summary>The registered session name; the tab title before it registers.</summary>
        public string Name => Tab is not null ? NameOf(Tab)
            : !string.IsNullOrEmpty(Info?.Name) ? Info.Name : string.IsNullOrEmpty(Info?.FolderLeaf) ? "?" : Info.FolderLeaf;
        public string Cwd => Tab?.Cwd ?? Info?.Cwd ?? "";
        public int Pid => Tab?.Pid ?? Info?.Pid ?? 0;
        /// <summary>Short id for addressing: the session id's first 8 characters, or the tab's before it registers.</summary>
        public string Id => IdCore(SessionId) is { Length: >= 8 } s ? s[..8] : "tab-" + Tab!.Id.ToString()[..8];
        public SessionInfo? Live => Info ?? Tab?.Session?.Info;
    }

    private List<Target> AllTargets()
    {
        var snapshot = _ws.LastSnapshot;
        var list = new List<Target>();
        foreach (var tab in _ws.Tabs)
            list.Add(new Target(tab, tab.Agent == AgentKind.Claude && tab.Pid > 0 ? snapshot.FirstOrDefault(s => s.Pid == tab.Pid) : null));
        foreach (var s in snapshot)
            if (!_ws.IsHostedPid(s.Pid) && !string.IsNullOrEmpty(s.SessionId))
                list.Add(new Target(null, s));
        return list;
    }

    private Target Resolve(string? query)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) throw new ToolError("Say which session: its name or id from list_sessions.");
        var all = AllTargets();
        var key = q.StartsWith("tab-", StringComparison.OrdinalIgnoreCase) ? q[4..] : q;

        var hits = new List<Target>();
        if (key.Length >= 6)
            hits = all.Where(t => IdMatches(t.SessionId, key)
                                  || (t.Tab?.Id.ToString().StartsWith(key, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (hits.Count == 0 && int.TryParse(q, out var pid) && pid > 0) hits = all.Where(t => t.Pid == pid).ToList();
        if (hits.Count == 0) hits = all.Where(t => t.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = all.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0)
            hits = all.Where(t => Path.GetFileName(t.Cwd.TrimEnd('\\', '/')).Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();

        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0) throw new ToolError($"No session matches '{query}'. Call list_sessions for names and ids.");
        var running = hits.Where(h => h.Tab is null || h.Tab.IsRunning).ToList();
        if (running.Count == 1) return running[0];
        throw new ToolError($"'{query}' matches {hits.Count} sessions: "
                            + string.Join(", ", hits.Take(6).Select(h => $"{h.Name} (id {h.Id})")) + ". Use the id.");
    }

    private static string StatusOf(Target t)
    {
        if (t.Tab is { } tab)
            return tab.State switch
            {
                TabState.Pending or TabState.Starting => "starting",
                TabState.Ended => "ended",
                TabState.Failed => "failed: " + (tab.StateNote ?? "unknown"),
                _ => Friendly(tab.Session?.StatusText ?? "running"),
            };
        return Friendly(t.Info?.EffectiveStatus ?? "unknown");
    }

    private static string Friendly(string status) => status switch
    {
        "waiting" => "waiting for the user",
        "shell" => "idle, with a background command running",
        _ => status,
    };

    private JsonObject Describe(Target t)
    {
        var o = new JsonObject
        {
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["agent"] = AgentProfiles.Id(t.Tab?.Agent ?? AgentKind.Claude),
            ["where"] = t.Tab is null
                ? t.Info?.Entrypoint == "claude-desktop" ? "Claude desktop app (read only)" : "outside Agmen (read only)"
                : t.Tab.IsPrimary ? "Agmen, the primary" : "Agmen tab",
            ["status"] = StatusOf(t),
            ["folder"] = t.Cwd,
        };
        if (t.Tab is { } tab)
        {
            if (_ws.ParentOf(tab) is { } parent) o["parent"] = NameOf(parent);
            var children = _ws.ChildrenOf(tab);
            if (children.Count > 0) o["children"] = new JsonArray(children.Select(c => (JsonNode)NameOf(c)).ToArray());
        }
        var info = t.Live;
        if (!string.IsNullOrEmpty(info?.GitBranch)) o["branch"] = info.GitBranch;
        if (t.Tab is { HasCategory: true } colored) o["color"] = ColorName(colored.Category);
        if (t.Tab is { HasNote: true } noted) o["summary"] = noted.Note;
        if (info?.LastActivity is { } last) o["last_activity"] = When(last.LocalDateTime);
        if (info is { ContextTokens: > 0 })
        {
            long limit = info.ContextWindow > 0 ? info.ContextWindow
                : info.Model?.Contains("[1m]", StringComparison.OrdinalIgnoreCase) == true ? 1_000_000 : 200_000;
            o["context_used"] = $"{Math.Min(100, info.ContextTokens * 100 / limit)}%";
        }
        if (!string.IsNullOrEmpty(info?.Model)) o["model"] = info.Model;
        if (t.Tab is { HasQueued: true } queued)
        {
            o["queued_messages"] = queued.QueuedCount;
            o["queue_note"] = queued.HoldReason;
        }
        if (t.SessionId is { } sid) o["session_id"] = sid;
        return o;
    }

    private string ListSessions(JsonObject args)
    {
        bool includeEnded = Bool(args, "include_ended") ?? true;
        var targets = AllTargets().Where(t => includeEnded || t.Tab is not { IsEnded: true }).ToList();
        int hosted = targets.Count(t => t.Tab is { IsPrimary: false });
        var lines = targets.Select(t => Describe(t).ToJsonString(ControlChannel.Json));
        return $"{targets.Count} sessions, {hosted} of them in Agmen tabs, listed in tree order with each one's parent and children. "
               + "You direct only your own children, with message_child. "
               + $"It is {DateTime.Now:ddd HH:mm} local time.\n[\n" + string.Join(",\n", lines) + "\n]";
    }

    /// <summary>The latest of a session's conversation, read from its own records without interrupting it.</summary>
    private async Task<string> ReadAsync(Func<Target> find, JsonObject args)
    {
        int count = Math.Clamp(Int(args, "messages") ?? 6, 1, 20);
        var context = await OnUi(() =>
        {
            var t = find();
            var agent = t.Tab?.Agent ?? AgentKind.Claude;
            var header = Describe(t).ToJsonString(ControlChannel.Json);
            string? path;
            if (t.Tab is { Agent: not AgentKind.Claude } tab)
            {
                var state = _ws.Agents.StateOf(tab.Id);
                path = state?.StartedAt == tab.StartedAt && state.Agent == tab.Agent
                    ? state.Transcript ?? tab.ConfirmedTranscript : tab.ConfirmedTranscript;
            }
            else path = t.SessionId is { } id ? RestoreService.FindTranscriptPath(id, t.Cwd) : null;
            return (Header: header, Path: path, t.Cwd, Agent: agent, Waiting: StatusOf(t).StartsWith("waiting", StringComparison.Ordinal));
        });
        if (context.Path is null || !File.Exists(context.Path))
            return context.Header + "\nNo transcript yet: the session hasn't been sent a message.";

        var sb = new StringBuilder();
        sb.AppendLine(context.Header);
        List<TranscriptDigest.Message> messages;
        if (context.Agent != AgentKind.Claude)
        {
            var other = await Task.Run(() => AgentTranscripts.Read(context.Agent, context.Path, count));
            if (context.Waiting && other.Waiting is { } asking) sb.AppendLine($"Waiting for the user to approve: {asking}");
            messages = other.Messages;
        }
        else
        {
            var digest = await Task.Run(() => TranscriptDigest.Read(context.Path, context.Cwd, count));
            if (context.Waiting && digest.PendingTool is { } pending) sb.AppendLine($"Waiting for the user to approve: {pending}");
            if (digest.Todos.Count > 0)
            {
                sb.AppendLine("Todo list:");
                foreach (var todo in digest.Todos.Take(25))
                    sb.AppendLine($"  [{todo.Status switch { "completed" => "x", "in_progress" => ">", _ => " " }}] {todo.Text}");
            }
            if (digest.Files.Count > 0) sb.AppendLine("Files changed recently: " + string.Join(", ", digest.Files));
            if (digest.Commands.Count > 0)
            {
                sb.AppendLine("Recent commands:");
                foreach (var c in digest.Commands) sb.AppendLine("  " + c);
            }
            messages = digest.Messages;
        }
        sb.AppendLine("Recent conversation, oldest first:");
        foreach (var m in messages)
        {
            var text = TranscriptDigest.Clip(m.Text, m.Role == "user" ? 700 : 1600, keepEnd: m.Role != "user");
            sb.AppendLine($"[{(m.At is { } at ? at.LocalDateTime.ToString("HH:mm") : "--:--")}] {m.Role}: {text}");
        }
        return sb.ToString().TrimEnd();
    }

    private string FocusSession(JsonObject args)
    {
        var t = Resolve(Str(args, "session"));
        if (t.Tab is not null)
        {
            _ws.FocusTab(t.Tab);
            return $"Showing {t.Name} in the workspace.";
        }
        if (t.Pid > 0 && WindowFocusService.FocusSession(t.Pid)) return $"Brought {t.Name}'s window to the front.";
        throw new ToolError($"Couldn't find a window for {t.Name}.");
    }

    private string Notify(JsonObject args)
    {
        var message = Str(args, "message")?.Trim();
        if (string.IsNullOrEmpty(message)) throw new ToolError("The message is empty.");
        HostedTab? tab = Str(args, "session") is { Length: > 0 } s ? Resolve(s).Tab : null;
        if (!_ws.Notifications)
            return "The user has turned Agmen notifications off, so none was shown. Say it in your reply instead.";
        _ws.RaiseNotify(TranscriptDigest.Clip(message, 250, keepEnd: false), tab);
        return "Notification shown.";
    }

    private static string ListProjects(JsonObject args)
    {
        var filter = Str(args, "filter")?.Trim();
        int limit = Math.Clamp(Int(args, "limit") ?? 30, 1, 100);
        var hits = ProjectIndex.Scan()
            .Where(p => string.IsNullOrEmpty(filter)
                        || p.Folder.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || (p.Repository?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || p.OtherRemotes.Any(r => r.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();
        var lines = hits.Select(p =>
        {
            var o = new JsonObject { ["folder"] = p.Folder };
            if (p.Repository is not null) o["repository"] = p.Repository;
            if (p.Remote is not null) o["remote"] = p.Remote;
            if (p.OtherRemotes.Count > 0) o["other_remotes"] = new JsonArray(p.OtherRemotes.Select(r => (JsonNode)r).ToArray());
            if (p.Branch is not null) o["branch"] = p.Branch;
            o["last_used"] = When(p.LastUsed);
            return o.ToJsonString(ControlChannel.Json);
        });
        return $"{hits.Count} folders{(string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'")}, most recent first:\n[\n"
               + string.Join(",\n", lines) + "\n]";
    }

    // ------------------------------------------------------------ helpers

    private static string? CodexFolder(string sessionFile)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(sessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var cwd = JsonNode.Parse(reader.ReadLine() ?? "")?["payload"]?["cwd"];
            return cwd is JsonValue v && v.TryGetValue<string>(out var folder) ? folder : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A full path to an existing folder, or null when none was given; a bad or missing folder throws.</summary>
    private static string? FolderOf(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'))); }
        catch (Exception) { throw new ToolError($"'{folder}' isn't a valid folder path."); }
        if (!Directory.Exists(full)) throw new ToolError($"The folder {full} doesn't exist. list_projects shows local project folders.");
        return full;
    }

    private static TabCategory ParseColor(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "bug" or "red" => TabCategory.Bug,
        "planning" or "blue" => TabCategory.Planning,
        "execution" or "yellow" => TabCategory.Execution,
        "ready" or "green" => TabCategory.Ready,
        null or "" or "none" or "clear" => TabCategory.None,
        _ => throw new ToolError($"'{text}' isn't a color code. Use bug, planning, execution, ready, or none."),
    };

    private static string ColorName(TabCategory category) => category switch
    {
        TabCategory.Bug => "bug",
        TabCategory.Planning => "planning",
        TabCategory.Execution => "execution",
        TabCategory.Ready => "ready",
        _ => "none",
    };

    /// <summary>"14:05 (12 min ago)": a clock time the model can quote, and how long ago it was.</summary>
    public static string When(DateTime local)
    {
        var age = DateTime.Now - local;
        string ago = age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} days ago";
        var clock = age.TotalDays < 1 ? local.ToString("HH:mm") : local.ToString("ddd d MMM HH:mm");
        return $"{clock} ({ago})";
    }

    /// <summary>Models sometimes send numbers and booleans as strings, so read arguments leniently.</summary>
    private static string? Str(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        return null;
    }

    private static int? Int(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        if (v.TryGetValue<string>(out var s) && Regex.Match(s, @"-?\d+") is { Success: true } digits
            && int.TryParse(digits.Value, out var parsed)) return parsed;
        return null;
    }

    private static bool? Bool(JsonObject args, string name) => args[name] is JsonValue v
        ? v.TryGetValue<bool>(out var b) ? b
        : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) ? parsed : null
        : null;

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
