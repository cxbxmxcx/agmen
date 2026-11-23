using System.IO;
using System.Text.Json;
using Agmen.Services;

namespace Agmen.Hosting;

/// <summary>
/// A session whose Agmen tab was closed, kept so it can be reopened: where it ran, its
/// conversation, and where it sat in the tree. <paramref name="Agent"/> is the agent's id.
/// </summary>
public sealed record HistoryEntry(Guid TabId, string Agent, string Cwd, string SessionId, string Title, DateTimeOffset ClosedAt,
                                  Guid? ParentId = null, string? ParentTitle = null, string? Category = null, string? Note = null,
                                  Guid? HandoffId = null);

/// <summary>
/// Agmen's own record of the sessions it hosted, newest first, in history.json. A tab goes in
/// when it closes, if it has a conversation to come back to.
/// </summary>
public static class SessionHistory
{
    private const int Limit = 300;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string FilePath => AppPaths.PathOf("history.json");

    public static List<HistoryEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) { Log.Write($"history.json unreadable: {ex.Message}"); }
            return new();
        }
    }

    /// <summary>Add sessions that just closed; an older entry for the same conversation gives way to the new one.</summary>
    public static void Record(IReadOnlyCollection<HistoryEntry> closed)
    {
        if (closed.Count == 0) return;
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(old => closed.Any(c => Same(old, c)));
            list.InsertRange(0, closed);
            Save(list.Take(Limit).ToList());
        }
    }

    /// <summary>Drop entries, such as sessions that are open in a tab again.</summary>
    public static void Forget(IReadOnlyCollection<HistoryEntry> entries)
    {
        if (entries.Count == 0) return;
        lock (Gate)
        {
            var list = Load();
            if (list.RemoveAll(old => entries.Any(e => Same(old, e))) > 0) Save(list);
        }
    }

    /// <summary>
    /// The sessions that closed together with this one because they were under it, parents
    /// before their children.
    /// </summary>
    public static List<HistoryEntry> ClosedUnder(HistoryEntry parent, IReadOnlyList<HistoryEntry> all)
    {
        var below = new List<HistoryEntry>();
        var seen = new HashSet<Guid> { parent.TabId };
        void Collect(HistoryEntry above)
        {
            foreach (var child in all.Where(e => e.ParentId == above.TabId
                                                 && (e.ClosedAt - above.ClosedAt).Duration() < TimeSpan.FromSeconds(10)))
            {
                if (!seen.Add(child.TabId)) continue;   // a damaged file can't send this round in circles
                below.Add(child);
                Collect(child);
            }
        }
        Collect(parent);
        return below;
    }

    private static bool Same(HistoryEntry a, HistoryEntry b) =>
        a.Agent == b.Agent && (string.IsNullOrEmpty(a.SessionId) || string.IsNullOrEmpty(b.SessionId)
            ? a.TabId == b.TabId && a.HandoffId == b.HandoffId : string.Equals(a.SessionId, b.SessionId, StringComparison.OrdinalIgnoreCase));

    private static void Save(List<HistoryEntry> list)
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Write($"history save failed: {ex.Message}"); }
    }
}
