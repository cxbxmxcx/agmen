using System.Windows;
using System.Windows.Controls;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Agmen;

/// <summary>
/// The session tree. A tab has at most one parent: the session that started it, or the one the
/// user put it under. A parent can have many children, and children can have their own. The
/// tab list is kept in tree order, each parent followed by its children, so the sidebar shows
/// the tree as indented cards that a parent can fold away.
/// </summary>
public partial class WorkspaceWindow
{
    /// <summary>How deep the tree may go below a top-level session.</summary>
    public const int MaxDepth = 4;
    /// <summary>How many children one session may have at a time.</summary>
    public const int MaxChildren = 12;

    public HostedTab? ParentOf(HostedTab tab) => tab.ParentId is { } id ? _tabs.FirstOrDefault(t => t.Id == id) : null;

    public List<HostedTab> ChildrenOf(HostedTab tab) => _tabs.Where(t => t.ParentId == tab.Id).ToList();

    public int DepthOf(HostedTab tab)
    {
        int depth = 0;
        for (var p = ParentOf(tab); p is not null && depth <= 32; p = ParentOf(p)) depth++;
        return depth;
    }

    private IEnumerable<HostedTab> DescendantsOf(HostedTab tab)
    {
        foreach (var child in ChildrenOf(tab))
        {
            yield return child;
            foreach (var below in DescendantsOf(child)) yield return below;
        }
    }

    /// <summary>
    /// Start a session under a parent. A first message goes in the way the agent takes it best:
    /// typed in when Claude Code or Kimi is ready, or on the command line for Codex and
    /// Antigravity. Either way the parent hears when the turn it starts is over. A model or
    /// effort given here is the child's own; otherwise it runs on the agent's settings.
    /// </summary>
    public HostedTab OpenChild(HostedTab parent, AgentKind agent, string cwd, string name, string? firstMessage,
                               string? resumeId, bool select = false, string? model = null, string? effort = null)
    {
        var tab = new HostedTab(cwd, resumeId, knownTitle: name, agent: agent)
        {
            LaunchName = agent == AgentKind.Claude && resumeId is null ? name : null,
            ParentId = parent.Id,
            LaunchModel = model,
            LaunchEffort = effort,
        };
        if (firstMessage is not null)
        {
            if (resumeId is null && agent is AgentKind.Codex or AgentKind.Antigravity or AgentKind.Kimi)
            {
                tab.InitialPrompt = firstMessage;
                tab.ReportFirstPromptTo = parent.Id;
            }
            else tab.Enqueue(new OutboxMessage { Text = firstMessage, Origin = "parent", ReportTo = parent.Id });
        }
        AddTab(tab, select);
        StartTab(tab);
        Log.Write($"tree: {parent.Title} started {name} ({AgentProfiles.DisplayName(agent)}) in {cwd}");
        return tab;
    }

    /// <summary>A name no other tab uses: codex-1, codex-2 and so on.</summary>
    public string UniqueName(AgentKind agent)
    {
        var prefix = AgentProfiles.ShortName(agent).ToLowerInvariant();
        for (int n = 1; ; n++)
        {
            var name = $"{prefix}-{n}";
            if (!NameTaken(name)) return name;
        }
    }

    /// <summary>The name itself when no tab uses it, otherwise the next free one: claude-1 becomes claude-2, notes becomes notes-2.</summary>
    public string FreeName(string name)
    {
        if (!NameTaken(name)) return name;
        var stem = name;
        int n = 2;
        int dash = name.LastIndexOf('-');
        if (dash > 0 && int.TryParse(name[(dash + 1)..], out var last))
        {
            stem = name[..dash];
            n = last + 1;
        }
        while (NameTaken($"{stem}-{n}")) n++;
        return $"{stem}-{n}";
    }

