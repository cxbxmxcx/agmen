using System.IO;
using Agmen.Services;

namespace Agmen.Tests;

internal static class NamedWorkspaceChecks
{
    internal static void Run()
    {
        string family = AppPaths.FamilyName;
        string originalDirectory = AppPaths.DataDir;
        var first = new WorkspaceIdentity(family, " Research Notes ");
        var alternative = new WorkspaceIdentity(family, "research notes");
        var other = new WorkspaceIdentity(family, "Review");
        if (first.Instance != alternative.Instance || first.Instance == other.Instance || first.DataDir == other.DataDir
            || new WorkspaceIdentity(family + "-other", first.Name).Instance == first.Instance)
            throw new Exception("Workspace names must ignore case and distinguish families and workspaces.");
        if (new WorkspaceIdentity(family, "deFAult").DataDir != originalDirectory
            || new WorkspaceIdentity(family, "default").Instance != family)
            throw new Exception("Default must retain the existing data directory and single-instance identity.");
        if (NamedWorkspaces.RequestedName(["--workspace", "--open", "C:\\project"]) != null
            || NamedWorkspaces.RequestedName(["--workspace", " Research Notes ", "--open", "C:\\project"]) != first.Name)
            throw new Exception("Bare and named --workspace options must coexist with normal launch arguments.");
        foreach (var invalid in new[] { "", "..", "x/y", "--review", "x\\z", new string('q', 65) })
        {
            try { _ = WorkspaceIdentity.ValidateName(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception($"Accepted invalid workspace name '{invalid}'.");
        }

        Parallel.For(0, 12, i => NamedWorkspaces.Remember(i % 2 == 0 ? first : alternative));
        if (NamedWorkspaces.SavedNames().Count(n => n.Equals(first.Name, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new Exception("Concurrent launches created duplicate saved workspaces.");
        try
        {
            AppPaths.SelectWorkspace(first.Name);
            if (AppPaths.InstanceName != first.Instance || WorkspaceIdentity.FromEnvironment().Instance != first.Instance
                || AppPaths.OwnsGlobalHotkeys || AppPaths.IsDefaultWorkspace)
                throw new Exception("A named workspace did not pass its identity to hosted processes.");
            File.WriteAllText(AppPaths.PathOf("workspace-check.txt"), "research");
            AppPaths.SelectWorkspace(other.Name);
            if (File.Exists(AppPaths.PathOf("workspace-check.txt")) || AppPaths.InstanceName == first.Instance)
                throw new Exception("Workspace state crossed into a different name.");
            AppPaths.SelectWorkspace(alternative.Name);
            if (File.ReadAllText(AppPaths.PathOf("workspace-check.txt")) != "research")
                throw new Exception("Reopening a workspace did not recover its state.");
        }
        finally { AppPaths.SelectWorkspace(WorkspaceIdentity.DefaultName); }
        if (AppPaths.DataDir != originalDirectory || !AppPaths.IsDefaultWorkspace)
            throw new Exception("Reopening Default changed its data location.");
    }
}
