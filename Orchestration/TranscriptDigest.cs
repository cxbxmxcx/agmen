using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agmen.Orchestration;

/// <summary>
/// Reads the tail of a session's transcript and boils it down to what an orchestrator
/// needs: the latest prompts and replies, the todo list, files changed, recent commands,
/// and the tool call a waiting session wants approved.
/// </summary>
public static class TranscriptDigest
{
    public sealed record Message(string Role, DateTimeOffset? At, string Text);
    public sealed record Todo(string Status, string Text);

    public sealed class Digest
    {
        public List<Message> Messages { get; } = new();
        public List<Todo> Todos { get; set; } = new();
        public List<string> Files { get; } = new();
        public List<string> Commands { get; } = new();
        public string? PendingTool { get; set; }
        public DateTimeOffset? LastActivity { get; set; }
    }

    private const int TailBytes = 4 * 1024 * 1024;
    private static readonly Regex SystemReminder = new(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CommandName = new(@"<command-name>(.*?)</command-name>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CommandArgs = new(@"<command-args>(.*?)</command-args>", RegexOptions.Singleline | RegexOptions.Compiled);

    public static Digest Read(string path, string cwd, int maxMessages)
    {
        var digest = new Digest();
        var messages = new List<Message>();
        var openTools = new Dictionary<string, string>();   // tool_use id -> description, until its result arrives
        var taskIndex = new Dictionary<string, int>();      // TaskCreate tool_use id -> todo index
        string? lastAssistantId = null;

        foreach (var line in ReadTailLines(path))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                if (r.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
                var type = Str(r, "type");
                if (type is not ("user" or "assistant")) continue;
                DateTimeOffset? at = DateTimeOffset.TryParse(Str(r, "timestamp"), out var ts) ? ts : null;
                if (at is not null) digest.LastActivity = at;
                if (!r.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("content", out var content)) continue;

                if (type == "user")
                {
                    bool isMeta = r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True;
                    // Messages from other sessions arrive as hidden user records; show who sent what.
                    var plain = PlainText(content);
                    if (plain is not null && CrossSession.Match(plain) is { Success: true } peer)
                    {
                        messages.Add(new Message($"message from {peer.Groups[1].Value}", at, peer.Groups[2].Value.Trim()));
                        continue;
                    }
                    if (isMeta) continue;
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        AddUser(messages, at, content.GetString() ?? "");
                        continue;
                    }
                    if (content.ValueKind != JsonValueKind.Array) continue;
                    var text = new StringBuilder();
                    foreach (var block in content.EnumerateArray())
                    {
                        var blockType = Str(block, "type");
                        if (blockType == "tool_result")
                        {
                            var useId = Str(block, "tool_use_id");
                            if (useId is not null)
                            {
                                openTools.Remove(useId);
                                if (taskIndex.TryGetValue(useId, out var idx))
                                {
                                    var resultText = ResultText(block);
                                    var num = Regex.Match(resultText, @"#(\d+)");
                                    if (num.Success) taskIndex[$"task:{num.Groups[1].Value}"] = idx;
                                }
                            }
                        }
                        else if (blockType == "text") text.Append(Str(block, "text")).Append('\n');
                        else if (blockType == "image") text.Append("[image]\n");
                    }
                    if (text.Length > 0) AddUser(messages, at, text.ToString());
                    continue;
                }

                // assistant
                if (content.ValueKind != JsonValueKind.Array) continue;
                var messageId = Str(msg, "id");
                foreach (var block in content.EnumerateArray())
                {
                    switch (Str(block, "type"))
                    {
                        case "text":
                            var t = (Str(block, "text") ?? "").Trim();
                            if (t.Length == 0) break;
                            if (messageId is not null && messageId == lastAssistantId && messages.Count > 0 && messages[^1].Role == "claude")
                                messages[^1] = messages[^1] with { Text = messages[^1].Text + "\n" + t };
                            else messages.Add(new Message("claude", at, t));
                            lastAssistantId = messageId;
                            break;
                        case "tool_use":
                            TrackTool(block, digest, openTools, taskIndex, cwd);
                            if (Str(block, "name") == "SendMessage" && block.TryGetProperty("input", out var sent)
                                && Str(sent, "message") is { Length: > 0 } outgoing)
                                messages.Add(new Message($"claude, to {Str(sent, "to") ?? "another session"}", at, outgoing));
                            break;
                    }
                }
            }
        }

        digest.PendingTool = openTools.Count > 0 ? openTools.Values.Last() : null;
        foreach (var m in messages.TakeLast(Math.Clamp(maxMessages, 1, 20))) digest.Messages.Add(m);
        return digest;
    }

