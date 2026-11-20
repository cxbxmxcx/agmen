using System.IO;
using System.Text.Json;
using Agmen.Models;

namespace Agmen.Services;

/// <summary>
/// Remembers Claude Code sessions so they can be reopened as workspace tabs:
/// a continuously updated "last known alive" list for reboot recovery, a
/// deliberately saved session set, and discovery straight from Claude Code's
/// own transcripts for when Agmen was not running before a crash.
/// Launching itself is the workspace's job.
/// </summary>
public static class RestoreService
{
    public sealed record Entry(string SessionId, string Cwd, string Name);
    public sealed record SavedSet(DateTimeOffset SavedAt, List<Entry> Entries);
    public sealed record RecentSession(Entry Entry, DateTime LastActive);

    private static string RestorePath => AppPaths.PathOf("restore.json");
    private static string SetPath => AppPaths.PathOf("saved-set.json");
    private static string ProjectsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private static string? _lastWritten;

    // ------------------------------------------------------ reboot recovery

    public static List<Entry> LoadPending()
    {
        try
        {
            if (!File.Exists(RestorePath)) return new();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(RestorePath)) ?? new();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>
    /// Persist the current live CLI session set (only when it changed).
    /// Pending not-yet-restored entries are kept so they survive an Agmen
    /// restart that happens before the user clicks Restore.
    /// </summary>
    public static void SaveCurrent(IEnumerable<SessionInfo> live, IEnumerable<Entry> pending)
    {
        try
        {
            var entries = live
                .Where(s => s.Entrypoint == "cli" && !string.IsNullOrEmpty(s.SessionId))
                .Select(s => new Entry(s.SessionId, s.Cwd, s.Name))
                .Concat(pending)
                .DistinctBy(e => e.SessionId)
                .OrderBy(e => e.SessionId)
                .ToList();
            var json = JsonSerializer.Serialize(entries);
            if (json == _lastWritten) return;
            File.WriteAllText(RestorePath, json);
            _lastWritten = json;
        }
        catch (Exception) { /* best-effort */ }
    }

    // ------------------------------------------ recovery from transcripts

    /// <summary>
    /// CLI sessions whose transcript was written within <paramref name="window"/>.
    /// Claude Code writes transcripts itself, so this works even when
    /// Agmen wasn't running before a crash.
    /// </summary>
    public static List<RecentSession> RecentFromTranscripts(TimeSpan window)
    {
        var cutoff = DateTime.Now - window;
        var result = new List<RecentSession>();
        if (!Directory.Exists(ProjectsDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.jsonl"); } // top level only: subagent transcripts live in subfolders
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTime < cutoff) continue;
                    var entry = EntryFromTranscript(file);
                    if (entry is not null) result.Add(new RecentSession(entry, fi.LastWriteTime));
                }
                catch (Exception) { /* unreadable transcript — skip */ }
            }
        }
        return result.OrderByDescending(r => r.LastActive).ToList();
    }

    /// <summary>
    /// True when Claude Code has saved a conversation for this session. A brand-new session
    /// writes nothing until its first message, so it cannot be resumed with --resume.
    /// </summary>
    public static bool HasTranscript(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out _) || !Directory.Exists(ProjectsDir)) return false;
        foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
            if (File.Exists(Path.Combine(dir, sessionId + ".jsonl"))) return true;
        return false;
    }

    /// <summary>The transcript file of a session, looking first where its folder says it should be.</summary>
    public static string? FindTranscriptPath(string sessionId, string? cwd)
    {
        if (!Guid.TryParse(sessionId, out _) || !Directory.Exists(ProjectsDir)) return null;
        if (!string.IsNullOrEmpty(cwd))
        {
            var slug = System.Text.RegularExpressions.Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
            var derived = Path.Combine(ProjectsDir, slug, sessionId + ".jsonl");
            if (File.Exists(derived)) return derived;
        }
        foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
        {
            var file = Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>The working folder recorded in a transcript, or null when it has none.</summary>
    public static string? TranscriptCwd(string transcriptPath) => ReadTranscriptMeta(transcriptPath).Cwd;

    /// <summary>
    /// The folder a session started in, from the first records of its transcript. Reads only
    /// the head of the file, so scanning every project stays quick.
    /// </summary>
    public static string? TranscriptLaunchCwd(string transcriptPath)
    {
        try
        {
            var head = ReadChunk(transcriptPath, fromEnd: false, size: 64 * 1024);
            if (head is null) return null;
            foreach (var line in head.Split('\n'))
            {
                if (line.Length < 2 || !line.Contains("\"cwd\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (GetStr(doc.RootElement, "cwd") is { } cwd) return cwd;
                }
                catch (JsonException) { /* the last line may be cut off */ }
            }
        }
        catch (Exception) { /* unreadable transcript */ }
        return null;
    }

    /// <summary>Find a session's working folder and name from its transcript, wherever it lives.</summary>
    public static Entry? FindSession(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out _) || !Directory.Exists(ProjectsDir)) return null;
        foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
        {
            var file = Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(file)) return EntryFromTranscript(file);
        }
        return null;
    }

    /// <summary>A resumable CLI session from a transcript file, or null for desktop sessions and vanished folders.</summary>
    private static Entry? EntryFromTranscript(string file)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file);
        if (!Guid.TryParse(sessionId, out _)) return null;
        var (cwd, entrypoint, slug) = ReadTranscriptMeta(file);
        if (cwd is null || !Directory.Exists(cwd)) return null;
        if (entrypoint is not null && entrypoint != "cli") return null;
        return new Entry(sessionId, cwd, slug ?? Path.GetFileName(cwd.TrimEnd('\\', '/')));
    }

    /// <summary>
    /// Pull cwd / entrypoint / slug from a transcript without reading the whole file: most
    /// records carry them, so the tail usually suffices and the head is the fallback. A
    /// record's cwd follows the session's shell as it changes directory, so prefer one that
    /// matches the project folder the transcript lives in: that is where the session started,
    /// and where it should resume.
    /// </summary>
    private static (string? Cwd, string? Entrypoint, string? Slug) ReadTranscriptMeta(string path)
    {
        var projectSlug = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        (string? Cwd, string? Entrypoint, string? Slug)? fallback = null;
        foreach (bool fromEnd in new[] { true, false })
        {
            var chunk = ReadChunk(path, fromEnd);
            if (chunk is null) continue;
            var lines = chunk.Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                var line = lines[fromEnd ? lines.Length - 1 - n : n];
                if (line.Length < 2 || !line.Contains("\"cwd\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    string? cwd = GetStr(r, "cwd");
                    if (cwd is null) continue;
                    var meta = (cwd, GetStr(r, "entrypoint"), GetStr(r, "slug"));
                    if (System.Text.RegularExpressions.Regex.Replace(cwd, "[^A-Za-z0-9]", "-") == projectSlug) return meta;
                    fallback ??= meta;
                }
                catch (JsonException) { /* partial line at chunk boundary */ }
            }
        }
        return fallback ?? (null, null, null);
    }

    private static string? GetStr(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ReadChunk(string path, bool fromEnd, int size = 256 * 1024)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        if (len == 0) return null;
        int take = (int)Math.Min(size, len);
        fs.Seek(fromEnd ? len - take : 0, SeekOrigin.Begin);
        var buf = new byte[take];
        int read = 0;
        while (read < take)
        {
            int n = fs.Read(buf, read, take - read);
            if (n == 0) break;
            read += n;
        }
        return System.Text.Encoding.UTF8.GetString(buf, 0, read);
    }

    // ------------------------------------------------------ named session set

    /// <summary>Deliberately remember the current CLI sessions for later relaunch.</summary>
    public static int SaveSet(IEnumerable<SessionInfo> live)
    {
        var entries = live
            .Where(s => s.Entrypoint == "cli" && !string.IsNullOrEmpty(s.SessionId))
            .Select(s => new Entry(s.SessionId, s.Cwd, s.Name))
            .ToList();
        if (entries.Count == 0) return 0;
        File.WriteAllText(SetPath, JsonSerializer.Serialize(
            new SavedSet(DateTimeOffset.Now, entries),
            new JsonSerializerOptions { WriteIndented = true }));
        return entries.Count;
    }

    public static SavedSet? LoadSet()
    {
        try
        {
            if (!File.Exists(SetPath)) return null;
            return JsonSerializer.Deserialize<SavedSet>(File.ReadAllText(SetPath));
        }
        catch (Exception) { return null; }
    }
}
