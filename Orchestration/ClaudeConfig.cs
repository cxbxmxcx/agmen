using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agmen.Orchestration;

/// <summary>
/// Read-only view of Claude Code's own configuration in ~/.claude.json: which folders the
/// user has trusted. Claude Code rewrites the file often, so it is re-read only when it
/// changes, and a read that catches it mid-write keeps the last good copy.
/// </summary>
public static class ClaudeConfig
{
    private static readonly object Gate = new();
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    private static readonly string ProjectsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private static DateTime _stamp;
    private static HashSet<string> _trusted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's home folder, where Claude's trust lasts only for the session.</summary>
    public static bool IsHome(string folder) =>
        string.Equals(Path.GetFullPath(folder).TrimEnd('\\', '/'),
                      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/'),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when Claude Code will start in this folder without asking the user to trust it:
    /// the folder or a parent was trusted, or Claude has already been used there. Text typed
    /// into a new session must never land on that trust question. Claude never remembers
    /// trust for the home folder, so it asks there every time it starts.
    /// </summary>
    public static bool IsTrusted(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || IsHome(folder)) return false;
        Refresh();
        HashSet<string> trusted;
        lock (Gate) trusted = _trusted;
        for (var dir = Normalize(folder); !string.IsNullOrEmpty(dir); dir = Parent(dir))
            if (trusted.Contains(dir)) return true;
        var slug = Regex.Replace(folder.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");
        try { return Directory.Exists(Path.Combine(ProjectsDir, slug)); }
        catch (Exception) { return false; }
    }

    private static void Refresh()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) return;
            lock (Gate)
                if (info.LastWriteTimeUtc == _stamp) return;

            JsonNode? root;
            using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                root = JsonNode.Parse(fs);
            if (root is not JsonObject obj) return;

            var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (obj["projects"] is JsonObject projects)
                foreach (var (path, value) in projects)
                    if (value is JsonObject p && p["hasTrustDialogAccepted"] is JsonValue v
                        && v.TryGetValue<bool>(out var accepted) && accepted)
                        trusted.Add(Normalize(path));

            lock (Gate)
            {
                _trusted = trusted;
                _stamp = info.LastWriteTimeUtc;
            }
        }
        catch (Exception)
        {
            // Caught Claude Code mid-write, or the file is locked. Keep what we had and try next time.
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static string Parent(string normalized)
    {
        int cut = normalized.LastIndexOf('/');
        return cut <= 0 ? "" : normalized[..cut];
    }
}
