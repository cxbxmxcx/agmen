using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Agmen.Agents;
using Agmen.Control;
using Agmen.Services;

namespace Agmen.Tests;

internal static class WorkspaceRoutingChecks
{
    internal static void Run()
    {
        // Use a real helper under a test host with the same process name as the production GUI.
        var binaries = AppContext.BaseDirectory;
        var folder = Directory.CreateDirectory(AppPaths.PathOf("routing-host")).FullName;
        foreach (string file in Directory.EnumerateFiles(binaries))
        {
            if (Path.GetExtension(file) is ".dll" or ".json")
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
        var hostExe = Path.Combine(folder, "Agmen.exe");
        File.Copy(Path.Combine(binaries, "Agmen.Tests.exe"), hostExe);
        var pinned = ToolServer.Entry();
        if (pinned.ContainsKey("env")) throw new Exception("The global agent registration pins one workspace.");
        pinned["env"] = new JsonObject { ["AGMEN_INSTANCE"] = "an-earlier-name" };
        if (!AgentConnect.UsesThisInstallation(pinned)) throw new Exception("An old registration no longer recognizes the same executable.");
        pinned["command"] = hostExe;
        if (AgentConnect.UsesThisInstallation(pinned)) throw new Exception("A different executable was treated as this installation.");

        string registration = Path.Combine(folder, "mcp-fixture.json");
        var config = new JsonObject { ["mcpServers"] = new JsonObject { ["agmen"] = pinned } };
        pinned["command"] = Environment.ProcessPath;
        File.WriteAllText(registration, config.ToJsonString());
        if (AgentConnect.RegistrationState(false, registration, _ => false) != AgentConnect.State.RegistrationRemaining)
            throw new Exception("Disconnect must be available for this installation after the provider CLI is removed.");
        if (AgentConnect.RegistrationState(true, registration, _ => false) != AgentConnect.State.Incomplete)
            throw new Exception("A missing agent file, grant or hook must allow both repair and removal.");
        pinned["args"] = new JsonArray("--outdated");
        File.WriteAllText(registration, config.ToJsonString());
        if (AgentConnect.RegistrationState(true, registration, _ => true) != AgentConnect.State.Incomplete)
            throw new Exception("Outdated local MCP arguments must allow both repair and removal.");
        pinned["command"] = hostExe;
        File.WriteAllText(registration, config.ToJsonString());
        if (AgentConnect.RegistrationState(false, registration, _ => false) != AgentConnect.State.NotInstalled)
            throw new Exception("An absent provider's registration for another installation must be left alone.");

        int wrongWorkspaceCalls = 0;
        using var outer = new ControlServer((tool, args, caller) =>
        {
            Interlocked.Increment(ref wrongWorkspaceCalls);
            return Task.FromResult(new JsonObject { ["ok"] = false });
        });
        outer.Start();
        var start = StartInfo(hostExe, "--routing-host", Path.Combine(binaries, "Agmen.exe"));
        start.Environment["AGMEN_INSTANCE"] = AppPaths.FamilyName;
        start.Environment["AGMEN_PROBE"] = "1";
        start.Environment.Remove("AGMEN_WORKSPACE_NAME");
        start.Environment.Remove("AGMEN_WORKSPACE_FAMILY");
        using var host = Process.Start(start)!;
        host.StandardInput.Close();
        var result = Finish(host);
        if (result.Trim() != "routing passed" || wrongWorkspaceCalls != 0)
            throw new Exception("The MCP server or hook reached the wrong workspace.");
        var outerProbe = Path.Combine(AppPaths.DataDir, "probe.jsonl");
        if (File.Exists(outerProbe) && File.ReadAllText(outerProbe).Contains("routing-test-signal"))
            throw new Exception("Hook diagnostics were recorded in the wrong workspace.");
    }

    internal static void RunHost(string helper)
    {
        string pinnedFamily = AppPaths.FamilyName;
        if (!pinnedFamily.StartsWith("Agmen-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("This check requires an isolated test instance.");
        AppPaths.SelectWorkspace("Routing fixture");
        int hookCalls = 0;
        int childPid = 0;
        using var server = new ControlServer((tool, args, caller) =>
        {
            if (caller.Count != 1 || caller[0] != Volatile.Read(ref childPid)) throw new Exception("Wrong Windows client process.");
            if (tool == "agmen:hook") Interlocked.Increment(ref hookCalls);
            return Task.FromResult(new JsonObject { ["ok"] = true, ["role"] = "tab", ["text"] = "the named workspace" });
        });
        server.Start();
        using (var client = StartHelper("--mcp"))
        {
            client.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            client.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"whoami","arguments":{}}}""");
            client.StandardInput.Close();
            if (!Finish(client).Contains("the named workspace")) throw new Exception("The MCP tool did not reach its host.");
        }
        using (var hook = StartHelper("--hook", "agy", "SessionStart"))
        {
            hook.StandardInput.WriteLine("""{"diagnostic":"routing-test-signal"}""");
            hook.StandardInput.Close();
            Finish(hook);
        }
        if (hookCalls != 1) throw new Exception("The hook did not reach its host.");
        if (!File.ReadAllText(AppPaths.PathOf("probe.jsonl")).Contains("routing-test-signal"))
            throw new Exception("The owning workspace did not record its hook diagnostic.");
        Console.WriteLine("routing passed");

        Process StartHelper(params string[] args)
        {
            var start = StartInfo(helper, args);
            // Model a registration made by the default workspace before multi-workspace support.
            start.Environment["AGMEN_INSTANCE"] = pinnedFamily;
            start.Environment.Remove("AGMEN_WORKSPACE_FAMILY");
            start.Environment.Remove("AGMEN_WORKSPACE_NAME");
            var child = Process.Start(start)!;
            Volatile.Write(ref childPid, child.Id);
            return child;
        }
    }

    private static ProcessStartInfo StartInfo(string exe, params string[] args)
    {
        var result = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) result.ArgumentList.Add(arg);
        return result;
    }

    private static string Finish(Process process)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The routing fixture did not exit.");
        }
        if (process.ExitCode != 0) throw new Exception(stderr.GetAwaiter().GetResult());
        return stdout.GetAwaiter().GetResult();
    }
}
