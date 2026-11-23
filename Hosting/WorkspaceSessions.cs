using System.IO;
using System.Security.Cryptography;
using System.Text;
using Agmen.Agents;
using Agmen.Services;

namespace Agmen.Hosting;

/// <summary>Native CLIs share their user-level transcript stores. Associate each managed conversation
/// with its workspace, so simultaneous restores cannot open it in two separate terminals.</summary>
internal sealed class WorkspaceSessions(string familyDirectory, string workspace, bool isDefault)
{
    internal static WorkspaceSessions Current => new(AppPaths.FamilyDir, AppPaths.WorkspaceName, AppPaths.IsDefaultWorkspace);

    internal bool Visible(AgentKind agent, string sessionId)
    {
        string? owner = Owner(Record(agent, sessionId));
        return owner == null ? isDefault : string.Equals(owner, workspace, StringComparison.OrdinalIgnoreCase);
    }

    internal string? OtherOwner(AgentKind agent, string sessionId)
    {
        string? owner = Owner(Record(agent, sessionId));
        return string.Equals(owner, workspace, StringComparison.OrdinalIgnoreCase) ? null : owner;
    }

    internal void Claim(AgentKind agent, string sessionId)
    {
        var path = Record(agent, sessionId);
        string? owner = Owner(path);
        if (owner == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, workspace);
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { /* a concurrent launch published its claim */ }
            }
            finally { File.Delete(temp); }
            owner = Owner(path);
        }
        if (!string.Equals(owner, workspace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"This conversation belongs to the '{owner}' workspace. Open that workspace to resume it.");
    }

    private string Record(AgentKind agent, string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(AgentProfiles.Id(agent) + "/" + id.ToLowerInvariant()));
        return Path.Combine(familyDirectory, "session-owners", Convert.ToHexString(hash) + ".txt");
    }

    private static string? Owner(string path)
    {
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
