using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agmen.Orchestration;

namespace Agmen.Agents;

/// <summary>
/// Reads the recent conversation of a Codex, Kimi Code or Antigravity session for read_session,
/// from the same files <see cref="AgentMonitor"/> follows: Codex's session file, Kimi's
/// wire.jsonl, and Antigravity's transcript.jsonl.
/// </summary>
public static class AgentTranscripts
{
    /// <summary>The latest prompts and replies, and what the session waits for the user to approve, if anything.</summary>
    public sealed record Digest(List<TranscriptDigest.Message> Messages, string? Waiting);

    private const int TailBytes = 4 * 1024 * 1024;
    private static readonly Regex UserRequest = new(@"<USER_REQUEST>\s*(.*?)\s*</USER_REQUEST>", RegexOptions.Singleline | RegexOptions.Compiled);

    public static Digest Read(AgentKind agent, string path, int maxMessages)
    {
        var messages = new List<TranscriptDigest.Message>();
        string? waiting = null;
        var label = AgentProfiles.ShortName(agent).ToLowerInvariant();
        foreach (var line in TailLines(path))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                switch (agent)
                {
                    case AgentKind.Codex: ReadCodex(r, messages, label); break;
                    case AgentKind.Kimi: waiting = ReadKimi(r, messages, label, waiting); break;
                    case AgentKind.Antigravity: ReadAntigravity(r, messages, label); break;
                }
            }
        }
        return new Digest(messages.TakeLast(Math.Clamp(maxMessages, 1, 20)).ToList(), waiting);
    }

    private static void ReadCodex(JsonElement r, List<TranscriptDigest.Message> messages, string label)
    {
        if (Str(r, "type") != "response_item" || !r.TryGetProperty("payload", out var p) || Str(p, "type") != "message") return;
        var role = Str(p, "role");
        if (role is not ("user" or "assistant")) return;
        var text = TextOf(p);
        // Codex adds its own context to the conversation as tagged user messages; they aren't the user's.
        if (string.IsNullOrWhiteSpace(text) || (role == "user" && text.TrimStart().StartsWith('<'))) return;
        messages.Add(new TranscriptDigest.Message(role == "user" ? "user" : label, Time(Str(r, "timestamp")), text.Trim()));
    }

    private static string? ReadKimi(JsonElement r, List<TranscriptDigest.Message> messages, string label, string? waiting)
    {
        DateTimeOffset? at = r.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        switch (Str(r, "type"))
        {
            case "agent.message.appended" when r.TryGetProperty("message", out var outer) && outer.TryGetProperty("message", out var m):
                var role = Str(m, "role");
                if (role is ("user" or "assistant") && TextOf(m) is { Length: > 0 } text)
                    messages.Add(new TranscriptDigest.Message(role == "user" ? "user" : label, at, text.Trim()));
                return waiting;
            case "interaction.request" when r.TryGetProperty("request", out var req):
                var display = req.TryGetProperty("display", out var d) ? Str(d, "summary") : null;
                return display ?? Str(req, "toolName") ?? "an action";
            case "interaction.resolved" or "turn.ended":
                return null;
            default:
                return waiting;
        }
    }

    private static void ReadAntigravity(JsonElement r, List<TranscriptDigest.Message> messages, string label)
    {
        var content = Str(r, "content");
        if (string.IsNullOrWhiteSpace(content)) return;
        var at = Time(Str(r, "created_at"));
        switch (Str(r, "type"))
        {
            case "USER_INPUT":
                // The user's words sit in a <USER_REQUEST> block, followed by metadata Antigravity adds.
                var match = UserRequest.Match(content);
                messages.Add(new TranscriptDigest.Message("user", at, (match.Success ? match.Groups[1].Value : content).Trim()));
                break;
            case "PLANNER_RESPONSE":
                messages.Add(new TranscriptDigest.Message(label, at, content.Trim()));
                break;
        }
    }

    /// <summary>A message's text, whether its content is a string or a list of text parts.</summary>
    public static string? TextOf(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var c)) return null;
        if (c.ValueKind == JsonValueKind.String) return c.GetString();
        if (c.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
            if (Str(part, "text") is { Length: > 0 } text) sb.Append(text).Append('\n');
        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> TailLines(string path)
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
                int nl = text.IndexOf('\n');
                text = nl >= 0 ? text[(nl + 1)..] : "";
            }
        }
        foreach (var line in text.Split('\n'))
            if (line.Length > 1) yield return line;
    }

    private static DateTimeOffset? Time(string? iso) => DateTimeOffset.TryParse(iso, out var at) ? at : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
