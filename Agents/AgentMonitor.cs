using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agmen.Models;
using Agmen.Services;

namespace Agmen.Agents;

/// <summary>A tab running an agent other than Claude, as the monitor needs to see it.</summary>
public sealed record AgentTabRef(Guid Id, AgentKind Agent, string Cwd, int Pid, DateTime StartedAt, string? SessionId);

/// <summary>What the monitor knows about one tab's session beyond its status.</summary>
public sealed record AgentSessionState(string? SessionId, int TurnsCompleted, string? Transcript, string? LastReply,
                                       bool AgentRefused = false);

/// <summary>
/// Follows the sessions of agents that have no live registry like Claude Code's, and reports
/// them as <see cref="SessionInfo"/>, so the workspace treats every tab alike:
/// <list type="bullet">
/// <item>Codex from its session file, ~/.codex/sessions/yyyy/MM/dd/rollout-*-&lt;id&gt;.jsonl, and the
/// notify command Agmen gives it on its command line;</item>
/// <item>Kimi Code from its session log, ~/.kimi-code/sessions/&lt;workspace&gt;/&lt;session&gt;/agents/main/wire.jsonl;</item>
/// <item>Antigravity from its hooks, once the user has connected it.</item>
/// </list>
/// <see cref="Poll"/> runs on a background thread; hook events arrive on pipe threads.
/// </summary>
public sealed class AgentMonitor
{
    private sealed class Track
    {
        public AgentKind Agent;
        public DateTime StartedAt;
        public string? SessionId;
        /// <summary>The file followed for status: Codex's session file or Kimi's wire.jsonl.</summary>
        public string? File;
        public long Offset;
        /// <summary>"", "busy", "idle" or "waiting"; empty until the session reports anything.</summary>
        public string Status = "";
        public string? Model;
        public long ContextTokens, ContextWindow, OutputTokens;
        public DateTimeOffset? LastActivity;
        public int TurnsCompleted;
        /// <summary>What read_session reads: Codex's session file, Kimi's wire.jsonl, Antigravity's transcript.jsonl.</summary>
        public string? Transcript;
        public string? LastReply;
        public DateTime LastSearch = DateTime.MinValue;
        /// <summary>Antigravity: a hook has fired in this run of the tab, so hooks will also end what its log starts.</summary>
        public bool HooksSeen;
        /// <summary>Antigravity: its log says it can't build the agent the session runs as.</summary>
        public bool AgentRefused;
    }

    /// <summary>On a first read of a long session, only its last few megabytes matter for status.</summary>
    private const long TailBytes = 4 * 1024 * 1024;

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string CodexSessions = Path.Combine(Home, ".codex", "sessions");
    private static readonly string KimiHome = Path.Combine(Home, ".kimi-code");

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Track> _tracks = new();

    /// <summary>Bring every tab's session up to date and report it. Background thread.</summary>
    public Dictionary<Guid, SessionInfo> Poll(IReadOnlyList<AgentTabRef> tabs)
    {
        var result = new Dictionary<Guid, SessionInfo>();
        lock (_gate)
        {
            foreach (var gone in _tracks.Keys.Where(id => tabs.All(t => t.Id != id)).ToList()) _tracks.Remove(gone);
            foreach (var tab in tabs)
            {
                var track = TrackFor(tab);
                try
                {
                    if (tab.Agent == AgentKind.Codex) PollCodex(tab, track);
                    else if (tab.Agent == AgentKind.Kimi) PollKimi(tab, track);
                    // Antigravity reports its turns through hooks (see OnHook), the rest in its log.
                    else if (tab.Agent == AgentKind.Antigravity) PollAntigravity(tab, track);
                }
                catch (IOException) { /* the agent is writing the file; try again next poll */ }
                catch (Exception ex) { Log.Write($"agents: {tab.Agent} poll failed: {ex.Message}"); }
                result[tab.Id] = ToInfo(tab, track);
            }
        }
        return result;
    }

