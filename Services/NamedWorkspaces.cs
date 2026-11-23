using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Agmen.Services;

internal sealed class WorkspaceIdentity
{
    internal const string DefaultName = "Default";
    internal string Family { get; }
    internal string Name { get; }
    internal string Instance { get; }
    internal string FamilyDir { get; }
    internal string DataDir { get; }
    internal bool IsDefault => Name == DefaultName;

    internal WorkspaceIdentity(string family, string name)
    {
        Family = family;
        Name = ValidateName(name);
        FamilyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), family);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(family.ToUpperInvariant() + "\n" + Name.ToUpperInvariant())));
        Instance = IsDefault ? family : "Agmen-ws-" + key;
        DataDir = IsDefault ? FamilyDir : Path.Combine(FamilyDir, "workspaces", key);
    }

    internal static string ValidateName(string text)
    {
        string name = text.Trim().Normalize();
        if (name.Length is 0 or > 64 || name.StartsWith("--", StringComparison.Ordinal)
            || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Use a workspace name of 1–64 characters without path separators or Windows filename punctuation.");
        return name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) ? DefaultName : name;
    }

    private static bool ValidFamily(string? text) => text is { Length: > 0 }
        && text.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    internal static WorkspaceIdentity FromEnvironment()
    {
        var instance = Environment.GetEnvironmentVariable("AGMEN_INSTANCE");
        var family = Environment.GetEnvironmentVariable("AGMEN_WORKSPACE_FAMILY");
        var name = Environment.GetEnvironmentVariable("AGMEN_WORKSPACE_NAME");
        if (ValidFamily(family) && !string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var inherited = new WorkspaceIdentity(family!, name);
                if (inherited.Instance.Equals(instance, StringComparison.OrdinalIgnoreCase)) return inherited;
            }
            catch (ArgumentException) { /* a launcher supplied an invalid inherited workspace name */ }
        }
        return new WorkspaceIdentity(ValidFamily(instance) ? instance! : AppPaths.IsDev ? "Agmen-dev" : "Agmen", DefaultName);
    }
}

internal static class NamedWorkspaces
{
    private const string NameFile = "name.txt";

    // A marker per workspace preserves the first display spelling and lets concurrent creates
    // in other processes succeed independently. A default-only installation needs no migration.
    internal static WorkspaceIdentity Remember(WorkspaceIdentity workspace)
    {
        if (workspace.IsDefault) return workspace;
        Directory.CreateDirectory(workspace.DataDir);
        string marker = Path.Combine(workspace.DataDir, NameFile);
        if (!File.Exists(marker))
        {
            var temporary = Path.Combine(workspace.DataDir, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, workspace.Name);
                try { File.Move(temporary, marker); }
                catch (IOException) when (File.Exists(marker)) { /* a concurrent launch remembered the name first */ }
            }
            finally { File.Delete(temporary); }
        }
        var stored = new WorkspaceIdentity(workspace.Family, File.ReadAllText(marker));
        if (stored.Instance != workspace.Instance) throw new InvalidDataException("The saved workspace name does not match its folder.");
        return stored;
    }

    internal static IEnumerable<string> SavedNames()
    {
        var names = new List<string> { WorkspaceIdentity.DefaultName };
        string folder = Path.Combine(AppPaths.FamilyDir, "workspaces");
        if (!Directory.Exists(folder)) return names;
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            try
            {
                var info = new WorkspaceIdentity(AppPaths.FamilyName, File.ReadAllText(Path.Combine(dir, NameFile)));
                if (string.Equals(dir, info.DataDir, StringComparison.OrdinalIgnoreCase)) names.Add(info.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { /* Skip a workspace being created or an unreadable marker. */ }
        }
        return names.Take(1).Concat(names.Skip(1).Order(StringComparer.CurrentCultureIgnoreCase));
    }

    internal static string? RequestedName(string[] args)
    {
        string? requested = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--workspace", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) continue;
            if (requested != null) throw new ArgumentException("Choose one workspace per launch.");
            requested = WorkspaceIdentity.ValidateName(args[++i]);
        }
        return requested;
    }

    internal static void Launch(string name)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Could not find the Agmen executable."))
        {
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--workspace");
        start.ArgumentList.Add(WorkspaceIdentity.ValidateName(name));
        start.Environment["AGMEN_INSTANCE"] = AppPaths.FamilyName;
        start.Environment.Remove("AGMEN_WORKSPACE_NAME");
        start.Environment.Remove("AGMEN_WORKSPACE_FAMILY");
        using var launched = Process.Start(start);
    }
}
