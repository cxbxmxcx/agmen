using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agmen.Services;

namespace Agmen.Agents;

/// <summary>
/// How Agmen starts an agent's sessions: the model, the reasoning effort and the permission
/// mode. Null leaves that choice to the agent's own settings.
/// </summary>
public sealed record AgentLaunch(string? Model = null, string? Effort = null, string? Permissions = null);

/// <summary>A permission mode as the settings window offers it: the id Agmen stores, and what it means.</summary>
public sealed record PermissionMode(string Id, string Label);

/// <summary>
/// The user's launch settings for each agent, kept in agents.json, and how each agent's command
/// line expresses them. Agmen applies them every time it starts, resumes or restarts a session. A
/// child can get its own model and effort from spawn_child, but never other permissions.
/// </summary>
public static class AgentSettings
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly Regex ModelName = new(@"^[A-Za-z0-9][A-Za-z0-9._:/@+\-\[\]]{0,79}$");
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string FilePath => AppPaths.PathOf("agents.json");
    private static Dictionary<string, AgentLaunch>? _settings;
    private static IReadOnlyList<string>? _antigravityModels;

    public static AgentLaunch For(AgentKind agent)
    {
        lock (Gate) return Load().TryGetValue(AgentProfiles.Id(agent), out var launch) ? launch : new AgentLaunch();
    }

    public static void Set(AgentKind agent, AgentLaunch launch)
    {
        lock (Gate)
        {
            var all = Load();
            all[AgentProfiles.Id(agent)] = launch;
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all, Options));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { Log.Write($"agent settings save failed: {ex.Message}"); }
        }
    }

    private static Dictionary<string, AgentLaunch> Load()
    {
        if (_settings is not null) return _settings;
        try
        {
            if (File.Exists(FilePath))
                _settings = JsonSerializer.Deserialize<Dictionary<string, AgentLaunch>>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { Log.Write($"agents.json unreadable: {ex.Message}"); }
        return _settings ??= new Dictionary<string, AgentLaunch>();
    }

    /// <summary>
    /// A model name safe to put on a command line: letters, digits and . _ : / @ + - [ ], as in
    /// opus[1m] or kimi-code/k3.1-pro.
    /// </summary>
    public static bool IsValidModel(string? model) => model is not null && ModelName.IsMatch(model);

    /// <summary>The reasoning efforts each agent takes, lowest first. Codex's ultra suits only some of its models.</summary>
    public static IReadOnlyList<string> Efforts(AgentKind agent) => agent == AgentKind.Codex
        ? new[] { "low", "medium", "high", "xhigh", "max", "ultra" }
        : new[] { "low", "medium", "high", "xhigh", "max" };

    public static IReadOnlyList<PermissionMode> PermissionModes(AgentKind agent) => agent switch
    {
        AgentKind.Claude => new PermissionMode[]
        {
            new("manual", "Ask before each action"),
            new("acceptEdits", "Accept file edits, ask for the rest"),
            new("auto", "Approve what Claude judges safe"),
            new("plan", "Plan only, no changes"),
            new("bypassPermissions", "Never ask"),
        },
        AgentKind.Codex => new PermissionMode[]
        {
            new("on-request", "Ask when needed, in the workspace sandbox"),
            new("approve-for-me", "Review approvals automatically, in the workspace sandbox"),
            new("never", "Never ask, in the workspace sandbox"),
            new("full-access", "Never ask, with no sandbox"),
        },
        AgentKind.Kimi => new PermissionMode[]
        {
            new("yolo", "Ask only for risky actions"),
            new("auto", "Never ask"),
        },
        _ => new PermissionMode[]
        {
            new("accept-edits", "Accept file edits, ask for the rest"),
            new("auto", "Approve what Antigravity judges safe"),
            new("plan", "Plan only, no changes"),
            new("skip-permissions", "Never ask"),
        },
    };

    /// <summary>
    /// Models to suggest; the user may type any other name the agent accepts. Antigravity's come
    /// from <see cref="LoadAntigravityModels"/>, which asks agy itself.
    /// </summary>
    public static IReadOnlyList<string> KnownModels(AgentKind agent)
    {
        try
        {
            return agent switch
            {
                AgentKind.Claude => new[] { "fable", "opus", "sonnet", "haiku" },
                AgentKind.Codex => CodexModels(),
                AgentKind.Kimi => KimiModels(),
                _ => _antigravityModels ?? Array.Empty<string>(),
            };
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>The models Codex lists, from the catalog it caches in ~/.codex/models_cache.json.</summary>
    private static IReadOnlyList<string> CodexModels()
    {
        var file = Path.Combine(Home, ".codex", "models_cache.json");
        if (!File.Exists(file)) return Array.Empty<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return models.EnumerateArray()
            .Where(m => m.TryGetProperty("visibility", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == "list")
            .Select(m => m.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null)
            .OfType<string>().ToList();
    }

    /// <summary>The model aliases Kimi Code has set up, from the [models."…"] tables of its config.toml.</summary>
    private static IReadOnlyList<string> KimiModels()
    {
        var file = Path.Combine(Home, ".kimi-code", "config.toml");
        if (!File.Exists(file)) return Array.Empty<string>();
        return File.ReadLines(file).Select(l => Regex.Match(l.Trim(), @"^\[models\.""([^""]+)""\]$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>Ask agy for its models, once per run of Agmen. It fetches them, so this takes a few seconds.</summary>
    public static async Task<IReadOnlyList<string>> LoadAntigravityModels()
    {
        if (_antigravityModels is not null) return _antigravityModels;
        if (AgentProfiles.Locate(AgentKind.Antigravity) is not { } agy) return Array.Empty<string>();
        try
        {
            var start = new ProcessStartInfo(agy, "models") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start)!;
            string output;
            try { output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                throw;
            }
            // One model per line: its id, a tab, and its display name.
            _antigravityModels = output.Split('\n').Select(l => l.Split('\t')[0].Trim()).Where(IsValidModel).ToList();
        }
        catch (Exception ex) { Log.Write($"agent settings: agy models failed: {ex.Message}"); }
        return _antigravityModels ?? Array.Empty<string>();
    }

    // ------------------------------------------------------------ command lines

    /// <summary>
    /// The settings an agent's command line may carry: a hand-edited agents.json can't put
    /// anything else there, and the Kimi command line goes through cmd.
    /// </summary>
    public static AgentLaunch Checked(AgentKind agent, AgentLaunch launch) => new(
        IsValidModel(launch.Model) ? launch.Model : null,
        launch.Effort is { } effort && Efforts(agent).Contains(effort) ? effort : null,
        launch.Permissions is { } mode && PermissionModes(agent).Any(m => m.Id == mode) ? mode : null);

    public static IEnumerable<string> ClaudeArgs(AgentLaunch launch)
    {
        launch = Checked(AgentKind.Claude, launch);
        if (launch.Model is { } model) { yield return "--model"; yield return model; }
        if (launch.Effort is { } effort) { yield return "--effort"; yield return effort; }
        if (launch.Permissions is { } mode) { yield return "--permission-mode"; yield return mode; }
    }

    public static IEnumerable<string> CodexArgs(AgentLaunch launch)
    {
        launch = Checked(AgentKind.Codex, launch);
        if (launch.Model is { } model) { yield return "-m"; yield return model; }
        if (launch.Effort is { } effort) { yield return "-c"; yield return $"model_reasoning_effort='{effort}'"; }
        switch (launch.Permissions)
        {
            case "on-request" or "never":
                yield return "-a";
                yield return launch.Permissions;
                yield return "-s";
                yield return "workspace-write";
                break;
            case "approve-for-me":
                yield return "--approve-for-me";
                break;
            case "full-access":
                yield return "--dangerously-bypass-approvals-and-sandbox";
                break;
        }
    }

    /// <summary>Kimi Code takes its effort only from KIMI_MODEL_THINKING_EFFORT; see <see cref="KimiEffortVariable"/>.</summary>
    public static IEnumerable<string> KimiArgs(AgentLaunch launch)
    {
        launch = Checked(AgentKind.Kimi, launch);
        if (launch.Model is { } model) { yield return "-m"; yield return model; }
        if (launch.Permissions is "yolo" or "auto") yield return "--" + launch.Permissions;
    }

    public const string KimiEffortVariable = "KIMI_MODEL_THINKING_EFFORT";

    public static IEnumerable<string> AntigravityArgs(AgentLaunch launch)
    {
        launch = Checked(AgentKind.Antigravity, launch);
        if (launch.Model is { } model) { yield return "--model"; yield return model; }
        if (launch.Effort is { } effort) { yield return "--effort"; yield return effort; }
        if (launch.Permissions == "skip-permissions") yield return "--dangerously-skip-permissions";
        else if (launch.Permissions is { } mode) { yield return "--mode"; yield return mode; }
    }
}
