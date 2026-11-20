using System.IO;
using System.Text.Json;
using Agmen.Services;

namespace Agmen.Agents;

/// <summary>A session found in an agent's own records, which Agmen can resume in a tab.</summary>
public sealed record FoundSession(AgentKind Agent, string SessionId, string Cwd, string Title, DateTime LastActive);

/// <summary>
/// Recent sessions of every agent, including ones started outside Agmen, read from each agent's
/// own records: Claude Code's transcripts, Codex's session files and prompt history, Kimi Code's
/// session index, and Antigravity's prompt history. Reads files only; call it off the UI thread.
/// </summary>
public static class SessionDiscovery
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Sessions active within the window whose folder still exists, newest first.</summary>
    public static List<FoundSession> Recent(TimeSpan window)
    {
        var since = DateTime.Now - window;
        var found = new List<FoundSession>();
        Collect("Claude Code", () => Claude(window), found);
        Collect("Codex", () => Codex(since), found);
        Collect("Kimi Code", () => Kimi(since), found);
        Collect("Antigravity", () => Antigravity(since), found);
        return found.Where(f => Directory.Exists(f.Cwd)).OrderByDescending(f => f.LastActive).ToList();
    }

    private static void Collect(string agent, Func<List<FoundSession>> read, List<FoundSession> into)
    {
        try { into.AddRange(read()); }
        catch (Exception ex) { Log.Write($"history: couldn't read {agent}'s sessions: {ex.Message}"); }
    }

    private static List<FoundSession> Claude(TimeSpan window) =>
        RestoreService.RecentFromTranscripts(window)
            .Select(r => new FoundSession(AgentKind.Claude, r.Entry.SessionId, r.Entry.Cwd, r.Entry.Name, r.LastActive))
            .ToList();

    /// <summary>
    /// Codex: each session file starts with the session's id and folder, by day under
    /// ~/.codex/sessions; ~/.codex/history.jsonl has the prompts that give it a title.
    /// </summary>
    private static List<FoundSession> Codex(DateTime since)
    {
        var list = new List<FoundSession>();
        var root = Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(root)) return list;
        var titles = FirstPrompts(Path.Combine(Home, ".codex", "history.jsonl"), "session_id", "text");
        for (var day = DateTime.Now.Date; day >= since.Date; day = day.AddDays(-1))
        {
            var dir = Path.Combine(root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl"))
            {
                if (file.LastWriteTime < since || FirstLine(file.FullName) is not { } first) continue;
                using var doc = JsonDocument.Parse(first);
                if (!doc.RootElement.TryGetProperty("payload", out var meta) || meta.ValueKind != JsonValueKind.Object) continue;
                // Sessions run by scripts through codex exec aren't conversations to come back to.
                if (Str(meta, "source") == "exec" || Str(meta, "id") is not { } id || Str(meta, "cwd") is not { } cwd) continue;
                list.Add(new FoundSession(AgentKind.Codex, id, cwd, titles.GetValueOrDefault(id) ?? "", file.LastWriteTime));
            }
        }
        return list;
    }

    /// <summary>Kimi: its session index names each session's folder, and the session's state.json its title.</summary>
    private static List<FoundSession> Kimi(DateTime since)
    {
        var list = new List<FoundSession>();
        foreach (var (id, dir, workDir) in AgentMonitor.KimiIndex())
        {
            var state = Path.Combine(dir, "state.json");
            if (!File.Exists(state)) continue;
            var last = File.GetLastWriteTime(state);
            if (last < since) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(state));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (root.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True) continue;
            var title = Str(root, "title") ?? Str(root, "lastPrompt") ?? "";
            list.Add(new FoundSession(AgentKind.Kimi, id, Path.GetFullPath(workDir), title, last));
        }
        return list;
    }

    /// <summary>
    /// Antigravity: every prompt goes into ~/.gemini/antigravity-cli/history.jsonl with its
    /// workspace. A conversation's first prompt comes before the conversation has an id, so
    /// it titles the next id seen in the same workspace.
    /// </summary>
    private static List<FoundSession> Antigravity(DateTime since)
    {
        var file = Path.Combine(Home, ".gemini", "antigravity-cli", "history.jsonl");
        var byId = new Dictionary<string, (string Title, string Cwd, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return new();
        string? firstPrompt = null, firstWorkspace = null;
        foreach (var line in ReadLinesShared(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var prompt = Str(r, "display") ?? "";
                var workspace = Str(r, "workspace");
                var at = r.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.MinValue;
                if (Str(r, "conversationId") is not { } id)
                {
                    firstPrompt = prompt;
                    firstWorkspace = workspace;
                    continue;
                }
                if (workspace is null) continue;
                if (byId.TryGetValue(id, out var known)) byId[id] = known with { Last = at > known.Last ? at : known.Last };
                else byId[id] = (firstPrompt is not null && AgentMonitor.SameFolder(firstWorkspace, workspace) ? firstPrompt : prompt, workspace, at);
                firstPrompt = firstWorkspace = null;
            }
            catch (JsonException) { /* a line being written */ }
        }
        return byId.Where(kv => kv.Value.Last >= since)
            .Select(kv => new FoundSession(AgentKind.Antigravity, kv.Key, kv.Value.Cwd, kv.Value.Title, kv.Value.Last))
            .ToList();
    }

    /// <summary>The first prompt of each session in a prompt-history file of JSON lines.</summary>
    private static Dictionary<string, string> FirstPrompts(string file, string idKey, string textKey)
    {
        var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return first;
        foreach (var line in ReadLinesShared(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (Str(doc.RootElement, idKey) is { } id && Str(doc.RootElement, textKey) is { } text) first.TryAdd(id, text);
            }
            catch (JsonException) { /* a line being written */ }
        }
        return first;
    }

    private static string? FirstLine(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadLine();
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
            if (line.Length > 1) yield return line;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
