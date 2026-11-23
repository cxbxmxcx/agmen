using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Agmen.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Agmen;

public partial class WorkspaceWindow
{
    internal IEnumerable<string> WorkspaceNames()
    {
        try { return NamedWorkspaces.SavedNames().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice($"Could not list saved workspaces. {ex.Message}");
            return [AppPaths.WorkspaceName];
        }
    }

    internal void OpenNamedWorkspace(string name)
    {
        if (name.Equals(AppPaths.WorkspaceName, StringComparison.OrdinalIgnoreCase)) { ShowWorkspace(); return; }
        try
        {
            // Register it before spawning so an inaccessible data folder can be reported in this window.
            var target = NamedWorkspaces.Remember(new WorkspaceIdentity(AppPaths.FamilyName, name));
            NamedWorkspaces.Launch(target.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            Notice($"Could not open workspace '{name}'. {ex.Message}");
        }
    }

    internal void NewNamedWorkspace()
    {
        var dialog = new NewWorkspaceWindow { WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen };
        if (IsVisible) dialog.Owner = this;
        if (dialog.ShowDialog() == true) OpenNamedWorkspace(dialog.WorkspaceName);
    }

    private void OnWorkspacesClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var create = new MenuItem { Header = "New workspace…" };
        create.Click += (_, _) => NewNamedWorkspace();
        menu.Items.Add(create);
        menu.Items.Add(new Separator());
        foreach (string name in WorkspaceNames())
        {
            var item = new MenuItem { Header = name.Replace("_", "__"), IsChecked = name == AppPaths.WorkspaceName };
            item.Click += (_, _) => OpenNamedWorkspace(name);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
