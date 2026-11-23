using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agmen.Agents;

/// <summary>Project native records onto the public conversation, one JSONL record at a time.</summary>
internal sealed class PortableTranscript(TextWriter output)
{
    private const string HandoffReference = "[page of inherited handoff history]";
    private readonly HashSet<string> handoffCalls = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> claudeRows = [];
    private bool antigravityReadHandoff;

    public static void Write(AgentKind agent, Stream source, long remaining, TextWriter output)
    {
        var projection = new PortableTranscript(output);
        var buffer = new byte[65536];
        using var record = new MemoryStream();
        bool first = true;
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read == 0) throw new EndOfStreamException("Native transcript was shortened during capture.");
            remaining -= read;
            int start = 0;
            while (start < read)
            {
                int newline = Array.IndexOf(buffer, (byte)'\n', start, read - start);
                if (newline < 0)
                {
                    record.Write(buffer, start, read - start);
                    break;
                }
                record.Write(buffer, start, newline - start);
                projection.WriteRecord(agent, record, first, final: false);
                first = false;
                record.SetLength(0);
                start = newline + 1;
            }
        }
        if (record.Length > 0) projection.WriteRecord(agent, record, first, final: true);
    }

    private void WriteRecord(AgentKind agent, MemoryStream record, bool first, bool final)
    {
        var bytes = record.GetBuffer().AsMemory(0, (int)record.Length);
        if (first && bytes.Span.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
        if (bytes.Span.IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r') < 0) return;
        JsonDocument json;
        try { json = JsonDocument.Parse(bytes); }
        catch (JsonException e)
        {
            // A producer can be midway through its last JSON record. Distinguish a valid prefix
            // from corrupt syntax, so an unreadable transcript cannot become an empty handoff.
            if (final)
            {
                try
                {
                    var reader = new Utf8JsonReader(bytes.Span, isFinalBlock: false, state: default);
                    while (reader.Read()) { }
                    return;
                }
                catch (JsonException) { }
            }
            throw new InvalidDataException("The native transcript contains an unreadable record.", e);
        }
        using (json)
        {
            var row = json.RootElement;
            switch (agent)
            {
                case AgentKind.Claude: Claude(row); break;
                case AgentKind.Codex: Codex(row); break;
                case AgentKind.Kimi: Kimi(row); break;
                case AgentKind.Antigravity: Antigravity(row); break;
                default: throw new ArgumentOutOfRangeException(nameof(agent));
            }
        }
    }

    private static JsonElement Field(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value : default;

    private static string? Str(JsonElement item, string key) =>
        Field(item, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private void Entry(string role, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        output.Write(role);
        output.Write(": ");
        output.Write(text);
        output.Write('\n');
    }

    private static readonly Regex Reminder = new(@"<system-reminder>[\s\S]*?(?:</system-reminder>|$)", RegexOptions.Compiled);
    private static readonly Regex CodexContext = new(@"\G\s*<(environment_context|codex_internal_context|permissions instructions|collaboration_mode|skills_instructions|subagent_notification|turn_aborted)(?:\s[^>]*)?>[\s\S]*?(?:</\1>|$)", RegexOptions.Compiled);
    private static readonly Regex PeerMessage = new(@"<cross-session-message\b[^>]*>([\s\S]*?)(?:</cross-session-message>|$)", RegexOptions.Compiled);

    private void Claude(JsonElement row)
    {
        var role = Str(row, "type");
        if (role is not ("user" or "assistant") || Field(row, "isSidechain").ValueKind == JsonValueKind.True
            || Field(row, "isCompactSummary").ValueKind == JsonValueKind.True) return;
        // Claude can replay a row when rebuilding context. Distinct rows in one assistant turn
        // can share message.id, so deduplicate only by the transcript row's UUID.
        if (Guid.TryParse(Str(row, "uuid"), out var rowId) && !claudeRows.Add(rowId)) return;
        var content = Field(Field(row, "message"), "content");
        if (Field(row, "isMeta").ValueKind == JsonValueKind.True)
        {
            if (role == "user" && PeerMessage.Match(Parts(content)) is { Success: true } peer)
                Entry("Message from another session", peer.Groups[1].Value);
            return;
        }
        var label = role == "user" ? "User" : "Claude";
        if (content.ValueKind != JsonValueKind.Array)
        {
            Entry(label, Reminder.Replace(Parts(content), ""));
            return;
        }
        foreach (var part in content.EnumerateArray())
        {
            switch (Str(part, "type"))
            {
                case "tool_use" or "server_tool_use":
                    ToolCall(Str(part, "name"), Str(part, "id"), Field(part, "input"));
                    break;
                case "tool_result":
                    ToolResult(Str(part, "tool_use_id"), Reminder.Replace(Parts(Field(part, "content")), ""),
                        Field(part, "is_error").ValueKind == JsonValueKind.True);
                    break;
                default:
                    Entry(label, Reminder.Replace(Part(part) ?? "", ""));
                    break;
            }
        }
    }

    private void Codex(JsonElement row)
    {
        if (Str(row, "type") != "response_item") return; // event_msg mirrors response_item messages.
        var item = Field(row, "payload");
        if (Str(item, "channel") == "analysis") return;
        var id = Str(item, "call_id") ?? Str(item, "id");
        switch (Str(item, "type"))
        {
            case "message" when Str(item, "role") is "user" or "assistant":
                var role = Str(item, "role");
                var content = Field(item, "content");
                if (role == "assistant") Entry("Codex", Parts(content));
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray()) CodexUser(Part(part));
                else CodexUser(Parts(content));
                break;
            case "function_call":
                var name = Str(item, "name");
                if (Str(item, "namespace") is { Length: > 0 } ns) name = ns + "." + name;
                ToolCall(name, id, Field(item, "arguments"));
                break;
            case "custom_tool_call": ToolCall(Str(item, "name"), id, Field(item, "input")); break;
            case "local_shell_call" or "web_search_call": ToolCall(Str(item, "type"), id, Field(item, "action")); break;
            case "function_call_output" or "custom_tool_call_output":
                var value = Field(item, "output");
                ToolResult(id, value.ValueKind == JsonValueKind.Object ? Details(value) : Parts(value));
                break;
        }
    }

    private void CodexUser(string? text)
    {
        if (text is null || text.StartsWith("# AGENTS.md instructions for ", StringComparison.Ordinal)) return;
        Entry("User", CodexContext.Replace(text, ""));
    }

    private void Kimi(JsonElement row)
    {
        if (Str(row, "type") != "agent.message.appended") return;
        var envelope = Field(row, "message");
        var message = Field(envelope, "message");
        var meta = Field(envelope, "meta");
        var content = Field(message, "content");
        switch (Str(message, "role"))
        {
            case "user" when Str(Field(meta, "origin"), "kind") != "system_trigger":
                Entry("User", Parts(content));
                break;
            case "assistant":
                Entry("Kimi", Parts(content));
                if (Field(message, "toolCalls") is { ValueKind: JsonValueKind.Array } calls)
                    foreach (var call in calls.EnumerateArray())
                        ToolCall(Str(call, "name"), Str(call, "id"), Field(call, "arguments"));
                break;
            case "tool": ToolResult(Str(message, "toolCallId"), Parts(content)); break;
        }
    }

    private static readonly Regex UserRequest = new(@"<USER_REQUEST>([\s\S]*?)</USER_REQUEST>", RegexOptions.Compiled);
    private static readonly Regex HandoffPage = new("""\{\s*"offset"\s*:\s*\d+\s*,\s*"next_offset"\s*:\s*\d+\s*,\s*"end"\s*:\s*(true|false)\s*,\s*"text"\s*:""", RegexOptions.Compiled);

    private void Antigravity(JsonElement row)
    {
        var text = Str(row, "content");
        switch (Str(row, "type"))
        {
            case "USER_INPUT":
                var request = UserRequest.Match(text ?? "");
                Entry("User", request.Success ? request.Groups[1].Value.Trim() : text);
                break;
            case "PLANNER_RESPONSE":
                Entry("Antigravity", text);
                antigravityReadHandoff = false;
                if (Field(row, "tool_calls") is { ValueKind: JsonValueKind.Array } calls)
                    foreach (var call in calls.EnumerateArray())
                    {
                        var args = Field(call, "args");
                        var name = Str(call, "name");
                        antigravityReadHandoff |= IsHandoffRead(name, args);
                        ToolCall(name, Str(call, "id"), args);
                    }
                break;
            case "GENERIC" when Str(row, "source") is null or "MODEL":
                // Generic steps lack call IDs. Match the page fields as well as the preceding batch.
                if (antigravityReadHandoff && text?.Contains("next_offset", StringComparison.Ordinal) == true
                    && HandoffPage.IsMatch(text.Replace("\\\"", "\"")))
                    text = HandoffReference;
                ToolResult(Str(row, "call_id"), text, Field(row, "error").ValueKind == JsonValueKind.True);
                break;
        }
    }

    private static bool IsHandoffRead(string? name, JsonElement args)
        => name is "read_handoff" or "mcp__agmen__read_handoff" or "mcp__agmen.read_handoff" or "agmen.read_handoff"
           || name == "call_mcp_tool" && Str(args, "ServerName") == "agmen" && Str(args, "ToolName") == "read_handoff";

    private void ToolCall(string? name, string? id, JsonElement args)
    {
        if (id is not null && IsHandoffRead(name, args)) handoffCalls.Add(id);
        Entry("Tool call" + (id is null ? "" : " " + id), (name ?? "tool") + " " + Details(args));
    }

    private void ToolResult(string? id, string? text, bool error = false)
    {
        if (id is not null && handoffCalls.Remove(id) && !error) text = HandoffReference;
        Entry((error ? "Tool error" : "Tool result") + (id is null ? "" : " " + id),
            string.IsNullOrEmpty(text) ? "[empty]" : text);
    }

    private static string Parts(JsonElement parts) => parts.ValueKind switch
    {
        JsonValueKind.String => parts.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n", parts.EnumerateArray().Select(Part).Where(t => t is { Length: > 0 })),
        _ => Part(parts) ?? ""
    };

    private static string? Part(JsonElement part) => Str(part, "type") switch
    {
        "text" or "input_text" or "output_text" => Str(part, "text"),
        var kind => Attachment(kind)
    };

    private static string? Attachment(string? kind) => kind switch
    {
        "image" or "image_url" or "input_image" or "output_image" => "[image]",
        "document" or "file" or "input_file" or "resource" => "[attachment]",
        "audio" or "audio_url" or "input_audio" => "[audio]",
        "video" or "video_url" => "[video]",
        _ => null
    };

    // Preserve public tool arguments, including structured inputs, without copying a provider's envelope.
    private static string Details(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "";
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text.AsSpan().TrimStart() is not ['{' or '[', ..]) return text;
            try
            {
                using var nested = JsonDocument.Parse(text);
                return Details(nested.RootElement);
            }
            catch (JsonException) { return text; }
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteValue(writer, value);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static readonly HashSet<string> PrivateFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "apiKey", "api_key", "x-api-key", "access_token", "refresh_token", "password",
        "cookie", "set-cookie", "thinking", "reasoning", "encrypted_content"
    };

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        if (Str(value, "type") is "reasoning" or "thinking" or "redacted_thinking" or "encrypted_content")
            writer.WriteStringValue("[private reasoning omitted]");
        else if (Attachment(Str(value, "type")) is { } marker) writer.WriteStringValue(marker);
        else if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject())
            {
                if (PrivateFields.Contains(field.Name)) continue;
                writer.WritePropertyName(field.Name);
                WriteValue(writer, field.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteValue(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
