using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agmen.Services;

namespace Agmen.Agents;

/// <summary>
/// The one-time Connect step for agents that read Agmen's settings only from their global config:
/// Kimi Code needs the agmen tool server in ~/.kimi-code/mcp.json, and Antigravity needs it in
/// ~/.gemini/config/mcp_config.json plus its status hooks in ~/.gemini/config/hooks.json. Both
/// also get a permission that lets Agmen's own tools run without asking (a rule in Kimi's
/// config.toml, a grant in Antigravity's settings.json), and an agmen agent carrying
/// <see cref="AgentGuide"/>, which Agmen starts their sessions with, since neither takes
/// instructions on its command line. Connect also gives their MCP config the servers every session
/// shares (<see cref="SharedServers"/>), and Agmen keeps those in step before each session starts.
/// The user runs Connect from the
/// Connect agents window, which shows <see cref="Preview"/> first. <see cref="Disconnect"/>
/// removes exactly what Connect added. Before Agmen first changes a file, it keeps a copy beside
/// it as *.agmen-backup.
/// </summary>
public static class AgentConnect
{
    public enum State { NotInstalled, NotConnected, Connected, ConnectedElsewhere, Unreadable }

    public static readonly AgentKind[] Connectable = { AgentKind.Kimi, AgentKind.Antigravity };

    private const string Key = "agmen";
    private static readonly string[] AntigravityEvents = { "SessionStart", "PreInvocation", "PostInvocation", "Stop" };
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string McpFile(AgentKind agent) => agent == AgentKind.Kimi
        ? Path.Combine(Home, ".kimi-code", "mcp.json")
        : Path.Combine(Home, ".gemini", "config", "mcp_config.json");

    private static string AntigravityHooksFile => Path.Combine(Home, ".gemini", "config", "hooks.json");
    private static string AntigravitySettingsFile => Path.Combine(Home, ".gemini", "antigravity-cli", "settings.json");
    private static string KimiConfigFile => Path.Combine(Home, ".kimi-code", "config.toml");

    /// <summary>Antigravity's permission grant that lets every Agmen tool run without asking.</summary>
    private const string AntigravityGrant = "mcp(agmen/*)";

    private const string KimiRuleComment = "# Added by Agmen's Connect, and removed by Disconnect: Agmen's own tools run without asking.";

    /// <summary>Kimi's permission rule that lets every Agmen tool run without asking, appended to config.toml.</summary>
    private static readonly string[] KimiRule =
    {
        KimiRuleComment,
        "[[permission.rules]]",
        "decision = \"allow\"",
        "pattern = \"mcp__agmen__*\"",
    };

    /// <summary>Where each agent discovers agents of the user's own: Kimi a file per agent, Antigravity a folder per agent.</summary>
    private static string AgentFile(AgentKind agent) => agent == AgentKind.Kimi
        ? Path.Combine(Home, ".kimi-code", "agents", AgentGuide.AgentName + ".md")
        : Path.Combine(Home, ".gemini", "config", "agents", AgentGuide.AgentName, "agent.md");

    private static string AgentText(AgentKind agent) =>
        agent == AgentKind.Kimi ? AgentGuide.KimiAgent() : AgentGuide.AntigravityAgent();

