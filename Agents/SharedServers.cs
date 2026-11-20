using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agmen.Services;

namespace Agmen.Agents;

/// <summary>
/// The MCP servers every session gets, whatever its agent: Claude Code's user-level servers, read
/// from ~/.claude.json, so a server added with <c>claude mcp add -s user</c> reaches every agent.
/// Claude sessions load them on their own. Codex gets them as -c overrides when it starts
/// (<see cref="CodexOverrides"/>), so its config.toml stays untouched. Kimi Code and Antigravity
/// read servers only from their own config, where <see cref="AgentConnect"/> keeps them beside the
/// agmen server, in the shape <see cref="EntryFor"/> gives.
/// </summary>
public static class SharedServers
{
    /// <summary>One server, under a name every agent accepts. A remote one has a Url and no Command.</summary>
    public sealed record Server(string Name, string? Command, string[] Args, JsonObject? Env, string? Url, string Transport, JsonObject? Headers);

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string ClaudeConfig => Path.Combine(Home, ".claude.json");
    private static string CodexConfig => Path.Combine(Home, ".codex", "config.toml");

    private static IReadOnlyList<Server> _last = Array.Empty<Server>();
    private static readonly HashSet<string> _notForCodex = new();

    /// <summary>
    /// Claude Code's user-level servers, except agmen, each once: entries with the same command or
    /// URL are one server added under two names, and the first name wins.
    /// </summary>
    public static IReadOnlyList<Server> Load()
    {
        try
        {
            var list = new List<Server>();
            if (File.Exists(ClaudeConfig) && JsonNode.Parse(File.ReadAllText(ClaudeConfig))?["mcpServers"] is JsonObject servers)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (rawName, node) in servers)
                {
                    if (node is not JsonObject def || Parse(rawName, def) is not { } server) continue;
                    if (server.Name == "agmen" || !seen.Add(Identity(server))) continue;
                    var name = server.Name;
                    for (int n = 2; list.Any(s => s.Name == name); n++) name = $"{server.Name}-{n}";
                    list.Add(server with { Name = name });
                }
            }
            return _last = list;
        }
        catch (Exception ex)
        {
            // Claude Code may be partway through rewriting the file; keep the set read last time.
            Log.Write($"shared servers: couldn't read {ClaudeConfig}: {ex.Message}");
            return _last;
        }
    }

    private static Server? Parse(string rawName, JsonObject def)
    {
        var name = Regex.Replace(rawName.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        if (name.Length == 0) return null;
        var url = Str(def["url"]);
        var transport = Str(def["type"]) ?? (url is null ? "stdio" : "http");
        var env = def["env"] is JsonObject { Count: > 0 } e ? (JsonObject)e.DeepClone() : null;
        var headers = def["headers"] is JsonObject { Count: > 0 } h ? (JsonObject)h.DeepClone() : null;
        if (transport == "stdio")
        {
            if (Str(def["command"]) is not { } command) return null;
            var args = def["args"] is JsonArray a ? a.Select(Str).OfType<string>().ToArray() : Array.Empty<string>();
            return new Server(name, command, args, env, null, transport, null);
        }
        return transport is "http" or "sse" && url is not null ? new Server(name, null, Array.Empty<string>(), null, url, transport, headers) : null;
    }

    private static string Identity(Server s) =>
        s.Command is not null ? s.Command + "\n" + string.Join("\n", s.Args) : s.Url!.TrimEnd('/');

    /// <summary>
    /// The server as an entry of Kimi's mcp.json or Antigravity's mcp_config.json, or null when the
    /// agent can't take it: Antigravity names its remote servers by serverUrl and has no SSE.
    /// </summary>
    public static JsonObject? EntryFor(AgentKind agent, Server s)
    {
        var entry = new JsonObject();
        if (s.Command is not null)
        {
            entry["command"] = s.Command;
            entry["args"] = new JsonArray(s.Args.Select(a => (JsonNode?)a).ToArray());
            if (s.Env is not null) entry["env"] = s.Env.DeepClone();
            return entry;
        }
        if (agent == AgentKind.Antigravity)
        {
            if (s.Transport == "sse") return null;
            entry["serverUrl"] = s.Url;
        }
        else
        {
            if (s.Transport == "sse") entry["transport"] = "sse";
            entry["url"] = s.Url;
        }
        if (s.Headers is not null) entry["headers"] = s.Headers.DeepClone();
        return entry;
    }

    /// <summary>Whether an entry already in an agent's config runs the same server, under whatever name.</summary>
    public static bool SameServer(JsonNode? existing, JsonObject entry)
    {
        if (existing is not JsonObject e) return false;
        if (Str(entry["command"]) is { } command)
        {
            var args = Args(e["args"]);
            return string.Equals(Str(e["command"]), command, StringComparison.OrdinalIgnoreCase)
                   && args.SequenceEqual(Args(entry["args"]));
        }
        var url = Url(entry);
        return url is not null && string.Equals(Url(e)?.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        static string[] Args(JsonNode? node) => node is JsonArray a ? a.Select(Str).OfType<string>().ToArray() : Array.Empty<string>();
        static string? Url(JsonObject o) => Str(o["url"]) ?? Str(o["serverUrl"]) ?? Str(o["httpUrl"]);
    }

    /// <summary>
    /// The shared servers Codex doesn't have in its own config, as -c overrides. A server whose env or
    /// headers hold values is left out, since they can be secrets that would show on the command line
    /// and in Agmen's log; so is SSE, which Codex doesn't speak.
    /// </summary>
    public static IEnumerable<string> CodexOverrides()
    {
        string config;
        try { config = File.Exists(CodexConfig) ? File.ReadAllText(CodexConfig).Replace(@"\\", @"\") : ""; }
        catch (Exception) { config = ""; }
        foreach (var s in Load())
        {
            // Codex's own entry wins: one by the same name, or one naming the same command and script, or URL.
            bool own = Regex.IsMatch(config, $@"^\s*\[mcp_servers\.(""?){Regex.Escape(s.Name)}\1\]", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                       || (s.Command is not null
                           ? config.Contains(s.Command, StringComparison.OrdinalIgnoreCase)
                             && (s.Args.Length == 0 || config.Contains(s.Args[^1], StringComparison.OrdinalIgnoreCase))
                           : config.Contains(s.Url!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (own) continue;
            if (s.Env is not null || s.Headers is not null || s.Transport == "sse")
            {
                if (_notForCodex.Add(s.Name)) Log.Write($"shared servers: {s.Name} isn't passed to Codex; add it to ~/.codex/config.toml to use it there");
                continue;
            }
            var key = "mcp_servers." + s.Name;
            if (s.Command is null)
            {
                yield return "-c";
                yield return $"{key}.url={Toml(s.Url!)}";
                continue;
            }
            yield return "-c";
            yield return $"{key}.command={Toml(s.Command)}";
            if (s.Args.Length == 0) continue;
            yield return "-c";
            yield return $"{key}.args=[{string.Join(",", s.Args.Select(Toml))}]";
        }
    }

    /// <summary>A TOML string: literal when it can be, so Windows paths keep their backslashes as they are.</summary>
    private static string Toml(string s) =>
        s.Contains('\'') || s.Any(char.IsControl) ? JsonSerializer.Serialize(s) : $"'{s}'";

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
