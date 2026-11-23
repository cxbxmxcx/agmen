using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.IO;
using Agmen.Services;

namespace Agmen;

/// <summary>
/// Entry point. Normally starts the tray app. With --mcp it runs headless instead, as the
/// MCP server that an agent CLI starts for a session; that server relays each tool call to the
/// running Agmen over a local pipe. With --hook it records a signal from an agent CLI and exits.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("--check-uninstall-connections", StringComparison.OrdinalIgnoreCase))
            return Agents.AgentConnect.HasInstalledServerReference() ? 12 : 0;
        if (args.Length > 0 && args[0].Equals("--hook", StringComparison.OrdinalIgnoreCase))
            return Hooks.HookRunner.Run(args);
        if (args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
            return Mcp.McpServer.Run();
        try { AppPaths.SelectWorkspace(NamedWorkspaces.RequestedName(args)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not open the workspace. {ex.Message}");
            return 1;
        }
        return RunApp();
    }

    // Kept separate so the MCP server process never loads WPF.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        ClearGuiStandardHandles();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    // A GUI launched by a shell or automation can inherit redirected standard streams.
    // Windows can pass those streams to the CLI in place of its new pseudoconsole.
    // Clear the GUI's standard handle slots once, without closing the inherited handles.
    internal static void ClearGuiStandardHandles()
    {
        const int stdin = -10, stdout = -11, stderr = -12;
        foreach (int stream in new[] { stdin, stdout, stderr })
            if (!SetStdHandle(stream, IntPtr.Zero))
                Log.Write($"Could not clear standard handle {stream} for the workspace (Windows error {Marshal.GetLastWin32Error()}).");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int stream, IntPtr handle);
}
