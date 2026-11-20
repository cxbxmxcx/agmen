using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agmen.Services;

namespace Agmen.Agents;

/// <summary>
/// How each agent CLI is pointed at Agmen's tool server (<c>Agmen.exe --mcp</c>) and, where it
/// needs one, at Agmen's hook command (<c>Agmen.exe --hook</c>). Every tab gets the server; what it
/// may do there depends on which tab it turns out to be, which the server works out from its
/// process tree.
/// </summary>
public static class ToolServer
{
    public static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Agmen.exe");

    /// <summary>The agmen server as an MCP config entry, in the shape Claude Code, Kimi and Antigravity all read.</summary>
    public static JsonObject Entry() => new()
    {
        ["command"] = Exe,
        ["args"] = new JsonArray("--mcp"),
        // The server must reach this instance's pipe, also for a dev or test copy with its own name.
        ["env"] = new JsonObject { ["AGMEN_INSTANCE"] = AppPaths.InstanceName },
    };

    /// <summary>
    /// An MCP config with only the agmen server, for <c>claude --mcp-config</c>. Rewritten before each
    /// start so the executable path stays right after an update or a move.
    /// </summary>
    public static string ClaudeConfigFile()
    {
        var entry = Entry();
        entry["type"] = "stdio";
        var config = new JsonObject { ["mcpServers"] = new JsonObject { ["agmen"] = entry } };
        var path = AppPaths.PathOf("tools-mcp.json");
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>
    /// Codex takes the server, its tools pre-approved, and the notify command that tells Agmen
    /// when a turn finishes, as -c overrides on its command line, so its own config.toml is never touched.
    /// </summary>
    public static IEnumerable<string> CodexOverrides(bool primary = false)
    {
        yield return "-c";
        yield return $"mcp_servers.agmen.command='{Exe}'";
        yield return "-c";
        yield return "mcp_servers.agmen.args=['--mcp']";
        yield return "-c";
        yield return $"mcp_servers.agmen.env.AGMEN_INSTANCE='{AppPaths.InstanceName}'";
        yield return "-c";
        yield return "mcp_servers.agmen.default_tools_approval_mode='approve'";
        yield return "-c";
        yield return $"notify=['{Exe}','--hook','codex','notify']";
        // Codex adds these to its own instructions rather than replacing them.
        yield return "-c";
        yield return "developer_instructions=" + AgentGuide.AsTomlString(primary);
    }

    /// <summary>
    /// The command an Antigravity hook runs. Antigravity passes it to cmd /c as one quoted argument,
    /// which turns quotes inside it into \" that cmd can't read, so the path goes in unquoted: as it
    /// is when it has no spaces, or else as its 8.3 short form.
    /// </summary>
    public static string AntigravityHookCommand(string evt) => $"{UnquotedExe()} --hook agy {evt}";

    private static string UnquotedExe()
    {
        var exe = Exe;
        if (!exe.Contains(' ')) return exe;
        var buffer = new StringBuilder(1024);
        uint length = GetShortPathName(exe, buffer, (uint)buffer.Capacity);
        var shortPath = buffer.ToString();
        if (length > 0 && length < buffer.Capacity && !shortPath.Contains(' ')) return shortPath;
        Log.Write($"connect: {exe} has spaces and no short name, so Antigravity's hooks can't run it");
        return $"\"{exe}\"";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);
}
