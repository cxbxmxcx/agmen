using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Services;
using Brush = System.Windows.Media.Brush;

namespace Agmen;

/// <summary>
/// The history of sessions: the ones closed in Agmen, which come back in their old place in the
/// tree, and every agent's sessions from the past week, which come back as top-level tabs. A
/// session already open in a tab isn't listed.
/// </summary>
public partial class HistoryWindow : Window
{
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

    private readonly WorkspaceWindow _workspace;
    private List<Row> _rows = new();

    /// <summary>One session as the window lists it.</summary>
    private sealed class Row
    {
        public required AgentKind Agent { get; init; }
        public required string SessionId { get; init; }
        public required string Cwd { get; init; }
        public required string Title { get; init; }
        public required DateTime When { get; init; }
        /// <summary>Agmen's own entry, for a session closed here.</summary>
        public HistoryEntry? Closed { get; init; }
        /// <summary>The sessions that closed with it because they were under it.</summary>
        public IReadOnlyList<HistoryEntry> Below { get; init; } = Array.Empty<HistoryEntry>();
        /// <summary>The agent's own record, for a session found there.</summary>
        public FoundSession? Found { get; init; }

        public string AgentLabel => AgentProfiles.ShortName(Agent);
        public Brush AgentBrush => AgentProfiles.BadgeBrush(Agent);
        public string ReopenText => "Reopen";
        public string WithChildrenText => Below.Count == 1 ? "Reopen with the one under it" : $"Reopen with the {Below.Count} under it";
        public Visibility WithChildrenVisibility => Below.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ForgetVisibility => Closed is null ? Visibility.Collapsed : Visibility.Visible;
        public string Tooltip => $"{AgentProfiles.DisplayName(Agent)} session {SessionId}\n{Cwd}";
        public string SearchText => $"{Title} {Cwd} {AgentProfiles.DisplayName(Agent)} {AgentLabel} {Closed?.ParentTitle}";

        public string Detail
        {
            get
            {
                var parts = new List<string> { Cwd, (Closed is null ? "active " : "closed ") + Ago(When) };
                if (Closed?.ParentTitle is { Length: > 0 } parent) parts.Add($"was under {parent}");
                return string.Join("   ·   ", parts);
            }
        }
    }

    public HistoryWindow(WorkspaceWindow workspace)
    {
        InitializeComponent();
        _workspace = workspace;
        if (!AppPaths.IsDefaultWorkspace)
            HistoryDescription.Text = $"Reopen a conversation from the {AppPaths.WorkspaceName} workspace.";
        Loaded += async (_, _) => await LoadAsync();
        Activated += (_, _) => { if (_rows.Count > 0) Refresh(); };
    }

    private async Task LoadAsync()
    {
        // Agmen's own history shows at once; the agents' records can take a moment to read.
        Status.Text = "Looking through each agent's sessions…";
        BuildRows(SessionHistory.Load(), Array.Empty<FoundSession>());
        if (!AppPaths.IsDefaultWorkspace) return;
        var scope = WorkspaceSessions.Current;
        var found = await Task.Run(() => SessionDiscovery.Recent(Lookback).Where(s => scope.Visible(s.Agent, s.SessionId)).ToList());
        BuildRows(SessionHistory.Load(), found);
    }

    private List<FoundSession> _found = new();

    private void BuildRows(List<HistoryEntry> history, IReadOnlyList<FoundSession> found)
    {
        _found = found.ToList();
        var rows = new List<Row>();
        foreach (var entry in history)
        {
            if (AgentProfiles.Parse(entry.Agent) is not { } agent || _workspace.TabFor(agent, entry) is not null) continue;
            rows.Add(new Row
            {
                Agent = agent, SessionId = entry.SessionId, Cwd = entry.Cwd, Title = entry.Title, When = entry.ClosedAt.LocalDateTime,
                Closed = entry,
                Below = SessionHistory.ClosedUnder(entry, history)
                    .Where(e => AgentProfiles.Parse(e.Agent) is { } a && _workspace.TabFor(a, e) is null).ToList(),
            });
        }
        foreach (var f in found)
        {
            if (IsOpen(f.Agent, f.SessionId) || rows.Any(r => r.Agent == f.Agent && Same(r.SessionId, f.SessionId))) continue;
            rows.Add(new Row { Agent = f.Agent, SessionId = f.SessionId, Cwd = f.Cwd, Title = TitleOf(f), When = f.LastActive, Found = f });
        }
        _rows = rows.OrderByDescending(r => r.When).ToList();
        ApplyFilter();
    }

    /// <summary>Rebuild from the stored history, keeping what the agents' records gave, e.g. after reopening.</summary>
    private void Refresh() => BuildRows(SessionHistory.Load(), _found);

    private bool IsOpen(AgentKind agent, string sessionId) => _workspace.TabFor(agent, sessionId) is not null;

    private void ApplyFilter()
    {
        var terms = Search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _rows.Where(r => terms.All(t => r.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        Rows.ItemsSource = shown;
        int closed = _rows.Count(r => r.Closed is not null);
        Status.Text = _rows.Count == 0 ? "No sessions to reopen."
            : shown.Count == 0 ? "No sessions match."
            : $"{shown.Count} of {_rows.Count} sessions"
              + (closed > 0 ? $"   ·   {closed} closed in Agmen" : "")
              + "   ·   double-click one to reopen it";
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void OnReopen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Row row) Reopen(row, withChildren: false);
    }

    private void OnReopenWithChildren(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Row row) Reopen(row, withChildren: true);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double-click on one of the row's buttons is that button's business.
        for (var d = e.OriginalSource as DependencyObject; d is not null;
             d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Button) return;
        if (Rows.SelectedItem is Row row && e.OriginalSource is FrameworkElement { DataContext: Row }) Reopen(row, withChildren: false);
    }

    private void Reopen(Row row, bool withChildren)
    {
        if (row.Closed is { } entry) _workspace.Reopen(entry, withChildren ? row.Below : Array.Empty<HistoryEntry>());
        else if (row.Found is { } found) _workspace.Reopen(found);
        Refresh();
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row { Closed: { } entry }) return;
        SessionHistory.Forget(new[] { entry });
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>A session found in an agent's records is titled by its first prompt, else by its folder.</summary>
    private static string TitleOf(FoundSession f)
    {
        var title = Regex.Replace(f.Title, @"\s+", " ").Trim();
        if (title.Length == 0) title = Path.GetFileName(f.Cwd.TrimEnd('\\', '/'));
        return title.Length > 100 ? title[..99] + "…" : title;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Ago(DateTime when)
    {
        var age = DateTime.Now - when;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (when.Date == DateTime.Today) return $"today {when:HH:mm}";
        if (when.Date == DateTime.Today.AddDays(-1)) return $"yesterday {when:HH:mm}";
        if (age < TimeSpan.FromDays(7)) return $"{when:ddd HH:mm}";
        return $"{when:MMM d}";
    }
}