    /// <summary>A signal from an agent CLI, already matched to its tab. Pipe thread.</summary>
    public void OnHook(AgentTabRef tab, string evt, JsonObject? payload)
    {
        lock (_gate)
        {
            var t = TrackFor(tab);
            t.LastActivity = DateTimeOffset.Now;
            switch (tab.Agent, evt)
            {
                case (AgentKind.Codex, "notify"):
                    // Sent once per finished turn: it names the session, so a guessed session file is corrected here.
                    if (Str(payload, "thread-id") is { Length: > 0 } thread && thread != t.SessionId)
                    {
                        t.SessionId = thread;
                        if (t.File is not null && !t.File.Contains(thread, StringComparison.OrdinalIgnoreCase))
                        {
                            t.File = t.Transcript = null;
                            t.Offset = 0;
                        }
                    }
                    t.Status = "idle";
                    t.TurnsCompleted = Math.Max(t.TurnsCompleted, 1);
                    t.LastReply = Str(payload, "last-assistant-message") ?? t.LastReply;
                    break;
                case (AgentKind.Antigravity, _):
                    t.HooksSeen = true;
                    t.SessionId = Str(payload, "conversationId") ?? t.SessionId;
                    t.Transcript = Str(payload, "transcriptPath") is { Length: > 0 } path ? Path.GetFullPath(path) : t.Transcript;
                    t.Model = Str(payload, "modelName") ?? t.Model;
                    if (evt == "PreInvocation") t.Status = "busy";
                    else if (evt == "Stop")
                    {
                        t.Status = "idle";
                        t.TurnsCompleted++;
                    }
                    else if (evt == "SessionStart" && t.Status.Length == 0) t.Status = "idle";
                    break;
            }
        }
    }

    public AgentSessionState? StateOf(Guid tabId)
    {
        lock (_gate)
            return _tracks.TryGetValue(tabId, out var t) ? new AgentSessionState(t.SessionId, t.TurnsCompleted, t.Transcript, t.LastReply, t.AgentRefused) : null;
    }

    private Track TrackFor(AgentTabRef tab)
    {
        if (_tracks.TryGetValue(tab.Id, out var t) && t.StartedAt == tab.StartedAt) return t;
        // A new tab, or the tab was started again: follow its session from scratch.
        t = new Track { Agent = tab.Agent, StartedAt = tab.StartedAt, SessionId = tab.SessionId };
        _tracks[tab.Id] = t;
        return t;
    }

    private static SessionInfo ToInfo(AgentTabRef tab, Track t) => new()
    {
        Pid = tab.Pid,
        SessionId = t.SessionId ?? "",
        Cwd = tab.Cwd,
        Entrypoint = AgentProfiles.Id(tab.Agent),
        // A session that hasn't reported yet is sitting at its prompt.
        Status = t.Status.Length == 0 ? "idle" : t.Status,
        StartedAt = new DateTimeOffset(tab.StartedAt),
        UpdatedAt = DateTimeOffset.Now,
        Model = t.Model,
        ContextTokens = t.ContextTokens,
        ContextWindow = t.ContextWindow,
        OutputTokens = t.OutputTokens,
        LastActivity = t.LastActivity,
    };

    // ------------------------------------------------------------ Codex

    private void PollCodex(AgentTabRef tab, Track t)
    {
        if (t.File is null)
        {
            if (DateTime.UtcNow - t.LastSearch < TimeSpan.FromSeconds(3)) return;
            t.LastSearch = DateTime.UtcNow;
            t.File = t.SessionId is { } id ? FindCodexFile(id) : ClaimCodexFile(tab, t);
            if (t.File is null) return;
            t.Transcript = t.File;
        }
        foreach (var line in ReadNewLines(t.File, ref t.Offset)) ApplyCodex(line, t);
    }