    private static readonly Regex CrossSession = new(
        "<cross-session-message[^>]*?from-name=\"([^\"]*)\"[^>]*>(.*?)(?:</cross-session-message>|$)",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TaskSummary = new(@"<summary>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>A user record's text, whether stored as a string or as text blocks.</summary>
    private static string? PlainText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
            if (Str(block, "type") == "text") sb.Append(Str(block, "text")).Append('\n');
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static void AddUser(List<Message> messages, DateTimeOffset? at, string raw)
    {
        var text = SystemReminder.Replace(raw, "").Trim();
        if (text.Length == 0) return;
        if (text.StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            // Claude Code's own notice that a background task or subagent finished.
            var summary = TaskSummary.Match(text);
            messages.Add(new Message("event", at, summary.Success ? summary.Groups[1].Value.Trim() : "a background task finished"));
            return;
        }
        var command = CommandName.Match(text);
        if (command.Success)
        {
            var args = CommandArgs.Match(text);
            text = (command.Groups[1].Value.Trim() + " " + (args.Success ? args.Groups[1].Value.Trim() : "")).Trim();
        }
        else if (text.StartsWith("<local-command-stdout>", StringComparison.Ordinal)
                 || text.StartsWith("<local-command-caveat>", StringComparison.Ordinal)
                 || text.StartsWith("[Request interrupted", StringComparison.Ordinal))
            return;
        messages.Add(new Message("user", at, text));
    }

    private static void TrackTool(JsonElement block, Digest digest, Dictionary<string, string> openTools,
                                  Dictionary<string, int> taskIndex, string cwd)
    {
        var name = Str(block, "name") ?? "tool";
        var id = Str(block, "id");
        block.TryGetProperty("input", out var input);
        string? detail = null;

        switch (name)
        {
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit":
                var file = Str(input, "file_path") ?? Str(input, "notebook_path");
                if (file is not null)
                {
                    var shown = Relative(file, cwd);
                    detail = shown;
                    if (IsScratch(file)) break;   // throwaway helper files say little about the work
                    digest.Files.Remove(shown);
                    digest.Files.Add(shown);
                    if (digest.Files.Count > 15) digest.Files.RemoveAt(0);
                }
                break;
            case "Bash" or "PowerShell":
                var command = Str(input, "command");
                if (command is not null)
                {
                    var first = command.Split('\n')[0].Trim();
                    if (first.Length > 140) first = first[..137] + "...";
                    digest.Commands.Add(first);
                    if (digest.Commands.Count > 8) digest.Commands.RemoveAt(0);
                    detail = first;
                }
                break;
            case "TodoWrite":
                if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
                    digest.Todos = todos.EnumerateArray()
                        .Select(t => new Todo(Str(t, "status") ?? "pending", Str(t, "content") ?? Str(t, "activeForm") ?? ""))
                        .Where(t => t.Text.Length > 0)
                        .ToList();
                break;
            case "TaskCreate":
                var subject = Str(input, "subject") ?? Str(input, "description");
                if (subject is not null)
                {
                    digest.Todos.Add(new Todo("pending", subject));
                    if (id is not null) taskIndex[id] = digest.Todos.Count - 1;
                }
                break;
            case "TaskUpdate":
                var taskId = Str(input, "taskId");
                var status = Str(input, "status");
                if (taskId is not null && status is not null && taskIndex.TryGetValue($"task:{taskId.TrimStart('#')}", out var idx)
                    && idx < digest.Todos.Count)
                    digest.Todos[idx] = digest.Todos[idx] with { Status = status };
                break;
            default:
                detail = Str(input, "url") ?? Str(input, "pattern") ?? Str(input, "description") ?? Str(input, "file_path");
                break;
        }

        if (id is not null)
        {
            var text = detail is null ? name : $"{name}: {detail}";
            openTools[id] = text.Length > 200 ? text[..197] + "..." : text;
        }
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
            if (Str(part, "type") == "text") sb.Append(Str(part, "text"));
        return sb.ToString();
    }

    private static IEnumerable<string> ReadTailLines(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n == 0) break;
                read += n;
            }
            text = Encoding.UTF8.GetString(buf, 0, read);
            if (start > 0)
            {
                // The first line is probably cut in half; drop it.
                int nl = text.IndexOf('\n');
                text = nl >= 0 ? text[(nl + 1)..] : "";
            }
        }
        foreach (var line in text.Split('\n'))
            if (line.Length > 1) yield return line;
    }

    private static readonly string TempDir = Path.GetFullPath(Path.GetTempPath());

    private static bool IsScratch(string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
            return full.StartsWith(TempDir, StringComparison.OrdinalIgnoreCase)
                   || full.Contains(@"\scratchpad\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static string Relative(string file, string cwd)
    {
        try
        {
            if (!string.IsNullOrEmpty(cwd) && file.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(cwd, file);
        }
        catch (Exception) { /* not a path we can shorten */ }
        return file;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Shorten long text for a digest, keeping the end, where conclusions usually are.</summary>
    public static string Clip(string text, int max, bool keepEnd)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        return keepEnd ? "…" + text[^(max - 1)..] : text[..(max - 1)] + "…";
    }
}
