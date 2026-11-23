using System.IO;
using Agmen.Services;

namespace Agmen.Tests;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--mcp", ..] or ["--hook", ..])
        {
            Environment.ExitCode = Agmen.Program.Main(args);
            return;
        }
        if (args is ["--real-provider-workspace", var name, var report])
        {
            RealProviderTerminalChecks.RunWorkspace(name, report);
            return;
        }
        if (args is ["--terminal-stdio-host", var marker]) { RedirectedTerminalChecks.RunHost(marker); return; }
        if (args is ["--terminal-stdio-child", var result])
        {
            File.WriteAllText(result + ".tmp", $"{Console.IsInputRedirected},{Console.IsOutputRedirected},{Console.IsErrorRedirected}");
            File.Move(result + ".tmp", result);
            return;
        }
        if (args is ["--routing-host", var helper]) { WorkspaceRoutingChecks.RunHost(helper); return; }
        string instance = "Agmen-tests-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("AGMEN_INSTANCE", instance);
        string scratch = AppPaths.DataDir;
        if (Path.GetFileName(scratch) != instance) throw new InvalidOperationException("Expected an isolated test instance.");
        Directory.CreateDirectory(scratch);
        try
        {
            WorkspaceSessionChecks.Run();
            NamedWorkspaceChecks.Run();
            PersistenceChecks.Run();
            HistoryChecks.Run();
            WorkspaceChecks.Run();
            SharingChecks.Run();
            IdentityChecks.Run();
            WorkspaceRoutingChecks.Run();
            CodexIdentityChecks.Run();
            if (args.Contains("--native"))
            {
                RedirectedTerminalChecks.Run();
                NativeConversationChecks.Run();
            }
            if (args.Contains("--real-providers")) RealProviderTerminalChecks.Run();
            Console.WriteLine("Conversation checks passed.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