    /// <summary>The session file of a known Codex session.</summary>
    public static string? FindCodexFile(string sessionId)
    {
        if (!Directory.Exists(CodexSessions)) return null;
        try
        {
            return Directory.EnumerateFiles(CodexSessions, $"rollout-*{sessionId}.jsonl", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// A new Codex session writes its file on the first prompt. Its first record names the folder
    /// and the time the session began, which picks out this tab's file from other sessions'.
    /// </summary>
    private string? ClaimCodexFile(AgentTabRef tab, Track t)
    {
        var claimed = _tracks.Values.Where(x => x != t && x.File is not null).Select(x => x.File!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var since = tab.StartedAt.AddSeconds(-10);
        foreach (var day in new[] { DateTime.Now, DateTime.Now.AddDays(-1) })
        {
            var dir = Path.Combine(CodexSessions, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl")
                         .Where(f => f.LastWriteTime >= since).OrderByDescending(f => f.CreationTime))
            {
                if (claimed.Contains(file.FullName)) continue;
                var meta = FirstLine(file.FullName);
                if (meta is null) continue;
                using var doc = JsonDocument.Parse(meta);
                if (!doc.RootElement.TryGetProperty("payload", out var p)) continue;
                if (!SameFolder(Str(p, "cwd"), tab.Cwd)) continue;
                if (!DateTimeOffset.TryParse(Str(p, "timestamp"), out var began) || began.LocalDateTime < since) continue;
                t.SessionId = Str(p, "id") ?? t.SessionId;
                return file.FullName;
            }
        }
        return null;
    }

    private static void ApplyCodex(string line, Track t)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        if (DateTimeOffset.TryParse(Str(r, "timestamp"), out var at)) t.LastActivity = at;
        if (!r.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return;
        switch (Str(r, "type"))
        {
            case "session_meta":
                t.SessionId ??= Str(p, "id");
                break;
            case "turn_context":
                t.Model = Str(p, "model") ?? t.Model;
                break;
            case "event_msg":
                switch (Str(p, "type"))
                {
                    case "task_started":
                        t.Status = "busy";
                        if (Long(p, "model_context_window") is > 0 and var window) t.ContextWindow = window;
                        break;
                    case "task_complete":
                        t.Status = "idle";
                        t.TurnsCompleted++;
                        t.LastReply = Str(p, "last_agent_message") ?? t.LastReply;
                        break;
                    case "turn_aborted":
                        t.Status = "idle";
                        break;
                    case "exec_approval_request" or "apply_patch_approval_request" or "request_user_input":
                        t.Status = "waiting";
                        break;
                    case "token_count" when p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object:
                        if (info.TryGetProperty("last_token_usage", out var last) && Long(last, "input_tokens") is { } input)
                            t.ContextTokens = input;
                        if (info.TryGetProperty("total_token_usage", out var total) && Long(total, "output_tokens") is { } output)
                            t.OutputTokens = output;
                        if (Long(info, "model_context_window") is > 0 and var w) t.ContextWindow = w;
                        break;
                }
                break;
        }
    }

    // ------------------------------------------------------------ Antigravity

    /// <summary>
    /// Antigravity's hooks report its turns, but no hook fires when it waits for the user to allow
    /// a command or a URL, or when the user stops a turn. Its log says both. Each agy process
    /// writes its own, ~/.gemini/antigravity-cli/log/cli-&lt;start time&gt;.log, so the file that
    /// started with this tab is this tab's.
    /// </summary>
    private void PollAntigravity(AgentTabRef tab, Track t)
    {
        if (t.File is null)
        {
            if (DateTime.UtcNow - t.LastSearch < TimeSpan.FromSeconds(3)) return;
            t.LastSearch = DateTime.UtcNow;
            t.File = ClaimAntigravityLog(tab, t);
            if (t.File is null) return;
        }
        foreach (var line in ReadNewLines(t.File, ref t.Offset)) ApplyAntigravityLog(line, t);
    }

    private string? ClaimAntigravityLog(AgentTabRef tab, Track t)
    {
        var dir = Path.Combine(Home, ".gemini", "antigravity-cli", "log");
        if (!Directory.Exists(dir)) return null;
        var claimed = _tracks.Values.Where(x => x != t && x.Agent == AgentKind.Antigravity && x.File is not null)
            .Select(x => x.File!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("cli-*.log").OrderBy(f => f.Name))
        {
            if (claimed.Contains(file.FullName)
                || !DateTime.TryParseExact(file.Name[4..^4], "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)
                || started < tab.StartedAt.AddSeconds(-5) || started > tab.StartedAt.AddSeconds(60))
                continue;
            // Status from here on only: the hooks have reported what happened before. But agy may
            // already have refused the agent, which no hook reports.
            long read = 0;
            foreach (var line in ReadNewLines(file.FullName, ref read))
                if (IsAgentRefusal(line)) t.AgentRefused = true;
            t.Offset = read;
            return file.FullName;
        }
        return null;
    }

    /// <summary>
    /// agy's log line for an agent it can't build, as when the agent names a tool this agy doesn't
    /// have: "failed to construct executor: … tool "x" not found in registry". The session stays
    /// open and answers every message with it.
    /// </summary>
    private static bool IsAgentRefusal(string line) =>
        line.Contains("] failed to construct executor:", StringComparison.Ordinal)
        && line.Contains("not found in registry", StringComparison.Ordinal);

    private static void ApplyAntigravityLog(string line, Track t)
    {
        if (IsAgentRefusal(line))
        {
            t.AgentRefused = true;
            return;
        }
        // Only hooks end what the log starts. A tab whose agent isn't connected fires none, so a
        // status read from its log would never clear.
        if (!t.HooksSeen) return;
        // A tool that needs the user's OK, or a question the agent asks them (ask_question).
        if (line.Contains("Surfacing tool confirmation", StringComparison.Ordinal)
            || line.Contains("Surfacing ask_question", StringComparison.Ordinal)) t.Status = "waiting";
        else if (line.Contains("Responding to tool confirmation", StringComparison.Ordinal)) t.Status = "busy";
        else if (line.Contains("Cancelling in-progress response", StringComparison.Ordinal)) t.Status = "idle";
        else return;
        t.LastActivity = DateTimeOffset.Now;
    }

    // ------------------------------------------------------------ Kimi Code

    private void PollKimi(AgentTabRef tab, Track t)
    {
        if (t.File is null)
        {
            if (DateTime.UtcNow - t.LastSearch < TimeSpan.FromSeconds(3)) return;
            t.LastSearch = DateTime.UtcNow;
            var dir = t.SessionId is { } id ? KimiSessionDir(id)?.Dir : ClaimKimiSession(tab, t);
            if (dir is null) return;
            var wire = Path.Combine(dir, "agents", "main", "wire.jsonl");
            if (!System.IO.File.Exists(wire)) return;
            t.File = t.Transcript = wire;
        }
        foreach (var line in ReadNewLines(t.File, ref t.Offset)) ApplyKimi(line, t);
    }

    /// <summary>A Kimi session's folder and working directory, from Kimi's session index.</summary>
    public static (string Dir, string WorkDir)? KimiSessionDir(string sessionId)
    {
        foreach (var (id, dir, workDir) in KimiIndex())
            if (string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase)) return (dir, workDir);
        return null;
    }

    /// <summary>Kimi adds a session to its index on the first prompt; the newest unclaimed one for this folder is this tab's.</summary>
    private string? ClaimKimiSession(AgentTabRef tab, Track t)
    {
        var claimed = _tracks.Values.Where(x => x != t && x.SessionId is not null).Select(x => x.SessionId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var since = new DateTimeOffset(tab.StartedAt.AddSeconds(-10)).ToUnixTimeMilliseconds();
        foreach (var (id, dir, workDir) in KimiIndex().Reverse())
        {
            if (claimed.Contains(id) || !SameFolder(workDir, tab.Cwd)) continue;
            var state = Path.Combine(dir, "state.json");
            if (!System.IO.File.Exists(state)) continue;
            // Older Kimi versions wrote createdAt as text; those sessions are never this tab's.
            long? created;
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(state));
                created = Long(doc.RootElement, "createdAt");
            }
            catch (Exception) { continue; }   // being written, or unreadable
            if (created is null || created < since) continue;
            t.SessionId = id;
            return dir;
        }
        return null;
    }

    /// <summary>Every session in Kimi's session index: its id, its folder of records, and its working folder.</summary>
    internal static IEnumerable<(string Id, string Dir, string WorkDir)> KimiIndex()
    {
        var index = Path.Combine(KimiHome, "session_index.jsonl");
        if (!System.IO.File.Exists(index)) return Array.Empty<(string, string, string)>();
        var list = new List<(string, string, string)>();
        foreach (var line in ReadAllLinesShared(index))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (Str(r, "sessionId") is { } id && Str(r, "sessionDir") is { } dir)
                    list.Add((id, Path.GetFullPath(dir), Str(r, "workDir") ?? ""));
            }
            catch (JsonException) { /* a line being written */ }
        }
        return list;
    }

    private static void ApplyKimi(string line, Track t)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        if (Long(r, "time") is { } ms) t.LastActivity = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        switch (Str(r, "type"))
        {
            case "agent.turn.started" or "turn.prompt" or "interaction.resolved":
                t.Status = "busy";
                break;
            case "interaction.request":
                t.Status = "waiting";
                break;
            case "turn.ended":
                t.Status = "idle";
                t.TurnsCompleted++;
                break;
            case "agent.turn.ended" or "prompt.completed":
                t.Status = "idle";
                break;
            case "agent.message.appended"
                when r.TryGetProperty("message", out var outer) && outer.TryGetProperty("message", out var m)
                     && Str(m, "role") == "assistant" && AgentTranscripts.TextOf(m) is { Length: > 0 } reply:
                t.LastReply = reply;
                break;
        }
    }

