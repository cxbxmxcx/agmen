using System.IO;
using System.Text.Json;
using Agmen.Services;

namespace Agmen.Hosting;

/// <summary>
/// A tab as saved in workspace.json. <paramref name="Agent"/> is the agent's id; none means Claude
/// Code. <paramref name="ParentId"/> places it in the session tree, and <paramref name="Collapsed"/>
/// says whether its children are folded away. <paramref name="Model"/> and <paramref name="Effort"/>
/// are the session's own, such as a parent chose; none means the agent's settings.
/// </summary>
public sealed record SavedTab(Guid Id, string Cwd, string? SessionId, string? Title, bool Ended, string? Category = null,
                             bool IsPrimary = false, string? Note = null, string? Agent = null,
                             Guid? ParentId = null, bool Collapsed = false, string? Model = null, string? Effort = null);
public sealed record SavedBounds(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>Everything the workspace needs to come back after a restart or a crash.</summary>
public sealed class WorkspaceState
{
    public List<SavedTab> Tabs { get; set; } = new();
    public Guid? Selected { get; set; }
    public SavedBounds? Bounds { get; set; }
    public string? LastFolder { get; set; }
    /// <summary>The agent last picked for a new session, which Ctrl+Shift+T starts again.</summary>
    public string? LastAgent { get; set; }
    public bool Notifications { get; set; } = true;
    /// <summary>
    /// A Claude Code primary's conversation, so closing its tab and starting it again picks up
    /// where it was. Kept for workspaces saved before any agent could be the primary.
    /// </summary>
    public string? PrimarySessionId { get; set; }
    /// <summary>The agent the primary runs as, by id; none means Claude Code.</summary>
    public string? PrimaryAgent { get; set; }
    /// <summary>Each agent's own primary conversation, by agent id, so switching back to an agent resumes it.</summary>
    public Dictionary<string, string>? PrimarySessions { get; set; }
}

public static class WorkspaceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string FilePath => AppPaths.PathOf("workspace.json");

    /// <summary>True when this copy of Agmen has never saved a workspace: the first time it runs.</summary>
    public static bool FirstRun { get; private set; }

    public static WorkspaceState Load()
    {
        try
        {
            FirstRun = !File.Exists(FilePath);
            if (!FirstRun)
                return JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for inspection instead of overwriting it with an empty workspace.
            Log.Write($"workspace.json unreadable, starting empty: {ex.Message}");
            try { File.Move(FilePath, FilePath + ".corrupt", overwrite: true); } catch (Exception) { /* best effort */ }
        }
        return new();
    }

    /// <summary>Write a temp file and swap it in, so a crash mid-save never leaves a half-written workspace.</summary>
    public static void Save(WorkspaceState state)
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Write($"workspace save failed: {ex.Message}"); }
    }
}
