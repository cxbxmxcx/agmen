using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agmen.Agents;
using Agmen.Services;

namespace Agmen.Orchestration;

/// <summary>
/// Prepares the primary session's own folder before every start: its instructions, the user's
/// notes and, for Claude Code, its permissions and an MCP configuration that points at this
/// Agmen executable. Any agent can run as the primary. Claude Code reads its instructions from
/// CLAUDE.md; Codex, Kimi Code and Antigravity read AGENTS.md. Regenerating each time keeps the
/// executable path and the notes current.
/// </summary>
public static class PrimarySetup
{
    private const string Marker = "<!-- Agmen manages this file";

    public static string Folder => Path.Combine(AppPaths.DataDir, "primary");
    public static string McpConfigPath => Path.Combine(Folder, ".agmen", "mcp.json");
    public static string SettingsPath => Path.Combine(Folder, ".agmen", "settings.json");
    public static string NotesPath => Path.Combine(Folder, "primary-notes.md");

    /// <summary>A Claude Code primary's session name: its title, and the address other sessions reply to.</summary>
    public static string SessionName => AppPaths.IsDev ? "agmen-primary-dev" : "agmen-primary";

    /// <summary>The file each agent reads its instructions from, in the folder it starts in.</summary>
    public static string InstructionsFile(AgentKind agent) => agent == AgentKind.Claude ? "CLAUDE.md" : "AGENTS.md";

    /// <summary>
    /// The first message of a brand-new primary. It names the instructions file, so an agent that
    /// didn't load the file on its own still reads it, and says when sessions are already under it.
    /// </summary>
    public static string FirstStartPrompt(AgentKind agent, int children) =>
        "[Agmen] You've just been started as the primary session."
        + (agent == AgentKind.Claude ? "" : $" Your instructions are in {InstructionsFile(agent)} in this folder: read it now if it isn't already in your context.")
        + (children == 0 ? "" : $" {children} session{(children == 1 ? " is" : "s are")} already under you from the primary before you: list_children shows {(children == 1 ? "it" : "them")}.")
        + " Introduce yourself to the user in two lines.";

    /// <summary>Write the folder's files and return the extra arguments a Claude Code primary needs; the other agents need none.</summary>
    public static IReadOnlyList<string> Prepare(AgentKind agent)
    {
        Directory.CreateDirectory(Path.Combine(Folder, ".agmen"));
        if (!File.Exists(NotesPath)) File.WriteAllText(NotesPath, NotesTemplate);
        WriteManaged(Path.Combine(Folder, InstructionsFile(agent)), Instructions(agent));
        Log.Write($"primary: prepared {Folder} for {AgentProfiles.DisplayName(agent)}");
        if (agent != AgentKind.Claude) return Array.Empty<string>();

        // Only the Agmen tools: the primary directs sessions and does no work of its own, so it
        // starts quickly without the user's other MCP servers. Settings passed on the command
        // line apply even before the user trusts the folder.
        WriteMcpConfig();
        File.WriteAllText(SettingsPath, Settings());
        return new[] { "--mcp-config", McpConfigPath, "--strict-mcp-config", "--settings", SettingsPath };
    }

    /// <summary>
    /// The instructions for an agent. Claude Code imports the notes file itself. The others can't
    /// import a file, so their copy carries the notes as they are when the primary starts.
    /// </summary>
    private static string Instructions(AgentKind agent)
    {
        bool claude = agent == AgentKind.Claude;
        string notes;
        if (claude) notes = "@primary-notes.md\n";
        else
        {
            string saved;
            try { saved = File.ReadAllText(NotesPath).Trim(); }
            catch (Exception) { saved = ""; }
            notes = "These are your standing notes, from `primary-notes.md` in this folder. Agmen copies them in here each time you start. "
                    + "To record something for next time, edit `primary-notes.md`, not this file.\n\n"
                    + (saved.Length == 0 ? "(no notes yet)" : saved) + "\n";
        }
        return Template
            .Replace("{HOW_NOTES_ARRIVE}", claude ? "which is imported at the end" : "which Agmen copies in at the end")
            .Replace("{CLAUDE_ONLY}", claude
                ? $" Use `message_child` rather than `SendMessage` for your children, so messages show in Agmen and reach every agent. Your session name is `{SessionName}`."
                : "")
            .Replace("{NOTES}", notes);
    }

