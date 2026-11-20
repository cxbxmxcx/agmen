using System.IO;
using Agmen.Services;

namespace Agmen.Orchestration;

/// <summary>
/// Local folders where Claude Code has been used, with their git remotes, so the primary
/// can find the clone of a repository before opening a session in it.
/// </summary>
public static class ProjectIndex
{
    /// <summary>A local folder, its origin remote, and the urls of any other remotes, such as a GitHub mirror.</summary>
    public sealed record Project(string Folder, string? Remote, string? Repository, string? Branch, DateTime LastUsed,
                                 IReadOnlyList<string> OtherRemotes);

    private static readonly object Gate = new();
    private static List<Project>? _cache;
    private static DateTime _cachedAt;

    private static string ProjectsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static List<Project> Scan()
    {
        lock (Gate)
            if (_cache is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(2)) return _cache;

        var byFolder = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(ProjectsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
            {
                try
                {
                    var newest = new DirectoryInfo(dir).EnumerateFiles("*.jsonl")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (newest is null) continue;
                    var cwd = RestoreService.TranscriptLaunchCwd(newest.FullName) ?? RestoreService.TranscriptCwd(newest.FullName);
                    if (cwd is null || !Directory.Exists(cwd)) continue;
                    if (byFolder.TryGetValue(cwd, out var seen) && seen.LastUsed >= newest.LastWriteTime) continue;
                    var (branch, _) = GitInfoReader.Read(cwd);
                    var (remote, repo) = GitInfoReader.ReadRemote(cwd);
                    var others = GitInfoReader.ReadRemotes(cwd).Select(r => r.Url).Where(u => u != remote)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    byFolder[cwd] = new Project(cwd, remote, repo, branch, newest.LastWriteTime, others);
                }
                catch (Exception) { /* unreadable project folder */ }
            }
        }

        var result = byFolder.Values.OrderByDescending(p => p.LastUsed).ToList();
        lock (Gate)
        {
            _cache = result;
            _cachedAt = DateTime.UtcNow;
        }
        return result;
    }
}
