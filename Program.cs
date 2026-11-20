using System.Runtime.CompilerServices;

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
        if (args.Length > 0 && args[0].Equals("--hook", StringComparison.OrdinalIgnoreCase))
            return Hooks.HookRunner.Run(args);
        if (args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
            return Mcp.McpServer.Run();
        return RunApp();
    }

    // Kept separate so the MCP server process never loads WPF.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