    /// <summary>
    /// True when the agent won't ask the user whether to trust this folder before its prompt
    /// comes up, so text Agmen types reaches the prompt and not that question.
    /// </summary>
    public static bool Trusts(AgentKind agent, string folder) => agent switch
    {
        AgentKind.Kimi => KimiTrusts(folder),
        AgentKind.Antigravity => AntigravityTrusts(folder),
        AgentKind.Codex => CodexTrusts(folder),
        _ => false,
    };

    /// <summary>Antigravity lists the folders the user trusted in its CLI settings.</summary>
    public static bool AntigravityTrusts(string folder)
    {
        var settings = Path.Combine(Home, ".gemini", "antigravity-cli", "settings.json");
        try
        {
            if (!System.IO.File.Exists(settings)) return false;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(settings));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("trustedWorkspaces", out var list) || list.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in list.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && SameFolder(item.GetString(), folder)) return true;
        }
        catch (Exception) { /* unreadable settings: not trusted */ }
        return false;
    }

    /// <summary>
    /// Codex asks about trust only in a git repository, and saves the answer in config.toml
    /// under [projects.'&lt;folder&gt;'] as trust_level = "trusted" or "untrusted".
    /// </summary>
    public static bool CodexTrusts(string folder)
    {
        var repo = GitRoot(folder);
        if (repo is null) return true;
        var config = Path.Combine(Home, ".codex", "config.toml");
        try
        {
            if (!System.IO.File.Exists(config)) return false;
            string? project = null;
            foreach (var raw in System.IO.File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    var header = CodexProjectHeader.Match(line);
                    project = !header.Success ? null
                        : header.Groups[1].Success ? header.Groups[1].Value : header.Groups[2].Value.Replace(@"\\", @"\");
                }
                else if (project is not null && CodexTrusted.IsMatch(line) && (SameFolder(project, repo) || SameFolder(project, folder)))
                    return true;
            }
        }
        catch (Exception) { /* unreadable config: not trusted */ }
        return false;
    }

    private static readonly Regex CodexProjectHeader = new(@"^\[projects\.(?:'([^']*)'|""((?:[^""\\]|\\.)*)"")\]$");
    private static readonly Regex CodexTrusted = new(@"^trust_level\s*=\s*[""']trusted[""']");

    private static string? GitRoot(string folder)
    {
        try
        {
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || System.IO.File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
        }
        catch (Exception) { /* an unreadable folder: treat it as outside a repository */ }
        return null;
    }

    /// <summary>True when Kimi has been told to trust this folder, so its prompt, not a trust question, takes typed text.</summary>
    public static bool KimiTrusts(string folder)
    {
        var dir = Path.Combine(KimiHome, "workspace-trust");
        if (!Directory.Exists(dir)) return false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                if (SameFolder(Str(doc.RootElement, "root"), folder)) return true;
            }
        }
        catch (Exception) { /* unreadable trust store: not trusted */ }
        return false;
    }

    // ------------------------------------------------------------ helpers

    private static List<string> ReadNewLines(string path, ref long offset)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < offset) offset = 0;   // replaced or truncated
        bool skipFirst = false;
        if (offset == 0 && fs.Length > TailBytes)
        {
            // Start a long file near its end; the line cut in half there is dropped.
            offset = fs.Length - TailBytes;
            skipFirst = true;
        }
        if (fs.Length == offset) return lines;
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - offset];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
        }
        // Only complete lines: a partial last line is read again next time.
        int last = Array.LastIndexOf(buf, (byte)'\n', read - 1);
        if (last < 0) return lines;
        offset += last + 1;
        var text = Encoding.UTF8.GetString(buf, 0, last + 1).Split('\n');
        for (int i = skipFirst ? 1 : 0; i < text.Length; i++)
            if (text[i].Length > 1) lines.Add(text[i]);
        return lines;
    }

    private static string? FirstLine(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadLine();
    }

    private static IEnumerable<string> ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 1) lines.Add(line);
        return lines;
    }

    /// <summary>Agents write folders with either slash and any case.</summary>
    public static bool SameFolder(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : null;
}
