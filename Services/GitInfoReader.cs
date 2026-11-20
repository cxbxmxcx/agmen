using System.IO;

namespace Agmen.Services;

/// <summary>
/// Reads the current branch and commit for a working directory straight from
/// .git files — no git.exe processes, cheap enough to run every poll.
/// </summary>
public static class GitInfoReader
{
    public static (string? Branch, string? Commit) Read(string cwd)
    {
        try
        {
            // Walk up from cwd to find the repo root (.git dir, or .git file for worktrees).
            string? dir = cwd;
            string? gitPath = null;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, ".git");
                if (Directory.Exists(candidate) || File.Exists(candidate)) { gitPath = candidate; break; }
                dir = Path.GetDirectoryName(dir);
            }
            if (gitPath is null) return (null, null);

            string gitDir = gitPath;
            if (File.Exists(gitPath)) // worktree: ".git" is a file pointing at the real gitdir
            {
                var content = File.ReadAllText(gitPath).Trim();
                if (!content.StartsWith("gitdir:")) return (null, null);
                gitDir = content["gitdir:".Length..].Trim();
                if (!Path.IsPathRooted(gitDir))
                    gitDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitPath)!, gitDir));
            }

            string headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile)) return (null, null);
            string head = File.ReadAllText(headFile).Trim();

            // Worktrees keep shared refs in the main repo's .git (the "commondir").
            string commonDir = gitDir;
            string commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                var c = File.ReadAllText(commonFile).Trim();
                commonDir = Path.IsPathRooted(c) ? c : Path.GetFullPath(Path.Combine(gitDir, c));
            }

            if (!head.StartsWith("ref:"))
                return ("detached", Short(head));

            string refName = head[4..].Trim(); // e.g. refs/heads/main
            string branch = refName.StartsWith("refs/heads/") ? refName["refs/heads/".Length..] : refName;

            string refFile = Path.Combine(commonDir, refName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile))
                return (branch, Short(File.ReadAllText(refFile).Trim()));

            string packed = Path.Combine(commonDir, "packed-refs");
            if (File.Exists(packed))
                foreach (var line in File.ReadLines(packed))
                    if (line.Length > 41 && line.EndsWith(" " + refName, StringComparison.Ordinal))
                        return (branch, Short(line[..40]));

            return (branch, null);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The origin remote's url and the repository name in it, read from .git/config. Handles
    /// https and ssh urls, including the …/_git/Repo form Azure Repos uses.
    /// </summary>
    public static (string? Url, string? Repository) ReadRemote(string cwd)
    {
        var remotes = ReadRemotes(cwd);
        var origin = remotes.FirstOrDefault(r => r.Name == "origin");
        if (origin.Url is null && remotes.Count > 0) origin = remotes[0];
        return (origin.Url, origin.Repository);
    }

    /// <summary>
    /// Every remote in .git/config. A clone can have more than one, such as an origin and a mirror.
    /// </summary>
    public static List<(string Name, string Url, string? Repository)> ReadRemotes(string cwd)
    {
        var result = new List<(string, string, string?)>();
        try
        {
            string? dir = cwd;
            string? gitPath = null;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, ".git");
                if (Directory.Exists(candidate) || File.Exists(candidate)) { gitPath = candidate; break; }
                dir = Path.GetDirectoryName(dir);
            }
            if (gitPath is null) return result;

            string gitDir = gitPath;
            if (File.Exists(gitPath))
            {
                var content = File.ReadAllText(gitPath).Trim();
                if (!content.StartsWith("gitdir:")) return result;
                gitDir = content["gitdir:".Length..].Trim();
                if (!Path.IsPathRooted(gitDir))
                    gitDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitPath)!, gitDir));
                var commonFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonFile))
                {
                    var c = File.ReadAllText(commonFile).Trim();
                    gitDir = Path.IsPathRooted(c) ? c : Path.GetFullPath(Path.Combine(gitDir, c));
                }
            }

            var config = Path.Combine(gitDir, "config");
            if (!File.Exists(config)) return result;
            string? remote = null;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    // [remote "origin"]
                    var m = System.Text.RegularExpressions.Regex.Match(line, "^\\[remote\\s+\"([^\"]+)\"\\]");
                    remote = m.Success ? m.Groups[1].Value : null;
                    continue;
                }
                if (remote is null || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var value = line[(eq + 1)..].Trim();
                if (value.Length > 0) result.Add((remote, value, RepositoryName(value)));
            }
        }
        catch (Exception) { /* unreadable config: no remotes */ }
        return result;
    }

    private static string? RepositoryName(string url)
    {
        var u = url.TrimEnd('/');
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        int git = u.IndexOf("/_git/", StringComparison.OrdinalIgnoreCase);
        if (git >= 0) return Uri.UnescapeDataString(u[(git + "/_git/".Length)..].Split('/')[0]);
        int cut = Math.Max(u.LastIndexOf('/'), u.LastIndexOf(':'));
        return cut >= 0 && cut < u.Length - 1 ? Uri.UnescapeDataString(u[(cut + 1)..]) : null;
    }

    private static string Short(string hash) => hash.Length >= 7 ? hash[..7] : hash;
}
