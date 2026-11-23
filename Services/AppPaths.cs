using System.IO;

namespace Agmen.Services;

/// <summary>Workspace identity and the files used by this process and its hosted agents.</summary>
public static class AppPaths
{
#if DEBUG
    public static bool IsDev => true;
#else
    public static bool IsDev => false;
#endif

    private static WorkspaceIdentity _current = WorkspaceIdentity.FromEnvironment();
    public static string FamilyName => _current.Family;
    public static string FamilyDir => _current.FamilyDir;
    public static string WorkspaceName => _current.Name;
    public static bool IsDefaultWorkspace => _current.IsDefault;
    public static string InstanceName => _current.Instance;
    public static string DataDir => _current.DataDir;
    public static bool OwnsGlobalHotkeys => !IsDev && IsDefaultWorkspace && FamilyName == "Agmen";
    public static string WorkspaceTitle
    {
        get
        {
            var app = FamilyName switch { "Agmen" => "Agmen", "Agmen-dev" => "Agmen (dev)", var other => $"Agmen ({other})" };
            return IsDefaultWorkspace ? app : $"{WorkspaceName} — {app}";
        }
    }

    // Called once by a GUI launch, before acquiring the workspace's mutex or starting any agents.
    internal static void SelectWorkspace(string? requested)
    {
        var next = requested == null ? _current : new WorkspaceIdentity(FamilyName, requested);
        _current = NamedWorkspaces.Remember(next);
        Environment.SetEnvironmentVariable("AGMEN_INSTANCE", InstanceName);
        Environment.SetEnvironmentVariable("AGMEN_WORKSPACE_FAMILY", FamilyName);
        Environment.SetEnvironmentVariable("AGMEN_WORKSPACE_NAME", WorkspaceName);
    }

    public static string PathOf(string fileName)
    {
        Directory.CreateDirectory(DataDir);
        return Path.Combine(DataDir, fileName);
    }
}
