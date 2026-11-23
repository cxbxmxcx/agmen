using System.IO;
using System.Text.Json;
using Agmen.Agents;
using Agmen.Services;

namespace Agmen.Tests;

internal static class CodexIdentityChecks
{
    internal static void Run()
    {
        string folder = Directory.CreateDirectory(AppPaths.PathOf("codex-identity")).FullName;
        var tab = new AgentTabRef(Guid.NewGuid(), AgentKind.Codex, folder, 1234, DateTime.Now, null);
        string other = Record(folder);
        string own = Record(folder);
        string wrongFolder = Record(Path.GetTempPath());
        string wrongId = Record(folder, Guid.NewGuid().ToString());
        string subagent = Record(folder, source: new { subagent = "review" });
        string[] candidates = [other, wrongFolder, wrongId, subagent, own];

        var id = AgentMonitor.OwnedCodexRollout(tab, candidates, path => path != other);
        if (id?.Path != own) throw new Exception("First-turn Codex capture picked the wrong rollout.");
        if (AgentMonitor.OwnedCodexRollout(tab, candidates, _ => true) != null)
            throw new Exception("Ambiguous Codex ownership must wait for the native notify hook.");
        if (AgentMonitor.OwnedCodexRollout(tab, candidates, path => path == own ? true : null) != null)
            throw new Exception("A missing ownership result for a plausible rollout must wait for notify.");

        ProcessTree.Node[] launcher = [new(1236, "codex"), new(tab.Pid, "node")];
        if (!AgentMonitor.CodexWriterChain(launcher, tab.Pid)
            || AgentMonitor.CodexWriterChain([new(1237, "powershell"), .. launcher], tab.Pid)
            || AgentMonitor.CodexWriterChain([new(1237, "codex"), .. launcher], tab.Pid)
            || AgentMonitor.CodexWriterChain(launcher, 555))
            throw new Exception("Only the native Codex process launched by this terminal can establish first-turn identity.");

        using var held = new FileStream(own, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (WindowsFileOwners.Get(own)?.Contains(Environment.ProcessId) != true)
            throw new Exception("Windows did not identify the process holding the synthetic rollout.");

        string Record(string cwd, string? differentId = null, object? source = null)
        {
            string native = Guid.NewGuid().ToString();
            var file = Path.Combine(folder, $"rollout-test-{native}.jsonl");
            var row = new { type = "session_meta", payload = new { id = differentId ?? native, cwd,
                timestamp = DateTimeOffset.Now, source = source ?? "cli" } };
            File.WriteAllText(file, JsonSerializer.Serialize(row) + "\n");
            return file;
        }
    }
}
