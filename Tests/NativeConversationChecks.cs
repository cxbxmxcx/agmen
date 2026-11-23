using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Services;

namespace Agmen.Tests;

/// <summary>Opt-in WPF/ConPTY checks using local sleeping processes in place of agent CLIs.</summary>
internal static class NativeConversationChecks
{
    internal static void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Load the actual workspace styles without starting the tray app, monitor or control pipe.
        using var theme = typeof(NativeConversationChecks).Assembly.GetManifestResourceStream("WorkspaceTheme.xaml")!;
        var root = XDocument.Load(theme).Root!;
        var resources = root.Elements().Single();
        resources.Name = root.Name.Namespace + "ResourceDictionary";
        resources.SetAttributeValue(XNamespace.Xmlns + "x", root.GetNamespaceOfPrefix("x")!.NamespaceName);
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        ExceptionDispatchInfo? failure = null;
        app.Dispatcher.InvokeAsync(async () =>
        {
            WorkspaceWindow? workspace = null;
            try
            {
                workspace = new WorkspaceWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                workspace.Show();
                await Exercise(workspace);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { workspace?.ShutdownTabs(); app.Shutdown(); }
        });
        app.Run();
        failure?.Throw();
    }

    private static async Task Exercise(WorkspaceWindow ws)
    {
        string cwd = AppPaths.DataDir;
        var root = new HostedTab(cwd, null, knownTitle: "parent");
        ws.AddTab(root, false);
        var original = WorkspaceWindow.NewChildTab(root, AgentKind.Claude, cwd, "design", null, null, null, null);
        var claudePath = Path.Combine(cwd, "claude.jsonl");
        File.WriteAllText(claudePath, """{"type":"user","message":{"content":"original decision"}}""");
        var originalHistory = ConversationHistory.Capture(null, AgentKind.Claude, claudePath);
        var kimi = WorkspaceWindow.NewContinuation(original, AgentKind.Kimi, originalHistory);
        var kimiPath = Path.Combine(cwd, "kimi.jsonl");
        Guid? Branch(string marker)
        {
            File.WriteAllText(kimiPath, JsonSerializer.Serialize(new
            {
                type = "agent.message.appended", message = new { message = new { role = "assistant", content = marker } }
            }));
            return ConversationHistory.Capture(kimi.HandoffId, AgentKind.Kimi, kimiPath);
        }
        var design = WorkspaceWindow.NewContinuation(kimi, AgentKind.Claude, Branch("private design"));
        var review = WorkspaceWindow.NewChildTab(root, AgentKind.Kimi, cwd, "review", null, null, null, null,
            Branch("private review"));
        string sleeper = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")
            + " -NoProfile -NonInteractive -Command Start-Sleep -Seconds 60";
        foreach (var tab in new[] { design, review })
        {
            tab.StartTerminal(sleeper);
            var starting = ws.PublishWhenRunningAsync(tab, () => ws.AddTab(tab, select: tab == design), () => { });
            while (tab.CurrentPid == 0 && !starting.IsCompleted) await Task.Delay(10);
            var role = await ws.Orchestrator.HandleAsync("agmen:role", new(), [tab.CurrentPid]);
            await starting;
            Check((string?)role["role"] == "tab", "The staged process lost its first MCP role request.");
        }

        async Task<string> Call(HostedTab caller, string tool, JsonObject args)
        {
            var result = await ws.Orchestrator.HandleAsync(tool, args, [caller.CurrentPid]);
            Check((bool?)result["ok"] == true, $"{tool} failed: {result}");
            return result["text"]!.GetValue<string>();
        }
        async Task<string> Read(HostedTab caller) => JsonNode.Parse(await Call(caller, "read_handoff", new()))!["text"]!.GetValue<string>();
        var designHistory = await Read(design);
        var reviewHistory = await Read(review);
        Check(designHistory.Contains("original decision") && designHistory.Contains("private design") && !designHistory.Contains("private review"),
            "The Claude to Kimi to Claude continuation lost its history or crossed into its sibling's branch.");
        Check(reviewHistory.Contains("original decision") && reviewHistory.Contains("private review") && !reviewHistory.Contains("private design"),
            "The second process did not receive its own fork.");

        await Call(design, "message_sibling", new() { ["sibling"] = review.Id.ToString(), ["message"] = "Review the two edge cases." });
        // Inspect the actual queued envelope without delivering keystrokes to a sleeping process.
        var outbox = (List<OutboxMessage>)typeof(HostedTab).GetField("_outbox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(review)!;
        Check(outbox.Single().Text == "[From your sibling, design] Review the two edge cases." && design.QueuedCount == 0
            && root.QueuedCount == 0 && await Read(review) == reviewHistory, "Sibling sharing sent more than the selected excerpt.");

        int originalPid = design.CurrentPid;
        var failed = WorkspaceWindow.NewContinuation(design, AgentKind.Kimi, design.HandoffId);
        failed.StartTerminal(Path.Combine(Environment.SystemDirectory, "cmd.exe") + " /d /c exit 9");
        try
        {
            await ws.PublishWhenRunningAsync(failed, () => ws.ReplaceTab(design, failed), () => { });
            throw new Exception("An immediately exiting process replaced the source.");
        }
        catch (InvalidOperationException) when (failed.IsEnded) { }
        design.Poll();
        Check(ws.SelectedTab == design && design.IsRunning && design.CurrentPid == originalPid && await Read(design) == designHistory,
            "A failed replacement lost the original process, selection or history.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
