using System.Text;

namespace Agmen.Agents;

/// <summary>
/// What every session in an Agmen tab is told about working in the tree, preloaded the way each
/// agent allows: Claude Code with --append-system-prompt, Codex with -c developer_instructions,
/// Kimi Code and Antigravity with an agmen agent that Connect installs and Agmen starts them with
/// (Kimi 2.1's interactive mode doesn't apply it yet). A child's first message also carries
/// <see cref="ChildFooter"/>, and every agent reads the tools' own descriptions.
/// </summary>
public static class AgentGuide
{
    public const string Text =
        "You are running inside Agmen, the user's Windows workspace, where agent sessions form a tree. " +
        "Agmen's tools (the agmen MCP server) connect you to it:\n" +
        "- spawn_child starts a child session under you, of any agent (Claude Code, Codex, Kimi Code or Antigravity), with its first task.\n" +
        "- message_child, read_child and list_children direct and follow your children. You can direct only your own children.\n" +
        "- message_parent reports to the session that started you. When your parent gives you a task, send it your result " +
        "with message_parent when you finish, or your question if you are blocked.\n" +
        "- whoami shows your name, your parent and your children.\n" +
        "Messages from your parent arrive as \"[From your parent, <name>]\" and from your children as \"[From your child, <name>]\". " +
        "When a child finishes a turn you started, Agmen adds an \"[Agmen]\" note; read_child shows what it did. " +
        "Messages without such a label come from the user, typing in your tab: answer those there, " +
        "and use message_parent only for work your parent gave you.\n" +
        "Start child sessions only when the user or your parent asks for them.";

    /// <summary>
    /// What a primary that isn't Claude Code is told on its command line, where a tab gets
    /// <see cref="Text"/>. Its full instructions are in the AGENTS.md of the primary's folder.
    /// </summary>
    public const string PrimaryText =
        "You are the primary session in Agmen, the user's Windows workspace, at the top of their tree of agent sessions. " +
        "Your instructions are in AGENTS.md in the folder you started in. Agmen's tools (the agmen MCP server) connect you to the sessions:\n" +
        "- spawn_child starts a child session of any agent (Claude Code, Codex, Kimi Code or Antigravity), with its first task.\n" +
        "- message_child, read_child, list_children and update_child direct and follow your own children.\n" +
        "- list_sessions and read_session show every session, read-only.\n" +
        "Messages from your children arrive as \"[From your child, <name>]\", and Agmen's own notes as \"[Agmen]\". " +
        "Messages without such a label come from the user.";

    /// <summary>Appended to a child's first message, so even an agent without preloaded instructions knows how to report back.</summary>
    public static string ChildFooter(string child, string parent) =>
        $"[Agmen] You are {child}, a child session of {parent} in Agmen. When you finish this task, or if you are blocked, " +
        $"tell {parent} with the agmen tool message_parent. Messages without a \"[From …]\" label come from the user: answer those here.";

    /// <summary>The name of the agent Connect installs for Kimi Code and Antigravity.</summary>
    public const string AgentName = "agmen";

    /// <summary>
    /// Kimi's agmen agent: its own default prompt (${base_prompt}) followed by the guide. Kimi
    /// keeps the agent a session started with when the session resumes.
    /// </summary>
    public static string KimiAgent() =>
        "---\n" +
        $"name: {AgentName}\n" +
        "description: Kimi Code's default agent, running in an Agmen tab with Agmen's tools for parent and child sessions\n" +
        "---\n\n" +
        "${base_prompt}\n\n" +
        Text + "\n";

    /// <summary>
    /// The tools Antigravity's agmen agent asks for: the ones its default agent has. An agent
    /// that names none gets only a small read-only set, with no file edits, no commands and no
    /// questions. MCP tools come with inheritMcp and aren't named here. agy won't run an agent
    /// that names a tool it doesn't have, so this list holds only names agy 1.5 accepts, and a
    /// new session whose agent agy refuses starts again as agy's default agent.
    /// </summary>
    public static readonly string[] AntigravityTools =
    {
        "read_file", "edit_file", "write_file", "run_command", "manage_task", "search_web", "ask_question",
        "schedule", "send_message", "define_subagent", "invoke_subagent", "manage_subagents", "run_workflow",
    };

    /// <summary>
    /// Antigravity's agmen agent: its default agent with the guide added, its default agent's
    /// tools, the user's own MCP servers and Agmen's, and never offered as a subagent.
    /// </summary>
    public static string AntigravityAgent() =>
        "---\n" +
        $"name: {AgentName}\n" +
        "description: Antigravity's default agent, running in an Agmen tab with Agmen's tools for parent and child sessions\n" +
        "mainAgent: true\n" +
        "subagent: false\n" +
        "inheritMcp: true\n" +
        $"tools: [{string.Join(", ", AntigravityTools)}]\n" +
        "---\n\n" +
        "# Agmen\n\n" +
        Text + "\n";

    /// <summary>The guide as a TOML basic string, for Codex's -c developer_instructions override.</summary>
    public static string AsTomlString(bool primary = false)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in primary ? PrimaryText : Text)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }
}
