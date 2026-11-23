using System.IO;
using System.Text;
using System.Text.Json;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Services;

namespace Agmen.Tests;

internal static class HistoryChecks
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("AGMEN_INSTANCE") != AppPaths.InstanceName
            || AppPaths.InstanceName is "Agmen" or "Agmen-dev")
            throw new InvalidOperationException("Run the checks in a dedicated AGMEN_INSTANCE.");

        var fixtures = Directory.CreateTempSubdirectory("agmen-history-");
        try
        {
            var native = Path.Combine(fixtures.FullName, "native.jsonl");
            File.WriteAllText(native, """
                {"type":"user","message":{"content":"first 🐈 turn"}}
                {"type":"user","uuid":"c68dfaca-4eae-4357-a878-b956a437fafe","message":{"content":"replayed prompt"}}
                {"type":"user","uuid":"c68dfaca-4eae-4357-a878-b956a437fafe","message":{"content":"replayed prompt"}}
                {"type":"user","isCompactSummary":true,"message":{"content":"PRIVATE synthetic compact summary"}}
                {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"PRIVATE"},{"type":"text","text":"public answer"},{"type":"tool_use","name":"Read","id":"c1","input":{"file_path":"a.cs","headers":{"Authorization":"PRIVATE"}}}]}}
                {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"c1","content":[{"type":"text","text":"file contents"},{"type":"image","source":{"data":"PRIVATE"}}]}]}}
                {"type":"user","message":{"content":"<system-reminder>PRIVATE</system-reminder>kept"}}
                {"type":"system","content":"PRIVATE"}
                {"type":"user","isSidechain":true,"message":{"content":"PRIVATE"}}

                """);
            var first = ConversationHistory.Capture(null, AgentKind.Claude, native);
            var original = All(first);
            Contains(original, "first 🐈 turn", "public answer", "Read", "a.cs", "c1", "file contents", "[image]", "kept");
            Expect(!original.Contains("PRIVATE"), "hidden Claude records or binary data exposed");
            Expect(original.Split("replayed prompt").Length == 2, "Claude replayed a native row twice");
            File.AppendAllText(native, """{"type":"user","message":{"content":"late parent turn"}}""");
            Expect(All(first, 1) == original, "the snapshot changed or split a Unicode scalar");

            File.WriteAllText(native, """
                {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<real-xml/>"},{"type":"input_image","image_url":"PRIVATE"}]}}
                {"type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"PRIVATE"}]}}
                {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>PRIVATE</environment_context>\n<codex_internal_context>PRIVATE</codex_internal_context>actual question"}]}}
                {"type":"response_item","payload":{"type":"message","role":"assistant","channel":"analysis","content":[{"type":"output_text","text":"PRIVATE"}]}}
                {"type":"response_item","payload":{"type":"reasoning","summary":[{"text":"PRIVATE"}]}}
                {"type":"event_msg","payload":{"type":"user_message","message":"<real-xml/>"}}
                {"type":"response_item","payload":{"type":"function_call","name":"run","call_id":"r1","arguments":"{\"cmd\":\"show work\"}"}}
                {"type":"response_item","payload":{"type":"function_call_output","call_id":"r1","output":"codex output"}}
                {"type":"response_item","payload":{"type":"custom_tool_call","name":"patch","call_id":"r2","input":"patch body"}}
                {"type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"r2","output":"applied"}}
                {"type":"response_item","payload":{"type":"function_call","name":"mcp__agmen__read_handoff","call_id":"r3","arguments":"{}"}}
                {"type":"response_item","payload":{"type":"function_call_output","call_id":"r3","output":"REPEATED inherited history"}}
                {"type":"response_item","payload":{"type":"function_call","namespace":"mcp__different","name":"read_handoff","call_id":"r4","arguments":"{}"}}
                {"type":"response_item","payload":{"type":"function_call_output","call_id":"r4","output":"another server output"}}
                {"type":"response_item","payload":{"type":"function_call_output","call_id":"r5","output":{"items":[{"type":"reasoning","summary":[{"text":"PRIVATE"}]}]}}}
                {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"codex reply"}]}}
                {"type":"response_item","payload":
                """);
            var second = ConversationHistory.Capture(first, AgentKind.Codex, native);
            var codex = All(second);
            Expect(codex.StartsWith(original, StringComparison.Ordinal), "ancestral history lost on a second capture");
            Contains(codex, "r1", "show work", "codex output", "patch body", "applied", "codex reply", "actual question", "another server output", "[image]");
            Expect(codex.Split("<real-xml/>").Length == 2 && !codex.Contains("PRIVATE") && !codex.Contains("late parent turn"),
                "Codex filtering, deduplication, or the fork boundary failed");
            Expect(codex.Contains("mcp__agmen__read_handoff") && !codex.Contains("REPEATED"), "handoff page recursively copied");

            File.WriteAllText(native, """
                {"type":"agent.message.appended","message":{"message":{"role":"user","content":[{"type":"text","text":"kimi prompt"}]}}}
                {"type":"agent.message.appended","message":{"message":{"role":"assistant","content":[{"type":"think","think":"PRIVATE"},{"type":"text","text":"kimi reply"}],"toolCalls":[{"name":"Shell","id":"k1","arguments":"list items"}]}}}
                {"type":"agent.message.appended","message":{"message":{"role":"tool","toolCallId":"k1","content":[{"type":"text","text":"kimi result"},{"type":"image_url","imageUrl":"PRIVATE"}]}}}
                """);
            var kimi = All(ConversationHistory.Capture(null, AgentKind.Kimi, native));
            Contains(kimi, "kimi prompt", "kimi reply", "Shell", "k1", "list items", "kimi result", "[image]");
            Expect(!kimi.Contains("PRIVATE"), "Kimi private data exposed");

            File.WriteAllText(native, """
                {"type":"USER_INPUT","content":"<USER_REQUEST>agy prompt</USER_REQUEST><ADDITIONAL_METADATA>PRIVATE</ADDITIONAL_METADATA>"}
                {"type":"PLANNER_RESPONSE","thinking":"PRIVATE","content":"agy reply","tool_calls":[{"name":"read","args":{"AbsolutePath":"path.md"}}]}
                {"type":"GENERIC","source":"MODEL","content":"agy result"}
                {"type":"PLANNER_RESPONSE","tool_calls":[{"name":"call_mcp_tool","args":{"ServerName":"agmen","ToolName":"read_handoff"}}]}
                {"type":"GENERIC","source":"MODEL","content":"{\"offset\":0,\"next_offset\":1,\"end\":true,\"text\":\"REPEATED\"}"}
                {"type":"GENERIC","source":"MODEL","content":"The formatter renders next_offset as text."}
                {"type":"SYSTEM_MESSAGE","content":"PRIVATE"}
                """);
            var agy = All(ConversationHistory.Capture(null, AgentKind.Antigravity, native));
            Contains(agy, "agy prompt", "agy reply", "read", "path.md", "agy result", "formatter renders");
            Expect(!agy.Contains("PRIVATE") && !agy.Contains("REPEATED"), "Antigravity private data or inherited page exposed");

            File.WriteAllText(native, """{"type":"user","message":{"content":"early marker"}}""" + "\n"
                + JsonSerializer.Serialize(new { type = "system", content = new string('x', 4 * 1024 * 1024) }) + "\n"
                + JsonSerializer.Serialize(new { type = "user", message = new { content = new string('y', 32_000) + "late marker" } }));
            var large = ConversationHistory.Capture(null, AgentKind.Claude, native);
            var largeText = All(large, int.MaxValue);
            Contains(largeText, "early marker", "late marker");

            File.Delete(native);
            Expect(All(second) == codex, "reopening the snapshot depended on the source file");
            Expect(ConversationHistory.Capture(first, AgentKind.Claude, null) == first, "empty fork lost its ancestry");
            ConversationHistory.DiscardCapture(first, first);
            Expect(All(first) == original, "discard removed shared ancestry");
            ConversationHistory.DiscardCapture(second, first);
            ExpectThrows(() => All(second), "unpublished snapshot was left on disk");
            File.WriteAllText(native, """{"type":"user","message":{"content":"round 2"}}""");
            var middle = ConversationHistory.Capture(first, AgentKind.Claude, native);
            var newest = ConversationHistory.Capture(middle, AgentKind.Claude, native);
            // Simulate loss of an ancestor file before a subsequent capture.
            File.Delete(Directory.GetFiles(AppPaths.DataDir, first!.Value.ToString("N") + ".history", SearchOption.AllDirectories).Single());
            ExpectThrows(() => ConversationHistory.Capture(newest, AgentKind.Claude, native), "broken ancestry was propagated");
            File.Delete(native);
            Expect(All(null).Length == 0, "fresh conversation has history");
            ExpectThrows(() => ConversationHistory.Capture(null, AgentKind.Claude, native), "missing source was silently ignored");
            ExpectThrows(() => ConversationHistory.Capture(Guid.NewGuid(), AgentKind.Claude, null), "missing ancestry was silently ignored");
            File.WriteAllText(native, "{broken}\n" + """{"type":"user","message":{"content":"later"}}""");
            ExpectThrows(() => ConversationHistory.Capture(null, AgentKind.Claude, native), "malformed completed record was silently ignored");
            File.WriteAllText(native, "{broken}");
            ExpectThrows(() => ConversationHistory.Capture(null, AgentKind.Claude, native), "malformed final record was silently ignored");
        }
        finally { fixtures.Delete(true); }
    }

    private static string All(Guid? snapshot, int count = 29)
    {
        var text = new StringBuilder();
        for (int offset = 0; ; )
        {
            using var json = JsonDocument.Parse(ConversationHistory.Read(snapshot, offset, count));
            var page = json.RootElement;
            var part = page.GetProperty("text").GetString()!;
            var next = page.GetProperty("next_offset").GetInt32();
            Expect(page.GetProperty("offset").GetInt32() == offset && next == offset + part.Length, "invalid page cursor");
            Expect(part.Length <= 16384, "page exceeds size bound");
            Expect(part.Length == 0 || !char.IsLowSurrogate(part[0]) && !char.IsHighSurrogate(part[^1]), "split surrogate pair");
            text.Append(part);
            if (page.GetProperty("end").GetBoolean()) return text.ToString();
            Expect(next > offset, "cursor did not advance");
            offset = next;
        }
    }

    private static void Contains(string text, params string[] values)
    {
        foreach (var value in values) Expect(text.Contains(value, StringComparison.Ordinal), $"missing history content '{value}'");
    }

    private static void ExpectThrows(Action action, string failure)
    {
        try { action(); }
        catch (IOException) { return; }
        catch (InvalidDataException) { return; }
        throw new Exception(failure);
    }

    private static void Expect(bool condition, string failure)
    {
        if (!condition) throw new Exception(failure);
    }
}