    private bool NameTaken(string name) => _tabs.Any(t => string.Equals(t.Title, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a tab may go under another: never under itself or its own descendants, and never too deep.</summary>
    public bool CanMoveUnder(HostedTab tab, HostedTab parent)
    {
        if (tab == parent || tab.IsPrimary || DescendantsOf(tab).Contains(parent)) return false;
        int subtreeDepth = DescendantsOf(tab).Select(d => DepthOf(d) - DepthOf(tab)).DefaultIfEmpty(0).Max();
        return DepthOf(parent) + 1 + subtreeDepth <= MaxDepth;
    }

    /// <summary>Put a tab under another, or at the top level with null; its own children come with it.</summary>
    public void MoveUnder(HostedTab tab, HostedTab? parent)
    {
        if (parent is not null && !CanMoveUnder(tab, parent)) return;
        tab.ParentId = parent?.Id;
        ReorderTree();
        RefreshTree();
        RequestSave();
        Log.Write($"tree: {tab.Title} moved {(parent is null ? "to the top level" : $"under {parent.Title}")}");
    }

    /// <summary>
    /// Keep the tab list in tree order: the primary first, then each top-level session followed
    /// by its children, depth first. A parent that is gone leaves its children at the top level.
    /// </summary>
    private void ReorderTree()
    {
        foreach (var t in _tabs)
            if (t.ParentId is not null && ParentOf(t) is null) t.ParentId = null;

        var ordered = new List<HostedTab>();
        void Visit(HostedTab t)
        {
            if (ordered.Contains(t)) return;
            ordered.Add(t);
            foreach (var child in _tabs.Where(c => c.ParentId == t.Id).ToList()) Visit(child);
        }
        foreach (var root in _tabs.Where(t => t.ParentId is null).OrderByDescending(t => t.IsPrimary).ToList()) Visit(root);
        // Anything left over sits in a loop of parents; break it at the top level.
        foreach (var t in _tabs.Where(t => !ordered.Contains(t)).ToList())
        {
            t.ParentId = null;
            Visit(t);
        }
        for (int i = 0; i < ordered.Count; i++)
        {
            int at = _tabs.IndexOf(ordered[i]);
            if (at != i) _tabs.Move(at, i);
        }
    }

    /// <summary>Recompute each card's depth, child count, parent's name, and whether a folded parent hides it.</summary>
    private void RefreshTree()
    {
        foreach (var t in _tabs)
        {
            int depth = 0;
            bool hidden = false;
            for (var p = ParentOf(t); p is not null && depth <= 32; p = ParentOf(p))
            {
                depth++;
                if (p.IsCollapsed) hidden = true;
            }
            t.SetTreePlace(depth, _tabs.Count(c => c.ParentId == t.Id), hidden, ParentOf(t)?.Title);
        }
        // A tab folded away can't stay selected; its visible parent takes its place.
        if (SelectedTab is { HiddenByCollapse: true } hiddenTab)
        {
            var p = ParentOf(hiddenTab);
            while (p is { HiddenByCollapse: true }) p = ParentOf(p);
            if (p is not null) Select(p);
        }
    }

    private void ToggleChildren(HostedTab tab)
    {
        tab.IsCollapsed = !tab.IsCollapsed;
        RefreshTree();
        RequestSave();
    }

    private void OnCardToggleChildren(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is HostedTab tab) ToggleChildren(tab);
    }

    /// <summary>The tree items on a tab's menu: new child, move under, fold children.</summary>
    private void AddTreeMenuItems(ContextMenu menu)
    {
        var newChild = new MenuItem { Header = "New child session" };
        foreach (var agent in AgentProfiles.All.Where(AgentProfiles.IsInstalled))
        {
            var a = agent;
            var item = new MenuItem { Header = AgentProfiles.DisplayName(a) };
            item.Click += (_, _) =>
            {
                if (SelectedTab is not { } parent) return;
                if (ChildrenOf(parent).Count >= MaxChildren || DepthOf(parent) >= MaxDepth)
                {
                    Notice($"{parent.Title} can't take another child: at most {MaxChildren} children, {MaxDepth} levels deep.");
                    return;
                }
                OfferConnect(a);
                OpenChild(parent, a, parent.Cwd, UniqueName(a), null, null, select: true);
            };
            newChild.Items.Add(item);
        }
        menu.Items.Add(newChild);

        var moveUnder = new MenuItem { Header = "Move under" };
        menu.Items.Add(moveUnder);
        var fold = new MenuItem();
        fold.Click += (_, _) => { if (SelectedTab is { HasChildren: true } t) ToggleChildren(t); };
        menu.Items.Add(fold);

        menu.Opened += (_, _) =>
        {
            var tab = SelectedTab;
            newChild.IsEnabled = tab is { IsEnded: false };
            fold.Visibility = tab is { HasChildren: true } ? Visibility.Visible : Visibility.Collapsed;
            fold.Header = tab is { IsCollapsed: true } ? "Show its children" : "Fold its children away";
            moveUnder.Items.Clear();
            moveUnder.IsEnabled = tab is { IsPrimary: false };
            if (tab is null || tab.IsPrimary) return;
            var top = new MenuItem { Header = "Top level", IsEnabled = tab.ParentId is not null };
            top.Click += (_, _) => MoveUnder(tab, null);
            moveUnder.Items.Add(top);
            moveUnder.Items.Add(new Separator());
            foreach (var candidate in _tabs.Where(c => c != tab && c.Id != tab.ParentId && CanMoveUnder(tab, c)))
            {
                var parent = candidate;
                var item = new MenuItem { Header = new string(' ', DepthOf(parent) * 3) + parent.DisplayTitle };
                item.Click += (_, _) => MoveUnder(tab, parent);
                moveUnder.Items.Add(item);
            }
        };
    }
}
