using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Services;

namespace Agmen.Tests;

/// <summary>Opt-in local CLI checks. No test presses Enter, grants workspace trust, or sends a model request.</summary>
internal static class RealProviderTerminalChecks
{
    internal static void Run()
    {
        var missing = AgentProfiles.All.Where(a => !AgentProfiles.IsInstalled(a)).Select(AgentProfiles.DisplayName).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("These provider CLIs must be installed: " + string.Join(", ", missing));
        Task.WhenAll(new[] { "Terminal A", "Terminal B" }.Select(CheckWorkspace)).GetAwaiter().GetResult();
    }

    private static async Task CheckWorkspace(string name)
    {
        string report = AppPaths.PathOf(name + ".txt");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Agmen.Tests.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["AGMEN_INSTANCE"] = AppPaths.FamilyName;
        start.Environment.Remove("AGMEN_WORKSPACE_NAME");
        start.Environment.Remove("AGMEN_WORKSPACE_FAMILY");
        foreach (string arg in new[] { "--real-provider-workspace", name, report }) start.ArgumentList.Add(arg);
        using var host = Process.Start(start)!;
        host.StandardInput.Close();
        var output = host.StandardOutput.ReadToEndAsync();
        var errors = host.StandardError.ReadToEndAsync();
        try { await host.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch (TimeoutException) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync(); throw; }
        string result = File.Exists(report) ? File.ReadAllText(report) : "Missing workspace report";
        Console.WriteLine(result);
        if ((await output).Length != 0 || (await errors).Length != 0)
            throw new Exception($"{name}: CLI output escaped the pseudoconsole.");
        if (host.ExitCode != 0 || !result.Contains("All four round trips passed.", StringComparison.Ordinal))
            throw new Exception($"{name}: one or more provider checks failed.");
        // Provider helpers and OpenConsole may take a moment to release their working directory.
        var project = Path.Combine(new WorkspaceIdentity(AppPaths.FamilyName, name).DataDir, "project");
        var wait = Stopwatch.StartNew();
        while (Directory.Exists(project))
        {
            try { Directory.Delete(project, recursive: true); }
            catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(12)) { await Task.Delay(150); }
        }
    }

    internal static void RunWorkspace(string name, string report)
    {
        if (!Regex.IsMatch(AppPaths.FamilyName, @"^Agmen-tests-[a-f0-9]{32}$")
            || name is not ("Terminal A" or "Terminal B")
            || Path.GetFullPath(report) != Path.Combine(AppPaths.FamilyDir, name + ".txt"))
            throw new InvalidOperationException("Expected an isolated provider test family.");
        AppPaths.SelectWorkspace(name);
        if (AgentConnect.HasInstalledServerReference())
            throw new InvalidOperationException("A real provider registration points to the test executable.");
        string project = Directory.CreateDirectory(AppPaths.PathOf("project")).FullName;
        var saved = AgentProfiles.All.Select(a => new SavedTab(Guid.NewGuid(), project, null, null, false, Agent: AgentProfiles.Id(a))).ToList();
        WorkspaceStore.Save(new WorkspaceState { Tabs = saved, Selected = saved[0].Id });
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                var window = Application.Current.Windows.OfType<WorkspaceWindow>().Single();
                window.Opacity = 0;
                window.ShowInTaskbar = false;
                var results = await Task.WhenAll(window.Tabs.Select(CheckTab));
                var lines = results.Select(r => $"{name} / {r.Agent}: {r.Status}").ToList();
                if (results.Length == 4 && results.All(r => r.Passed)) lines.Add("All four round trips passed.");
                File.WriteAllLines(report, lines);
                window.ShutdownTabs();
            }
            catch (Exception ex) { File.WriteAllText(report, $"{name}: {ex.GetType().Name} {ex.Message}"); }
            finally { Application.Current.Shutdown(); }
        }, DispatcherPriority.Background);
        Environment.ExitCode = Agmen.Program.Main(["--workspace", name]);
    }

    private static async Task<(string Agent, bool Passed, string Status)> CheckTab(HostedTab tab)
    {
        string agent = AgentProfiles.DisplayName(tab.Agent);
        var capture = new StringBuilder();
        string Screen(int offset = 0)
        {
            lock (capture) return Visible(capture.ToString(offset, capture.Length - offset));
        }
        int Offset() { lock (capture) return capture.Length; }
        void Append(ref Span<char> chars) { lock (capture) if (capture.Length < 512_000) capture.Append(chars); }
        async Task Wait(Func<bool> condition, string expectation, int seconds = 20)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                tab.Poll();
                if (tab.IsEnded) throw new Exception("CLI exited while waiting for " + expectation);
                if (timer.Elapsed > TimeSpan.FromSeconds(seconds)) throw new Exception("Timed out waiting for " + expectation);
                await Task.Delay(60);
            }
        }
        try
        {
            await Wait(() => tab.Terminal != null, "the scheduled terminal");
            var term = tab.Terminal!.ConPTYTerm;
            term.InterceptOutputToUITerminal += Append;
            try
            {
                bool IsMenu(string s) => Regex.IsMatch(s, @"trust (this|the|files)|trust and continue", RegexOptions.IgnoreCase);
                bool IsPrompt(string s) => Regex.IsMatch(s, @"ask codex|type.*message|type your |for shortcuts|ask anything|send a message", RegexOptions.IgnoreCase);
                await Wait(() => IsMenu(Screen()) || IsPrompt(Screen()), "a recognizable interactive screen");
                if (IsMenu(Screen()))
                {
                    // Most menus open on Trust. Claude Code 2.1.331 opens on "No, exit", above Trust.
                    const string exit = "No|Exit|Quit", trust = "Yes|Trust|Continue";
                    await Wait(() => Selected(Screen(), exit) || Selected(Screen(), trust), "a highlighted option");
                    bool opensOnExit = Selected(Screen(), exit);
                    var (first, other) = opensOnExit ? (exit, trust) : (trust, exit);
                    var (firstName, otherName) = opensOnExit ? ("Exit", "Trust") : ("Trust", "Exit");
                    // Claude Code can drop a key that arrives just as it redraws its menu; a person pauses between keys too.
                    await Task.Delay(400);
                    int offset = Offset();
                    term.WriteToTerm("\x1b[B");
                    await Wait(() => Selected(Screen(offset), other), $"Down to highlight {otherName}");
                    await Task.Delay(400);
                    offset = Offset();
                    term.WriteToTerm("\x1b[A");
                    await Wait(() => Selected(Screen(offset), first), $"Up to highlight {firstName} again");
                    return (agent, true, $"Down highlighted {otherName}, Up highlighted {firstName}, neither selected");
                }
                string marker = "agmen" + Guid.NewGuid().ToString("N")[..6];
                int before = Offset();
                tab.Poll();
                tab.Paste(marker);
                await Wait(() => Screen(before).Contains(marker, StringComparison.OrdinalIgnoreCase), "echo of the unsubmitted marker");
                return (agent, true, "prompt echoed the unsubmitted marker");
            }
            finally { term.InterceptOutputToUITerminal -= Append; }
        }
        catch (Exception ex) { return (agent, false, ex.Message); }
    }

    private static bool Selected(string screen, string options) => Regex.IsMatch(screen,
        $@"[>❯›▸]\s*(?:\d+[.)]\s*)?(?:{options})\b", RegexOptions.IgnoreCase);

    private static string Visible(string text) =>
        Regex.Replace(Regex.Replace(text, @"\x1b\[\d*C", " "), @"\x1b(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)|[@-Z\\-_])", "");
}
