using System.Text.Json.Nodes;

namespace Agmen.Mcp;

/// <summary>
/// The tools the Agmen MCP server offers, with their input schemas. What a session sees depends
/// on its role, which the server learns from Agmen when it starts: every tab gets the tree
/// tools, the primary also gets its overview of every session, and a session outside Agmen
/// gets none.
/// </summary>
public static class McpTools
{
    public const string PrimaryInstructions =
        "You are the primary session in Agmen, the user's Windows workspace, at the top of their session tree. " +
        "spawn_child starts child sessions under you, of any agent: Claude Code, Codex, Kimi Code or Antigravity. " +
        "message_child, read_child and list_children direct and follow them; you direct only your own children. " +
        "list_sessions and read_session give you a read-only view of every session.";

    public const string TabInstructions =
        "Agmen runs this session in a tab of the user's session tree. spawn_child starts child sessions under you, of " +
        "any agent; message_child, read_child and list_children direct and follow them. message_parent reports to the " +
        "session that started you. whoami shows your name, your parent and your children.";

    public const string OutsideInstructions =
        "This session isn't running inside Agmen, so Agmen's tools aren't available here.";

    /// <summary>Tools only the primary gets: its read-only overview of every session, and notifications.</summary>
    public static readonly HashSet<string> PrimaryOnly = new(StringComparer.Ordinal)
    {
        "list_sessions", "read_session", "focus_session", "notify",
    };

    private const string Colors = """["bug", "planning", "execution", "ready", "none"]""";
    private const string Agents = """["claude", "codex", "kimi", "antigravity"]""";