    public static State StateOf(AgentKind agent)
    {
        if (!AgentProfiles.IsInstalled(agent)) return State.NotInstalled;
        try
        {
            var server = Read(McpFile(agent))?["mcpServers"]?[Key] as JsonObject;
            var hooks = agent == AgentKind.Antigravity ? Read(AntigravityHooksFile)?[Key] as JsonObject : new JsonObject();
            bool approved = agent == AgentKind.Kimi ? KimiRuleRange(KimiConfigLines()) is not null : HasAntigravityGrant();
            if (server is null || hooks is null || !approved || !File.Exists(AgentFile(agent))) return State.NotConnected;
            bool here = server["command"] is JsonValue c && c.TryGetValue<string>(out var command)
                        && string.Equals(Path.GetFullPath(command), Path.GetFullPath(ToolServer.Exe), StringComparison.OrdinalIgnoreCase)
                        && server["env"]?["AGMEN_INSTANCE"] is JsonValue i && i.TryGetValue<string>(out var instance)
                        && instance == AppPaths.InstanceName;
            // Hooks that differ from what this copy writes, such as an older Agmen's, need Connect again.
            if (here && agent == AgentKind.Antigravity && !JsonNode.DeepEquals(hooks, AntigravityHooks())) return State.NotConnected;
            return here ? State.Connected : State.ConnectedElsewhere;
        }
        catch (Exception) { return State.Unreadable; }
    }

    public static string Describe(AgentKind agent, State state) => state switch
    {
        State.NotInstalled => $"{AgentProfiles.DisplayName(agent)} isn't installed.",
        State.Connected => "Connected to this copy of Agmen.",
        State.ConnectedElsewhere => "Connected to another copy of Agmen. Connect again to point it at this one.",
        State.Unreadable => $"Its config file isn't valid JSON, so Agmen leaves it alone. Fix {Shown(McpFile(agent))} first.",
        _ => agent == AgentKind.Antigravity
            ? "Not connected. Its tabs can't show when they're working, and its sessions can't use Agmen's tools."
            : "Not connected. Its tabs still show their status, but its sessions can't use Agmen's tools.",
    };

    /// <summary>
    /// The agent to start a new session with: agmen when this copy of Agmen is connected, after
    /// bringing the agent file up to date with this version's guide. Null otherwise, so a session
    /// whose tools reach no Agmen isn't told it has them.
    /// </summary>
    public static string? AgentFor(AgentKind agent)
    {
        if (!Connectable.Contains(agent) || StateOf(agent) != State.Connected) return null;
        try
        {
            var path = AgentFile(agent);
            if (File.ReadAllText(path) != AgentText(agent)) File.WriteAllText(path, AgentText(agent));
        }
        catch (Exception ex) { Log.Write($"connect: couldn't update {AgentFile(agent)}: {ex.Message}"); }
        return AgentGuide.AgentName;
    }

    /// <summary>Exactly what Connect writes, for the user to read before choosing.</summary>
    public static string Preview(AgentKind agent)
    {
        var sb = new StringBuilder();
        sb.Append(Shown(McpFile(agent))).AppendLine(", under \"mcpServers\":");
        sb.Append($"\"{Key}\": ").AppendLine(ToolServer.Entry().ToJsonString(Indented));
        if (agent == AgentKind.Antigravity)
        {
            sb.AppendLine();
            sb.Append(Shown(AntigravityHooksFile)).AppendLine(":");
            sb.Append($"\"{Key}\": ").AppendLine(AntigravityHooks().ToJsonString(Indented));
            sb.AppendLine();
            sb.Append(Shown(AntigravitySettingsFile)).AppendLine(", under \"permissions\", \"allow\":");
            sb.AppendLine($"\"{AntigravityGrant}\"");
        }
        else
        {
            sb.AppendLine();
            sb.Append(Shown(KimiConfigFile)).AppendLine(", at the end:");
            foreach (var line in KimiRule) sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.Append(Shown(AgentFile(agent))).AppendLine(", a new file:");
        sb.AppendLine(AgentText(agent));
        var shared = SharedEntries(agent);
        if (shared.Count > 0)
        {
            sb.AppendLine();
            sb.Append(Shown(McpFile(agent))).AppendLine(", also under \"mcpServers\", Claude Code's user-level MCP servers, "
                                                         + "unless it has them already under any name:");
            foreach (var (name, entry) in shared) sb.Append($"\"{name}\": ").AppendLine(entry.ToJsonString(Indented));
            sb.AppendLine();
        }
        sb.Append("Outside Agmen the agmen entries do nothing: the tool server offers no tools, so its permission covers nothing, "
                  + "the hooks exit at once, and the agmen agent is used only by sessions Agmen starts. "
                  + "The shared servers work in every session, as they do in Claude Code. Agmen keeps them in step with "
                  + "Claude Code's list before each session it starts, and Disconnect removes the ones it added.");
        return sb.ToString();
    }

    public static void Connect(AgentKind agent)
    {
        Update(McpFile(agent), root => ObjectAt(root, "mcpServers")[Key] = ToolServer.Entry());
        ShareServers(agent, again: true);
        if (agent == AgentKind.Antigravity)
        {
            Update(AntigravityHooksFile, root => root[Key] = AntigravityHooks());
            Update(AntigravitySettingsFile, root =>
            {
                var allow = ArrayAt(ObjectAt(root, "permissions"), "allow");
                if (!allow.Any(IsAgmenGrant)) allow.Add(AntigravityGrant);
            });
        }
        else AddKimiRule();
        var file = AgentFile(agent);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, AgentText(agent));
        Log.Write($"connect: {AgentProfiles.DisplayName(agent)} now uses {ToolServer.Exe}");
    }

