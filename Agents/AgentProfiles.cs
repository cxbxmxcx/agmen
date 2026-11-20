using System.IO;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Agmen.Agents;

/// <summary>The agent CLIs Agmen can run in a tab.</summary>
public enum AgentKind { Claude, Codex, Kimi, Antigravity }

/// <summary>What Agmen knows about each agent CLI: its names, its colors, and where it is installed.</summary>
public static class AgentProfiles
{
    public static readonly AgentKind[] All = { AgentKind.Claude, AgentKind.Codex, AgentKind.Kimi, AgentKind.Antigravity };

    public static string DisplayName(AgentKind agent) => agent switch
    {
        AgentKind.Codex => "Codex",
        AgentKind.Kimi => "Kimi Code",
        AgentKind.Antigravity => "Antigravity",
        _ => "Claude Code",
    };

    /// <summary>The display name after "a" or "an", for sentences such as "an Antigravity session".</summary>
    public static string WithArticle(AgentKind agent) =>
        (agent == AgentKind.Antigravity ? "an " : "a ") + DisplayName(agent);

    /// <summary>The label on a session's card.</summary>
    public static string ShortName(AgentKind agent) => agent switch
    {
        AgentKind.Codex => "Codex",
        AgentKind.Kimi => "Kimi",
        AgentKind.Antigravity => "agy",
        _ => "Claude",
    };

    /// <summary>The name used in tool arguments and in saved tabs.</summary>
    public static string Id(AgentKind agent) => agent switch
    {
        AgentKind.Codex => "codex",
        AgentKind.Kimi => "kimi",
        AgentKind.Antigravity => "antigravity",
        _ => "claude",
    };

    public static AgentKind? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "claude" or "claude code" or "claude-code" => AgentKind.Claude,
        "codex" or "openai codex" => AgentKind.Codex,
        "kimi" or "kimi code" or "kimi-code" => AgentKind.Kimi,
        "antigravity" or "agy" or "google" or "gemini" => AgentKind.Antigravity,
        _ => null,
    };

    private static readonly Brush ClaudeBrush = Frozen(0xE2, 0x88, 0x5C);
    private static readonly Brush CodexBrush = Frozen(0xD0, 0xD0, 0xDA);
    private static readonly Brush KimiBrush = Frozen(0xA7, 0x8B, 0xFA);
    private static readonly Brush AntigravityBrush = Frozen(0x33, 0xC4, 0xB8);

    /// <summary>The badge color, kept away from the four color codes so an agent never reads as a status.</summary>
    public static Brush BadgeBrush(AgentKind agent) => agent switch
    {
        AgentKind.Codex => CodexBrush,
        AgentKind.Kimi => KimiBrush,
        AgentKind.Antigravity => AntigravityBrush,
        _ => ClaudeBrush,
    };

    /// <summary>
    /// The installed command, found on PATH: claude.exe, codex.cmd (the npm wrapper), kimi.exe,
    /// agy.exe. Null when the agent isn't installed.
    /// </summary>
    public static string? Locate(AgentKind agent) => FindOnPath(agent switch
    {
        AgentKind.Codex => "codex.cmd",
        AgentKind.Kimi => "kimi.exe",
        AgentKind.Antigravity => "agy.exe",
        _ => "claude.exe",
    });

    public static bool IsInstalled(AgentKind agent) => Locate(agent) is not null;

    /// <summary>
    /// Codex installs from npm as codex.cmd, which runs node on the package's codex.js. Starting
    /// node directly keeps cmd.exe from re-reading the quotes and percent signs of a first message.
    /// Returns node.exe and codex.js, or null to fall back to codex.cmd.
    /// </summary>
    public static (string Node, string Script)? CodexEntryPoint()
    {
        if (Locate(AgentKind.Codex) is not { } cmd) return null;
        var dir = Path.GetDirectoryName(cmd)!;
        var script = Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js");
        if (!File.Exists(script)) return null;
        var node = Path.Combine(dir, "node.exe");
        if (!File.Exists(node)) node = FindOnPath("node.exe") ?? "";
        return node.Length == 0 ? null : (node, script);
    }

    public static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