    private static void WriteMcpConfig()
    {
        var entry = Agents.ToolServer.Entry();
        entry["type"] = "stdio";
        var config = new JsonObject { ["mcpServers"] = new JsonObject { ["agmen"] = entry } };
        File.WriteAllText(McpConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Settings()
    {
        var settings = new JsonObject
        {
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("mcp__agmen", "Edit(./primary-notes.md)") },
        };
        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Rewrite a file Agmen owns, unless the user removed the marker to take it over.</summary>
    private static void WriteManaged(string path, string content)
    {
        if (File.Exists(path))
        {
            var first = File.ReadLines(path).FirstOrDefault() ?? "";
            if (!first.StartsWith(Marker, StringComparison.Ordinal)) return;
        }
        File.WriteAllText(path, content);
    }

    private const string NotesTemplate = """
        # Primary notes

        Standing instructions for the primary session. Agmen never overwrites this file. The primary records what it learns here, and you can edit it too.

        ## Preferences

        """;

    private const string Template = """
        <!-- Agmen manages this file and rewrites it each time the primary starts. Put your own instructions in primary-notes.md, {HOW_NOTES_ARRIVE}. Delete this line to stop Agmen from rewriting the file. -->

        # You are the primary session

        You run inside Agmen, the user's Windows workspace for agent sessions. Sessions in Agmen form a tree, and you sit at the top of the user's. Each session runs one agent CLI: Claude Code, Codex (OpenAI), Kimi Code, or Antigravity (Google).

        - **Direction.** When the user hands you work, start child sessions for it, give each a clear task, follow their progress, and report back. Your children can start children of their own, which they direct.
        - **Awareness.** Know what every session is doing, so you can answer "what's everyone working on?", "what's waiting on me?", or "is the API fix done?".

        This folder only holds your instructions and notes. You don't write code here; your children do.

        ## Tools

        The `agmen` tools connect you to Agmen:

        | Tool | Use it to |
        |---|---|
        | `spawn_child` | Start a child session of any agent, in a folder, with its first task |
        | `list_children` | See your children: name, agent, status, folder |
        | `message_child` | Send one of your children a message |
        | `read_child` | Read what a child has been doing, without interrupting it |
        | `update_child` | Set a child's color and one-line summary |
        | `whoami` | See your place in the tree |
        | `list_projects` | Find local project folders |
        | `list_sessions` | See every session, in tree order, and Claude Code sessions outside Agmen; read-only |
        | `read_session` | Read any session; read-only |
        | `focus_session` | Bring a session to the front for the user |
        | `notify` | Show the user a Windows notification |

        You direct only your own children. A session the user started by hand isn't yours to direct unless they move it under you. Messages from your children arrive as "[From your child, <name>]". When a child finishes a turn your message started, Agmen adds an "[Agmen]" note with the start of its reply.{CLAUDE_ONLY}

        Color codes follow the user's convention: red **bug**, blue **planning**, yellow **execution**, green **ready** (done, or waiting for new work).

        ## Rules

        1. **Nothing leaves this machine without the user's OK in this conversation.** No pushes, merges, branch deletions, releases, or changes to remote services. Don't ask a child to do any of these unless the user asked for it.
        2. **Never answer for the user.** When a session waits on a permission prompt or a question, tell the user instead of answering it.
        3. **Read before you message.** Use `read_child` to learn how a child is doing. Message a child only to direct its work, and avoid interrupting one that is busy unless it matters now.
        4. **Write self-contained tasks.** Say what to do, why, and what done looks like, and ask the child to report back with `message_parent`.
        5. **Start children only when asked,** or as part of a plan the user approved. Use the agent the user asks for, and Claude Code when they don't say. Give each a short name, and start it in the folder the work belongs in.
        6. **Close the loop.** When a child reports back, or Agmen says it finished, check its result and tell the user what happened in a line or two. Send a follow-up only when it's part of what the user asked for.
        7. **Be brief.** The user glances at this tab between other work. Lead with what needs them.

        ## Your notes

        {NOTES}
        """;
}