    public static void Disconnect(AgentKind agent)
    {
        UnshareServers(agent);
        Update(McpFile(agent), root => (root["mcpServers"] as JsonObject)?.Remove(Key));
        if (agent == AgentKind.Antigravity)
        {
            Update(AntigravityHooksFile, root => root.Remove(Key));
            Update(AntigravitySettingsFile, root =>
            {
                if (root["permissions"]?["allow"] is JsonArray allow)
                    foreach (var grant in allow.Where(IsAgmenGrant).ToList()) allow.Remove(grant);
            });
        }
        else RemoveKimiRule();
        var file = AgentFile(agent);
        if (File.Exists(file)) File.Delete(file);
        // Antigravity's agent is a folder of its own; leave it if the user put anything else there.
        if (agent == AgentKind.Antigravity && Path.GetDirectoryName(file) is { } folder
            && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
        Log.Write($"connect: {AgentProfiles.DisplayName(agent)} disconnected");
    }

    /// <summary>One hook per event that tells Agmen a session started, a model call began, or a turn ended.</summary>
    private static JsonObject AntigravityHooks()
    {
        var hooks = new JsonObject();
        foreach (var evt in AntigravityEvents)
            hooks[evt] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = ToolServer.AntigravityHookCommand(evt),
                ["timeout"] = 10,
            });
        return hooks;
    }

    private static bool HasAntigravityGrant() =>
        Read(AntigravitySettingsFile)?["permissions"]?["allow"] is JsonArray allow && allow.Any(IsAgmenGrant);

    private static bool IsAgmenGrant(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var grant) && grant == AntigravityGrant;

    // ------------------------------------------------------------ shared MCP servers

    /// <summary>What Agmen added to each agent's MCP config, as it wrote it, and the names the user removed since.</summary>
    private static string SharedRecordFile => AppPaths.PathOf("shared-servers.json");

    private static List<(string Name, JsonObject Entry)> SharedEntries(AgentKind agent) =>
        SharedServers.Load().Select(s => (s.Name, Entry: SharedServers.EntryFor(agent, s)))
            .Where(s => s.Entry is not null).Select(s => (s.Name, s.Entry!)).ToList();

    /// <summary>Before one of the agent's sessions starts: keep its config in step with the shared servers, if this copy is connected.</summary>
    public static void KeepServersShared(AgentKind agent)
    {
        if (!Connectable.Contains(agent) || StateOf(agent) != State.Connected) return;
        try { ShareServers(agent, again: false); }
        catch (Exception ex) { Log.Write($"connect: couldn't share MCP servers with {AgentProfiles.DisplayName(agent)}: {ex.Message}"); }
    }

    /// <summary>
    /// Add the shared servers the agent's config lacks, and update or remove the ones Agmen added
    /// before. A server the user has under any name stays as it is, and so does one Agmen added that
    /// the user has changed since. One they removed stays removed until they Connect again.
    /// </summary>
    private static void ShareServers(AgentKind agent, bool again) => ShareServers(agent, again, McpFile(agent), SharedRecordFile);

    private static void ShareServers(AgentKind agent, bool again, string mcpFile, string recordFile)
    {
        var records = Read(recordFile) ?? new JsonObject();
        var before = records.ToJsonString();
        var record = ObjectAt(records, AgentProfiles.Id(agent));
        var added = ObjectAt(record, "added");
        var declined = ArrayAt(record, "declined");
        if (again) declined.Clear();
        var servers = Read(mcpFile)?["mcpServers"] as JsonObject ?? new JsonObject();
        var wanted = SharedEntries(agent).ToDictionary(s => s.Name, s => s.Entry);
        var changes = new Dictionary<string, JsonObject?>();   // null removes

        foreach (var (name, written) in added.ToList())
        {
            added.Remove(name);
            if (servers[name] is not JsonObject current)
            {
                if (!again && !Declined(name)) declined.Add(name);
                continue;
            }
            if (!JsonNode.DeepEquals(current, written)) continue;   // changed by the user, so theirs now
            if (!wanted.TryGetValue(name, out var entry)) changes[name] = null;
            else
            {
                if (!JsonNode.DeepEquals(entry, written)) changes[name] = entry;
                added[name] = entry.DeepClone();
            }
        }
        foreach (var (name, entry) in wanted)
        {
            if (added.ContainsKey(name) || servers.ContainsKey(name) || Declined(name)) continue;
            if (servers.Any(s => !(changes.TryGetValue(s.Key, out var c) && c is null) && SharedServers.SameServer(s.Value, entry))) continue;
            changes[name] = entry;
            added[name] = entry.DeepClone();
        }

        if (changes.Count > 0)
        {
            Update(mcpFile, root =>
            {
                var all = ObjectAt(root, "mcpServers");
                foreach (var (name, entry) in changes)
                    if (entry is null) all.Remove(name);
                    else all[name] = entry.DeepClone();
            });
            Log.Write($"connect: {AgentProfiles.DisplayName(agent)} servers "
                      + string.Join(", ", changes.Select(c => (c.Value is null ? "-" : "+") + c.Key)));
        }
        if (records.ToJsonString() != before) File.WriteAllText(recordFile, records.ToJsonString(Indented) + "\n");

        bool Declined(string name) => declined.Any(d => d is JsonValue v && v.TryGetValue<string>(out var s) && s == name);
    }

    private static void UnshareServers(AgentKind agent) => UnshareServers(agent, McpFile(agent), SharedRecordFile);

    /// <summary>Remove the shared servers Agmen added that the user left as they were, and forget the agent's record.</summary>
    private static void UnshareServers(AgentKind agent, string mcpFile, string recordFile)
    {
        var records = Read(recordFile) ?? new JsonObject();
        if (records[AgentProfiles.Id(agent)]?["added"] is JsonObject added
            && Read(mcpFile)?["mcpServers"] is JsonObject servers)
        {
            var ours = added.Where(a => servers[a.Key] is JsonObject current && JsonNode.DeepEquals(current, a.Value))
                .Select(a => a.Key).ToList();
            if (ours.Count > 0)
                Update(mcpFile, root =>
                {
                    if (root["mcpServers"] is JsonObject all) foreach (var name in ours) all.Remove(name);
                });
        }
        if (records.Remove(AgentProfiles.Id(agent))) File.WriteAllText(recordFile, records.ToJsonString(Indented) + "\n");
    }

    // ------------------------------------------------------------ Kimi's config.toml

    private static string[] KimiConfigLines() =>
        File.Exists(KimiConfigFile) ? File.ReadAllText(KimiConfigFile).Replace("\r\n", "\n").Split('\n') : Array.Empty<string>();

    /// <summary>
    /// The lines of Agmen's [[permission.rules]] table in config.toml, from its comment to its
    /// last key, or null when it isn't there. A table counts only with exactly Agmen's two keys.
    /// </summary>
    private static (int Start, int End)? KimiRuleRange(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "[[permission.rules]]") continue;
            var keys = new List<string>();
            int last = i;
            for (int j = i + 1; j < lines.Length && !lines[j].TrimStart().StartsWith('['); j++)
            {
                var line = lines[j].Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                keys.Add(line.Replace(" ", "").Replace('\'', '"'));
                last = j;
            }
            if (keys.Count == 2 && keys.Contains("decision=\"allow\"") && keys.Contains("pattern=\"mcp__agmen__*\""))
                return (i > 0 && lines[i - 1].Trim() == KimiRuleComment ? i - 1 : i, last);
        }
        return null;
    }

    /// <summary>
    /// Append Agmen's rule to config.toml. A file that sets permission rules as an inline list
    /// can't take another [[permission.rules]] table, so it throws and stays as it is.
    /// </summary>
    private static void AddKimiRule()
    {
        var lines = KimiConfigLines();
        if (KimiRuleRange(lines) is not null) return;
        string? table = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) table = line;
            else if ((table == "[permission]" && Regex.IsMatch(line, @"^rules\s*="))
                     || (table is null && Regex.IsMatch(line, @"^permission(\.rules)?\s*=")))
                throw new InvalidOperationException(
                    $"{Shown(KimiConfigFile)} already lists its permission rules in a form Agmen can't add to. "
                    + $"Add this rule to them yourself: decision \"allow\", pattern \"mcp__agmen__*\".");
        }
        var text = string.Join("\n", lines).TrimEnd('\n');
        WriteKimiConfig((text.Length == 0 ? "" : text + "\n\n") + string.Join("\n", KimiRule) + "\n");
    }

    private static void RemoveKimiRule()
    {
        var lines = KimiConfigLines().ToList();
        if (KimiRuleRange(lines.ToArray()) is not { } range) return;
        lines.RemoveRange(range.Start, range.End - range.Start + 1);
        // The blank line Connect put before the rule goes with it.
        if (range.Start > 0 && range.Start <= lines.Count && lines[range.Start - 1].Trim().Length == 0
            && (range.Start == lines.Count || lines[range.Start].Trim().Length == 0))
            lines.RemoveAt(range.Start - 1);
        WriteKimiConfig(string.Join("\n", lines).TrimEnd('\n') + "\n");
    }

    private static void WriteKimiConfig(string text)
    {
        var path = KimiConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && !File.Exists(path + ".agmen-backup")) File.Copy(path, path + ".agmen-backup");
        var tmp = path + ".agmen-tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    // ------------------------------------------------------------ JSON config files

    /// <summary>Change one config file, keeping everything else in it; a file that isn't valid JSON throws and stays as it is.</summary>
    private static void Update(string path, Action<JsonObject> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = Read(path) ?? new JsonObject();
        if (File.Exists(path) && !File.Exists(path + ".agmen-backup")) File.Copy(path, path + ".agmen-backup");
        change(root);
        var tmp = path + ".agmen-tmp";
        File.WriteAllText(tmp, root.ToJsonString(Indented) + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The object under a key, added when missing. A node can have only one parent, so an existing one is never set again.</summary>
    private static JsonObject ObjectAt(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var added = new JsonObject();
        parent[key] = added;
        return added;
    }

    private static JsonArray ArrayAt(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing) return existing;
        var added = new JsonArray();
        parent[key] = added;
        return added;
    }

    private static JsonObject? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text) as JsonObject ?? throw new JsonException($"{path} isn't a JSON object.");
    }

    private static string Shown(string path) => "~" + path[Home.Length..];
}