    private static readonly string CatalogJson = """
    [
      {
        "name": "whoami",
        "title": "Where am I in the tree",
        "description": "Tell you which Agmen tab you run in: your name, your agent, your folder, your parent session if you have one, and your children.",
        "inputSchema": { "type": "object", "properties": {} },
        "annotations": { "readOnlyHint": true }
      },
      {
        "name": "list_children",
        "title": "List your children",
        "description": "List the child sessions you started, or that the user put under you: each one's name, agent, status (busy, idle, waiting for the user, ended), folder, its own children, and any messages still waiting to be typed into it.",
        "inputSchema": { "type": "object", "properties": {} },
        "annotations": { "readOnlyHint": true }
      },
      {
        "name": "spawn_child",
        "title": "Start a child session",
        "description": "Start a new session as your child, in its own Agmen tab under yours: Claude Code, Codex, Kimi Code or Antigravity. Give it its first task in message. It starts in your folder unless you name another, and it reports its result back to you with message_parent; Agmen also tells you when the turn your task starts is over. It runs on the model, effort and permissions the user set for that agent in Agmen, unless you give it a model or effort of its own. Start children only when the user or your parent asks you to, or the task you were given asks for it.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "agent": { "type": "string", "enum": AGENTS, "description": "The agent CLI to run: claude (Claude Code), codex (OpenAI Codex), kimi (Kimi Code) or antigravity (Google's Antigravity CLI). Default: the same agent as you." },
            "message": { "type": "string", "description": "The child's first task. Make it self-contained: what to do, why, and what done looks like." },
            "name": { "type": "string", "description": "A short name such as api-tests: letters, digits, dots, dashes and underscores. Default: the agent and a number, such as codex-1." },
            "folder": { "type": "string", "description": "The folder to work in. Default: your own folder. list_projects finds local projects." },
            "resume_session_id": { "type": "string", "description": "Resume an earlier conversation of the chosen agent as your child, instead of starting a new one." },
            "color": { "type": "string", "enum": COLORS, "description": "Color code for its card: bug (red), planning (blue), execution (yellow), ready (green)." },
            "model": { "type": "string", "description": "A model for the child, such as opus or sonnet for Claude Code, gpt-6.1-sol for Codex, kimi-code/k3.1-pro for Kimi Code, or gemini-4-argon-high for Antigravity. Default: the user's setting for that agent." },
            "effort": { "type": "string", "enum": ["low", "medium", "high", "xhigh", "max", "ultra"], "description": "Reasoning effort for the child; ultra is Codex only. Default: the user's setting for that agent." }
          }
        }
      },
      {
        "name": "message_child",
        "title": "Message a child",
        "description": "Send one of your children a message. Agmen types it into the child's prompt, labeled '[From your parent, <your name>]', as soon as the child is idle and nobody is typing there; until then it waits in a queue, and the result says why. By default Agmen tells you when the turn your message starts is over. A child whose session ended is started again, resuming its conversation.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "child": { "type": "string", "description": "The child's name from list_children. Optional when you have only one child." },
            "message": { "type": "string", "description": "What to tell it. Make it specific and self-contained." },
            "report_back": { "type": "boolean", "description": "Tell you when the turn this message starts is over. Default true." }
          },
          "required": ["message"]
        }
      },
      {
        "name": "read_child",
        "title": "Read a child",
        "description": "Read what one of your children has been doing, from its own records, without interrupting it: its latest prompts and replies, and what it is waiting for. Prefer this to messaging a child just to ask how it is going.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "child": { "type": "string", "description": "The child's name from list_children. Optional when you have only one child." },
            "messages": { "type": "integer", "description": "How many recent messages to include. Default 6, at most 20." }
          }
        },
        "annotations": { "readOnlyHint": true }
      },
      {
        "name": "message_parent",
        "title": "Message your parent",
        "description": "Send your parent session a message. Agmen types it into your parent's prompt, labeled '[From your child, <your name>]', as soon as your parent is free. Use it to report your result when you finish a task your parent gave you, or to ask your parent a question when you are blocked.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "What to tell your parent: the result, what changed and where, or the question." }
          },
          "required": ["message"]
        }
      },
      {
        "name": "update_child",
        "title": "Annotate a child",
        "description": "Set one of your children's color code and a one-line summary of what it is working on, shown on its card. Only the fields you pass change.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "child": { "type": "string", "description": "The child's name from list_children. Optional when you have only one child." },
            "color": { "type": "string", "enum": COLORS, "description": "Color code for its card." },
            "summary": { "type": "string", "description": "One line, under 120 characters. Empty string clears it." }
          }
        }
      },
      {
        "name": "list_projects",
        "title": "List local projects",
        "description": "List local folders where Claude Code has been used, most recent first, with each folder's git remote, repository name, and current branch. Use it to find the right folder before starting a child there.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "filter": { "type": "string", "description": "Only folders whose path or repository name contains this text." },
            "limit": { "type": "integer", "description": "At most this many. Default 30." }
          }
        },
        "annotations": { "readOnlyHint": true }
      },
      {
        "name": "list_sessions",
        "title": "List every session",
        "description": "List every session Agmen can see, in tree order: each tab with its agent, parent and children, and Claude Code sessions running outside Agmen. Shows each one's status, folder, git branch, summary, and queued messages. Read-only: you direct only your own children.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "include_ended": { "type": "boolean", "description": "Include tabs whose session has ended. Default true." }
          }
        },
        "annotations": { "readOnlyHint": true }
      },
      {
        "name": "read_session",
        "title": "Read any session",
        "description": "Read what any session has been doing, from its own records, without interrupting it. For Claude Code sessions it also shows the todo list, files changed and recent commands.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "session": { "type": "string", "description": "The session's name or id from list_sessions." },
            "messages": { "type": "integer", "description": "How many recent messages to include. Default 6, at most 20." }
          },
          "required": ["session"]
        },
        "annotations": { "readOnlyHint": true }
      },
      {
        "name": "focus_session",
        "title": "Show a session",
        "description": "Bring a session to the front so the user can see it: its Agmen tab, or the window it runs in.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "session": { "type": "string", "description": "The session's name or id from list_sessions." }
          },
          "required": ["session"]
        }
      },
      {
        "name": "notify",
        "title": "Notify the user",
        "description": "Show the user a Windows notification from Agmen. Use it sparingly, for things that need the user soon, such as a session that has been waiting on them for a while.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "One or two short sentences." },
            "session": { "type": "string", "description": "A session to open when the user clicks the notification." }
          },
          "required": ["message"]
        }
      }
    ]
    """.Replace("COLORS", Colors).Replace("AGENTS", Agents);

    private static readonly JsonArray Parsed = (JsonArray)JsonNode.Parse(CatalogJson)!;

    public static readonly HashSet<string> Names =
        Parsed.Select(t => t!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a session in this role may call the tool.</summary>
    public static bool Allowed(string role, string tool) => role switch
    {
        "primary" => Names.Contains(tool),
        "tab" => Names.Contains(tool) && !PrimaryOnly.Contains(tool),
        _ => false,
    };

    public static string InstructionsFor(string role) => role switch
    {
        "primary" => PrimaryInstructions,
        "tab" => TabInstructions,
        _ => OutsideInstructions,
    };

    /// <summary>The tools for a role, as a fresh copy each time, because a JSON node can only belong to one parent.</summary>
    public static JsonArray Catalog(string role) =>
        new(Parsed.Where(t => Allowed(role, t!["name"]!.GetValue<string>())).Select(t => t!.DeepClone()).ToArray());
}
