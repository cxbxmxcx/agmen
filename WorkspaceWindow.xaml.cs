using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Agmen.Agents;
using Agmen.Hosting;
using Agmen.Models;
using Agmen.Services;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace Agmen;

/// <summary>
/// The tabbed workspace. Every Claude Code session Agmen starts lives here,
/// one terminal per tab, with the session cards serving as a vertical tab strip.
/// </summary>
public partial class WorkspaceWindow : Window
{
    private const int HotkeyId = 0xC1AE;
    private const int WM_KEYDOWN = 0x0100, WM_HOTKEY = 0x0312;
    private const int VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_LEFT = 0x25, VK_DOWN = 0x28, VK_0 = 0x30, VK_4 = 0x34;
    private const int VK_C = 0x43, VK_H = 0x48, VK_R = 0x52, VK_T = 0x54, VK_V = 0x56, VK_W = 0x57;
    /// <summary>Opening more tabs than this at once asks first: each one is its own Claude process.</summary>
    private const int OpenWithoutAsking = 6;

    private readonly ObservableCollection<HostedTab> _tabs = new();
    private readonly WorkspaceState _state;
    private readonly DispatcherTimer _saveTimer;
    private DateTime _noticeUntil = DateTime.MinValue;
    private bool _allowClose;
    private bool _backgroundNoticeShown;

    /// <summary>A session finished, needs input, or ended while the workspace was not in front.</summary>
    public event Action<HostedTab, string>? Attention;
    /// <summary>A message for the tray, e.g. that sessions keep running after the window is closed.</summary>
    public event Action<string>? BackgroundNotice;

    public WorkspaceWindow()
    {
        InitializeComponent();
        Title = AppPaths.InstanceName switch
        {
            "Agmen" => "Agmen",
            "Agmen-dev" => "Agmen (dev)",
            var other => $"Agmen ({other})",
        };
        TabList.ItemsSource = _tabs;
        TabList.ContextMenu = BuildTabMenu();
        _state = WorkspaceStore.Load();
        ApplyBounds(_state.Bounds);
        InitPrimary();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
        Activated += (_, _) =>
        {
            if (SelectedTab is { } t) t.NeedsAttention = false;
            // If the window first appeared without focus, e.g. at sign-in while you were busy elsewhere,
            // no control has keyboard focus yet. Coming forward should always mean typing reaches Claude.
            FocusSelectedTerminalSoon();
        };
        LocationChanged += (_, _) => RequestSave();
        SizeChanged += (_, _) => RequestSave();
        Closing += OnClosing;
        new WindowInteropHelper(this).EnsureHandle();   // so the hotkey works before the window is first shown
        UpdateChrome();
    }

    public IReadOnlyList<HostedTab> Tabs => _tabs;
    public HostedTab? SelectedTab => TabList.SelectedItem as HostedTab;
    /// <summary>Follows the sessions of Codex, Kimi Code and Antigravity tabs.</summary>
    public AgentMonitor Agents { get; } = new();

    public bool Notifications
    {
        get => _state.Notifications;
        set { _state.Notifications = value; RequestSave(); }
    }

    // ----------------------------------------------------------- showing

    public void ShowWorkspace()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        FocusSelectedTerminalSoon();
    }

    public bool IsHostedPid(int pid) => pid > 0 && _tabs.Any(t => t.Pid == pid && t.IsRunning);

    public bool HasSession(string? sessionId) => _tabs.Any(t => Same(t.SessionId, sessionId));

    /// <summary>Bring the tab running this process to the front. False if it is not hosted here.</summary>
    public bool FocusPid(int pid)
    {
        var tab = _tabs.FirstOrDefault(t => t.Pid == pid && pid > 0);
        if (tab is null) return false;
        FocusTab(tab);
        return true;
    }

    public void FocusTab(HostedTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        Select(tab);
        ShowWorkspace();
    }

    /// <summary>The color code of the tab running this process, for the session panel.</summary>
    public TabCategory CategoryOfPid(int pid) =>
        pid > 0 ? _tabs.FirstOrDefault(t => t.Pid == pid && t.IsRunning)?.Category ?? TabCategory.None : TabCategory.None;

    public void SetCategory(HostedTab tab, TabCategory category)
    {
        if (tab.Category == category || tab.IsPrimary) return;
        tab.Category = category;
        Log.Write($"color code: {tab.Title} -> {category}");
        UpdateChrome();
        RequestSave();
    }

    /// <summary>Set the primary's one-line summary on a tab's card; null clears it.</summary>
    public void SetNote(HostedTab tab, string? note)
    {
        tab.Note = note ?? "";
        UpdateChrome();
        RequestSave();
    }

    // ----------------------------------------------------------- opening

    /// <summary>
    /// Start a brand-new session of an agent in a folder, optionally with a first message and a
    /// session name. The primary opens tabs in the background so the user's view stays put.
    /// </summary>
    public HostedTab? OpenNew(string cwd, string? initialPrompt = null, bool select = true, string? name = null,
                              AgentKind agent = AgentKind.Claude)
    {
        if (!Directory.Exists(cwd))
        {
            Notice($"Folder not found: {cwd}");
            return null;
        }
        OfferConnect(agent);
        var tab = new HostedTab(cwd, sessionId: null, knownTitle: name, agent: agent) { InitialPrompt = initialPrompt, LaunchName = name };
        AddTab(tab, select);
        StartTab(tab);
        return tab;
    }

    /// <summary>Resume a Codex, Kimi Code or Antigravity conversation in a new tab.</summary>
    public HostedTab? OpenResumed(AgentKind agent, string cwd, string sessionId, bool select = true)
    {
        if (!Directory.Exists(cwd))
        {
            Notice($"Folder not found: {cwd}");
            return null;
        }
        OfferConnect(agent);
        var tab = new HostedTab(cwd, sessionId, agent: agent);
        AddTab(tab, select);
        StartTab(tab);
        return tab;
    }

    /// <summary>
    /// Resume conversations as tabs. A conversation already open here is selected
    /// instead, and one running in another window is skipped, so no conversation
    /// is ever live twice. Returns how many tabs were opened or restarted.
    /// </summary>
    public int OpenResume(IEnumerable<RestoreService.Entry> entries, bool select = true)
    {
        var live = SessionMonitor.LiveSessionIds();
        var toOpen = new List<RestoreService.Entry>();
        int restarted = 0, alreadyHere = 0, elsewhere = 0, missing = 0;
        HostedTab? focus = null;

        foreach (var e in entries.DistinctBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase))
        {
            var existing = _tabs.FirstOrDefault(t => Same(t.SessionId, e.SessionId));
            if (existing is not null)
            {
                focus ??= existing;
                if (existing.IsEnded && !live.Contains(e.SessionId)) { StartTab(existing); restarted++; }
                else alreadyHere++;
                continue;
            }
            if (live.Contains(e.SessionId)) { elsewhere++; continue; }
            if (!Directory.Exists(e.Cwd)) { missing++; continue; }
            toOpen.Add(e);
        }

        if (toOpen.Count > OpenWithoutAsking &&
            !Confirm($"Open {toOpen.Count} sessions as tabs?\n\nEach one starts its own Claude Code process.", "Open sessions"))
            return restarted;

        int index = 0;
        foreach (var e in toOpen)
        {
            var tab = new HostedTab(e.Cwd, e.SessionId, knownTitle: e.Name);
            AddTab(tab, select: false);
            ScheduleStart(tab, index++);
            focus ??= tab;
        }
        if (focus is not null && select) Select(focus);

        var parts = new List<string>();
        if (toOpen.Count + restarted > 0) parts.Add($"opened {toOpen.Count + restarted}");
        if (alreadyHere > 0) parts.Add($"{alreadyHere} already open here");
        if (elsewhere > 0) parts.Add($"{elsewhere} running in another window");
        if (missing > 0) parts.Add($"{missing} with a missing folder");
        if (parts.Count > 0) Notice(string.Join(" · ", parts));
        return toOpen.Count + restarted;
    }

    /// <summary>The tab that has this conversation open, if any.</summary>
    public HostedTab? TabFor(AgentKind agent, string sessionId) =>
        _tabs.FirstOrDefault(t => t.Agent == agent && Same(t.SessionId, sessionId));

    private HistoryWindow? _history;

    /// <summary>The session history: sessions closed here, and every agent's recent ones, to reopen.</summary>
    public void ShowHistory()
    {
        if (_history is { IsVisible: true })
        {
            _history.Activate();
            return;
        }
        _history = new HistoryWindow(this);
        if (IsVisible) _history.Owner = this;
        _history.Show();
    }

    /// <summary>
    /// Reopen sessions from the history, each under its old parent when that is open again, so a
    /// subtree that closed together comes back as one. A conversation already open is just shown.
    /// </summary>
    public void Reopen(HistoryEntry entry, IReadOnlyList<HistoryEntry> below)
    {
        var all = below.Prepend(entry).ToList();   // parents before their children
        foreach (var agent in all.Select(e => AgentProfiles.Parse(e.Agent)).OfType<AgentKind>().Distinct()) OfferConnect(agent);
        var live = SessionMonitor.LiveSessionIds();
        var ids = new Dictionary<Guid, Guid>();   // a closed tab's id, and the id of the tab it reopened as
        var reopened = new List<HistoryEntry>();
        HostedTab? first = null;
        foreach (var e in all)
        {
            if (AgentProfiles.Parse(e.Agent) is not { } agent) continue;
            if (TabFor(agent, e.SessionId) is { } open)
            {
                ids[e.TabId] = open.Id;
                first ??= open;
                continue;
            }
            string? problem = !AgentProfiles.IsInstalled(agent) ? $"{AgentProfiles.DisplayName(agent)} isn't installed"
                : !Directory.Exists(e.Cwd) ? $"its folder is gone: {e.Cwd}"
                : agent == AgentKind.Claude && live.Contains(e.SessionId) ? "its conversation is open in another window"
                : null;
            if (problem is not null)
            {
                Notice($"Can't reopen {e.Title}: {problem}.");
                continue;
            }
            Guid? parent = e.ParentId is not { } p ? null
                : ids.TryGetValue(p, out var reopenedAs) ? reopenedAs
                : _tabs.Any(t => t.Id == p) ? p : null;
            // The old id, unless a tab has it again, so children that closed with it can find it.
            var tab = new HostedTab(e.Cwd, e.SessionId, _tabs.Any(t => t.Id == e.TabId) ? null : e.TabId, e.Title,
                                    TabCategories.Parse(e.Category), agent: agent) { ParentId = parent };
            if (e.Note is { Length: > 0 } note) tab.Note = note;
            ids[e.TabId] = tab.Id;
            AddTab(tab, select: false);
            ScheduleStart(tab, reopened.Count);
            first ??= tab;
            reopened.Add(e);
        }
        SessionHistory.Forget(reopened);
        if (first is null) return;
        Select(first);
        ShowWorkspace();
    }

    /// <summary>Resume a session found in an agent's own records, in a new top-level tab.</summary>
    public void Reopen(FoundSession found)
    {
        if (TabFor(found.Agent, found.SessionId) is { } open) Select(open);
        else if (!AgentProfiles.IsInstalled(found.Agent))
        {
            Notice($"{AgentProfiles.DisplayName(found.Agent)} isn't installed.");
            return;
        }
        else if (found.Agent == AgentKind.Claude) OpenResume(new[] { new RestoreService.Entry(found.SessionId, found.Cwd, found.Title) });
        else OpenResumed(found.Agent, found.Cwd, found.SessionId);
        ShowWorkspace();
    }

    /// <summary>Reopen CLI sessions whose transcripts were active within the window.</summary>
    public int OpenRecent(TimeSpan window)
    {
        var entries = RestoreService.RecentFromTranscripts(window).Select(r => r.Entry).ToList();
        if (entries.Count == 0)
        {
            Notice("No sessions were active in that period.");
            return 0;
        }
        return OpenResume(entries);
    }

    /// <summary>
    /// Reopen the tabs from the last run, resuming each conversation. Tabs whose session
    /// the user had exited stay closed until resumed by hand; ones now running elsewhere
    /// are left alone. Returns how many tabs came back.
    /// </summary>
    public int RestoreSavedTabs()
    {
        if (_state.Tabs.Count == 0) return 0;
        var live = SessionMonitor.LiveSessionIds();
        int started = 0;
        foreach (var saved in _state.Tabs)
        {
            if (!Directory.Exists(saved.Cwd))
            {
                Log.Write($"restore: dropping tab for missing folder {saved.Cwd}");
                continue;
            }
            if (saved.IsPrimary && Primary is not null) continue;   // only ever one primary
            var agent = AgentProfiles.Parse(saved.Agent) ?? AgentKind.Claude;
            var tab = saved.IsPrimary
                ? NewPrimaryTab(agent, saved.SessionId, saved.Id)
                : new HostedTab(saved.Cwd, saved.SessionId, saved.Id, saved.Title, TabCategories.Parse(saved.Category), agent: agent);
            tab.Note = saved.Note ?? "";
            tab.ParentId = saved.IsPrimary ? null : saved.ParentId;
            tab.IsCollapsed = saved.Collapsed;
            tab.LaunchModel = saved.Model;
            tab.LaunchEffort = saved.Effort;
            if (tab.IsPrimary) _tabs.Insert(0, tab);
            else _tabs.Add(tab);
            // Only Claude Code keeps a registry that says whether a conversation is open somewhere else.
            if (agent == AgentKind.Claude && saved.SessionId is not null && live.Contains(saved.SessionId))
                tab.MarkEnded("open in another window");
            else if (saved.Ended)
                tab.MarkEnded(null);
            else
                ScheduleStart(tab, started++);
        }
        ReorderTree();
        RefreshTree();
        var selected = _tabs.FirstOrDefault(t => t.Id == _state.Selected && !t.HiddenByCollapse) ?? _tabs.FirstOrDefault();
        if (selected is not null) Select(selected);
        UpdateChrome();
        Log.Write($"restore: {_tabs.Count} tabs, {started} resuming");
        return _tabs.Count;
    }

    /// <summary>The agent last picked for a new session; Ctrl+Shift+T starts it again.</summary>
    public AgentKind LastAgent => AgentProfiles.Parse(_state.LastAgent) ?? AgentKind.Claude;

    /// <summary>Pick a folder, then start a new session of the agent there; the agent defaults to the last one picked.</summary>
    public void NewSessionDialog(AgentKind? agent = null)
    {
        var chosen = agent ?? LastAgent;
        if (!AgentProfiles.IsInstalled(chosen))
        {
            Notice($"{AgentProfiles.DisplayName(chosen)} isn't installed.");
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Choose a folder for the new {AgentProfiles.DisplayName(chosen)} session",
            InitialDirectory = SelectedTab?.Cwd ?? _state.LastFolder
                               ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        bool? picked = IsVisible ? dialog.ShowDialog(this) : dialog.ShowDialog();
        if (picked != true) return;
        _state.LastFolder = dialog.FolderName;
        _state.LastAgent = AgentProfiles.Id(chosen);
        OpenNew(dialog.FolderName, agent: chosen);
        ShowWorkspace();
    }

    private readonly HashSet<AgentKind> _connectOffered = new();

    /// <summary>
    /// Open the Connect agents window when an agent that needs it isn't connected, once per run
    /// of Agmen, so a Kimi or Antigravity session never starts without its tools unnoticed. A
    /// session loads its tools only when it starts, so this comes before the session does.
    /// </summary>
    public void OfferConnect(AgentKind agent)
    {
        if (!AgentConnect.Connectable.Contains(agent) || _connectOffered.Contains(agent)) return;
        if (AgentConnect.StateOf(agent) is not (AgentConnect.State.NotConnected or AgentConnect.State.ConnectedElsewhere)) return;
        _connectOffered.Add(agent);
        RunConnect($"{AgentProfiles.DisplayName(agent)} isn't connected to Agmen yet, so its sessions can't use "
                   + "Agmen's tools: they can't start children or report to a parent."
                   + (agent == AgentKind.Antigravity ? " Its tabs can't show when it's working either." : ""));
    }

    /// <summary>A menu of the agents to start, for the + button, the tray and a tab's "New session in this folder".</summary>
    public ContextMenu AgentMenu(Action<AgentKind> start, bool withConnect)
    {
        var menu = new ContextMenu();
        foreach (var agent in AgentProfiles.All)
        {
            var a = agent;
            bool installed = AgentProfiles.IsInstalled(a);
            var item = new MenuItem
            {
                Header = installed ? AgentProfiles.DisplayName(a) : $"{AgentProfiles.DisplayName(a)} (not installed)",
                IsEnabled = installed,
                FontWeight = a == LastAgent ? FontWeights.SemiBold : FontWeights.Normal,
                Icon = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = AgentProfiles.BadgeBrush(a) },
            };
            item.Click += (_, _) => start(a);
            menu.Items.Add(item);
        }
        if (withConnect)
        {
            menu.Items.Add(new Separator());
            var settings = new MenuItem { Header = "Agent settings…" };
            settings.Click += (_, _) => ShowAgentSettings();
            menu.Items.Add(settings);
            var connect = new MenuItem { Header = "Connect agents…" };
            connect.Click += (_, _) => ShowConnect();
            menu.Items.Add(connect);
            var primaryAs = new MenuItem { Header = "Run the primary session as" };
            foreach (var item in PrimaryAgentItems()) primaryAs.Items.Add(item);
            menu.Items.Add(primaryAs);
        }
        return menu;
    }

    /// <summary>The window where the user connects Kimi Code and Antigravity to Agmen.</summary>
    public void ShowConnect() => RunConnect(null);

    /// <summary>The window for each agent's model, effort and permissions, then the restarts that make them count.</summary>
    public void ShowAgentSettings()
    {
        var window = new AgentSettingsWindow();
        if (IsVisible) window.Owner = this;
        window.ShowDialog();
        // A running session keeps the settings it started with until it restarts.
        var affected = _tabs.Where(t => window.Changed.Contains(t.Agent) && t.State is TabState.Running or TabState.Starting).ToList();
        if (affected.Count == 0) return;
        var question = affected.Count == 1
            ? $"Restart '{affected[0].Title}' now, so it uses the new settings? It keeps its conversation."
            : $"Restart the {affected.Count} running sessions of {(window.Changed.Count == 1 ? "that agent" : "those agents")} now, "
              + "so they use the new settings? Each keeps its conversation.";
        if (Confirm(question, "Agent settings")) RestartTabs(affected);
    }

    private void RunConnect(string? reason)
    {
        var before = AgentConnect.Connectable.ToDictionary(a => a, AgentConnect.StateOf);
        var window = new ConnectWindow(reason);
        if (IsVisible) window.Owner = this;
        window.ShowDialog();

        // A session loads its tools when it starts, so tabs already running need a restart to get them.
        var stale = _tabs.Where(t => t.State is TabState.Running or TabState.Starting && before.TryGetValue(t.Agent, out var was)
                                     && was != AgentConnect.State.Connected
                                     && AgentConnect.StateOf(t.Agent) == AgentConnect.State.Connected).ToList();
        if (stale.Count == 0) return;
        // Connected now, but these sessions started without the tools and keep running that way until restarted.
        foreach (var t in stale)
            t.ToolsMissing = $"{AgentProfiles.DisplayName(t.Agent)} is connected now, but this session started before that. Restart it to get Agmen's tools.";
        var names = string.Join(", ", stale.Select(t => $"'{t.Title}'"));
        if (Confirm($"{names} started before {(stale.Count == 1 ? "its agent was" : "their agents were")} connected. "
                    + $"Restart {(stale.Count == 1 ? "it" : "them")} now to get Agmen's tools?", "Connect agents"))
            RestartTabs(stale);
    }

    // ----------------------------------------------------------- tab plumbing

    private void AddTab(HostedTab tab, bool select)
    {
        _tabs.Add(tab);
        ReorderTree();
        RefreshTree();
        if (select) Select(tab);
        UpdateChrome();
        RequestSave();
    }

    private void Select(HostedTab tab)
    {
        TabList.SelectedItem = tab;
        TabList.ScrollIntoView(tab);
    }

    /// <summary>Start, or restart, the tab's agent process in a fresh terminal control.</summary>
    private void StartTab(HostedTab tab)
    {
        var watch = Stopwatch.StartNew();
        if (tab.Terminal is { } old) TerminalHost.Children.Remove(old);
        if (tab.IsPrimary) PreparePrimary(tab);
        // Every Claude tab gets Agmen's tool server beside the user's own MCP servers, its tools
        // pre-approved, and Agmen's guide to the tree. The other agents get theirs on their command
        // line or from Connect. (--allowedTools takes a list, so another option must follow it.)
        else if (tab.Agent == AgentKind.Claude)
        {
            try
            {
                tab.ExtraArgs = new[]
                {
                    "--allowedTools", "mcp__agmen", "--mcp-config", ToolServer.ClaudeConfigFile(),
                    "--append-system-prompt", AgentGuide.Text,
                };
            }
            catch (Exception ex) { Log.Write($"tab {tab.Title}: couldn't write the tool server config: {ex.Message}"); }
        }
        // Kimi Code and Antigravity get Agmen's tools only when connected to this copy. A session
        // that starts without them says so on its card, however it was started.
        tab.ToolsMissing = AgentConnect.ToolsMissingReason(tab.Agent);
        if (tab.ToolsMissing is { } missing) Log.Write($"tab {tab.Title}: starting without Agmen's tools: {missing}");
        long prepared = watch.ElapsedMilliseconds;
        var ctl = tab.Start();
        if (ctl is not null)
        {
            // Hidden rather than collapsed: background tabs keep their real size, so the
            // pseudo-console starts at the right width and resizes along with the window.
            ctl.Visibility = tab == SelectedTab ? Visibility.Visible : Visibility.Hidden;
            TerminalHost.Children.Add(ctl);
            if (tab == SelectedTab) FocusSelectedTerminalSoon();
        }
        UpdateChrome();
        RequestSave();
        if (watch.ElapsedMilliseconds > 400)
            Log.Write($"tab {tab.Title}: starting took {watch.ElapsedMilliseconds} ms (setup {prepared} ms)");
    }

    /// <summary>Stagger restores so a dozen Claude processes do not all start in the same instant.</summary>
    private async void ScheduleStart(HostedTab tab, int index)
    {
        if (index > 0) await Task.Delay(index * 700);
        if (_tabs.Contains(tab) && tab.Terminal is null && tab.State == TabState.Pending) StartTab(tab);
    }

    /// <summary>
    /// Stop the agents and start them again on the same conversations, for a session that is
    /// stuck or one that should pick up new settings, such as Agmen's tools after Connect. One
    /// question covers any that are working, and the starts are staggered. A session Agmen
    /// hasn't learned the id of starts fresh.
    /// </summary>
    public void RestartTabs(IReadOnlyCollection<HostedTab> tabs)
    {
        var live = tabs.Where(t => t.State is TabState.Running or TabState.Starting).ToList();
        if (live.Count == 0) return;
        var working = live.Where(t => t.IsBusy).ToList();
        if (working.Count > 0)
        {
            var question = live.Count == 1
                ? $"'{live[0].Title}' is working. Restarting stops its current turn. Restart anyway?"
                : $"{Names(working)} {(working.Count == 1 ? "is" : "are")} working. Restarting stops "
                  + $"{(working.Count == 1 ? "its current turn" : "their current turns")}. Restart anyway?";
            if (!Confirm(question, "Restart")) return;
        }
        var unknown = live.Where(t => t.SessionId is null).ToList();
        if (unknown.Count > 0)
            Notice(unknown.Count == 1
                ? $"Agmen doesn't know {unknown[0].Title}'s conversation yet, so it starts a new one."
                : $"Agmen doesn't know the conversations of {Names(unknown)} yet, so they start new ones.");
        for (int i = 0; i < live.Count; i++) ScheduleRestart(live[i], i);

        static string Names(List<HostedTab> list) => string.Join(", ", list.Select(t => $"'{t.Title}'"));
    }

    private async void ScheduleRestart(HostedTab tab, int index)
    {
        if (index > 0) await Task.Delay(index * 700);
        if (!_tabs.Contains(tab) || tab.State is not (TabState.Running or TabState.Starting)) return;
        Log.Write($"tab {tab.Title}: restarting");
        // The conversation must not be open in the old process and the new one at once.
        await tab.StopForRestartAsync(TimeSpan.FromSeconds(5));
        if (_tabs.Contains(tab)) StartTab(tab);
    }

    /// <summary>
    /// Stop an Antigravity session that agy won't run as the agmen agent, and start it again as
    /// agy's default agent, which has the same tools and Agmen's.
    /// </summary>
    private async void RestartAsDefaultAgent(HostedTab tab)
    {
        Log.Write($"tab {tab.Title}: Antigravity refused the agmen agent, so the session starts again as its default agent");
        Notice($"Antigravity refused Agmen's agent, so '{tab.Title}' starts again as its default agent. Update Agmen if this keeps happening.");
        await tab.StopForRestartAsync(TimeSpan.FromSeconds(5));
        if (_tabs.Contains(tab)) StartTab(tab);
    }

    /// <summary>Restart the selected session if it is running, or resume it if it has ended.</summary>
    private void RestartOrResume(HostedTab tab)
    {
        if (tab.IsEnded) ResumeTab(tab);
        else RestartTabs(new[] { tab });
    }

    public void ResumeTab(HostedTab tab)
    {
        if (!tab.IsEnded) return;
        if (tab.Agent == AgentKind.Claude && tab.SessionId is not null && SessionMonitor.LiveSessionIds().Contains(tab.SessionId))
        {
            Notice("That conversation is open in another window. Close it there, then resume it here.");
            return;
        }
        StartTab(tab);
    }

    /// <summary>Close a session, and with it every session under it: its children, theirs, and so on.</summary>
    public void CloseTab(HostedTab tab)
    {
        var below = DescendantsOf(tab).ToList();
        int working = below.Prepend(tab).Count(t => t.IsBusy);
        string? question;
        if (tab.IsPrimary)
            question = "Close the primary session?\n\nStart it again with ★ to pick up the same conversation."
                       + (below.Count == 0 ? "" : below.Count == 1 ? "\n\nThe session under it closes too." : $"\n\nThe {below.Count} sessions under it close too.");
        else if (below.Count > 0)
            question = $"Close '{tab.Title}' and the {Sessions(below.Count)} under it?"
                       + (working == 0 ? "" : $"\n\n{working} of them {(working == 1 ? "is" : "are")} still working.")
                       + "\n\nTheir conversations are saved, so you can resume them later.";
        else question = tab.IsBusy
            ? $"'{tab.Title}' is still working. Close it anyway?\n\nThe conversation is saved, so you can resume it later."
            : null;
        if (question is not null && !Confirm(question, tab.IsPrimary ? "Close the primary session" : "Close tab")) return;
        if (tab.IsPrimary) RememberPrimarySession(tab);

        // Closed sessions go into the history with their place in the tree, to be reopened later.
        var closed = below.Prepend(tab).Where(t => !t.IsPrimary && t.SessionId is not null)
            .Select(t => new HistoryEntry(t.Id, AgentProfiles.Id(t.Agent), t.Cwd, t.SessionId!, t.Title, DateTimeOffset.Now,
                                          t.ParentId, ParentOf(t)?.Title, t.HasCategory ? t.Category.ToString() : null,
                                          t.HasNote ? t.Note : null))
            .ToList();
        int index = _tabs.IndexOf(tab);
        // The deepest sessions go first, so none is left without its parent even for a moment.
        foreach (var t in Enumerable.Reverse(below)) Remove(t);
        Remove(tab);
        ReorderTree();
        RefreshTree();
        var next = _tabs.Skip(Math.Min(index, Math.Max(0, _tabs.Count - 1))).Concat(_tabs.Reverse()).FirstOrDefault(t => !t.HiddenByCollapse);
        if (next is not null) Select(next);
        UpdateChrome();
        RequestSave();
        SessionHistory.Record(closed);

        void Remove(HostedTab t)
        {
            t.Stop();
            if (t.Terminal is { } ctl) TerminalHost.Children.Remove(ctl);
            _tabs.Remove(t);
            Log.Write($"closed tab {t.Title} ({t.SessionId})");
        }

        static string Sessions(int n) => n == 1 ? "session" : $"{n} sessions";
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab step through the cards that are showing, skipping folded ones.</summary>
    private void CycleTab(int direction)
    {
        var visible = _tabs.Where(t => !t.HiddenByCollapse).ToList();
        if (visible.Count == 0) return;
        int current = SelectedTab is { } s ? Math.Max(0, visible.IndexOf(s)) : 0;
        Select(visible[((current + direction) % visible.Count + visible.Count) % visible.Count]);
    }

    private void FocusSelectedTerminalSoon() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsActive) SelectedTab?.Terminal?.Focus();
        });

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedTab;
        foreach (var t in _tabs)
            if (t.Terminal is { } ctl) ctl.Visibility = t == selected ? Visibility.Visible : Visibility.Hidden;
        if (selected is not null && IsActive) selected.NeedsAttention = false;
        UpdateChrome();
        RequestSave();
        // A right-click selects the card for its menu; moving focus then would close the menu.
        if (Mouse.RightButton != MouseButtonState.Pressed) FocusSelectedTerminalSoon();
    }

    // ----------------------------------------------------------- polling and alerts

    /// <summary>
    /// The tabs whose agent the agent monitor follows, for the background poll. Claude tabs are
    /// matched to Claude Code's own registry instead.
    /// </summary>
    public List<AgentTabRef> AgentTabRefs() =>
        _tabs.Where(t => t.Agent != AgentKind.Claude && t.State is TabState.Starting or TabState.Running)
             .Select(t => t.AgentRef()).ToList();

    /// <summary>
    /// Called after every poll: match Claude tabs to their registry entries by process id and
    /// the other tabs to what the agent monitor found, follow status changes, and raise an
    /// alert when an agent hands the turn back while the user is looking elsewhere.
    /// </summary>
    public void UpdateFromSnapshot(IReadOnlyList<SessionInfo> snapshot, IReadOnlyDictionary<Guid, SessionInfo>? agentInfos = null)
    {
        LastSnapshot = snapshot;
        var byPid = new Dictionary<int, SessionInfo>();
        foreach (var s in snapshot)
            if (s.Pid > 0) byPid.TryAdd(s.Pid, s);

        bool changed = false;
        long maxOutput = 1;
        foreach (var tab in _tabs.ToList())
        {
            var idBefore = tab.SessionId;
            var stateBefore = tab.State;
            string? alert = tab.Poll() ? "ended" : null;
            // agy refuses to run the agmen agent in this new session: start it again as agy's default agent.
            if (tab.Agent == AgentKind.Antigravity && Agents.StateOf(tab.Id) is { AgentRefused: true } && tab.TakeAgentRefusal())
            {
                RestartAsDefaultAgent(tab);
                continue;
            }
            SessionInfo? info = null;
            if (tab.Agent != AgentKind.Claude)
            {
                if (tab.IsRunning && agentInfos is not null && agentInfos.TryGetValue(tab.Id, out var agentInfo)) info = agentInfo;
            }
            else if (tab.Pid > 0) byPid.TryGetValue(tab.Pid, out info);
            if (info is not null)
            {
                alert ??= tab.UpdateFrom(info);
                maxOutput = Math.Max(maxOutput, info.OutputTokens);
                // Typed text must reach the prompt, not a question whether to trust the folder.
                // Nor, for Codex, one of its startup questions, which it also asks when it resumes:
                // it starts Agmen's tool server only once it is past them.
                if (tab.Agent != AgentKind.Claude && !tab.AgentReady)
                    tab.AgentReady = (tab.Agent != AgentKind.Codex || tab.ToolServerSeen)
                                     && (Agents.StateOf(tab.Id) is { TurnsCompleted: > 0 } || AgentMonitor.Trusts(tab.Agent, tab.Cwd));
            }
            else tab.Refresh();
            if (tab.TakeTrustQuestion()) alert ??= "trust";
            if (!Same(idBefore, tab.SessionId) || stateBefore != tab.State) changed = true;
            // Not the user's turn while sessions under it are still at work.
            if (alert == "finished" && RunningBelow(tab)) alert = null;

            if (alert is not null && !(IsActive && tab == SelectedTab))
            {
                tab.Flag(alert);
                if (!IsActive)
                {
                    Flash();
                    Attention?.Invoke(tab, alert);
                }
            }
        }
        foreach (var tab in _tabs)
        {
            tab.Session?.SetOutputScale(maxOutput);
            // A "your turn" badge stops being true once the session, or one under it, is working again.
            if (tab.IsBusy || RunningBelow(tab)) tab.ClearYourTurn();
        }
        if (changed) RequestSave();
        PumpMessages();
        UpdateChrome();
    }

    /// <summary>Whether a session under this one is starting or working.</summary>
    private bool RunningBelow(HostedTab tab) => DescendantsOf(tab).Any(t => t.IsBusy || t.State == TabState.Starting);

    private void UpdateChrome()
    {
        var tab = SelectedTab;
        EmptyState.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFooter();
        CloseTabButton.IsEnabled = tab is not null;
        // One button: Resume for a session that has ended, Restart for one that is running.
        RestartButton.Visibility = tab is { IsEnded: true } or { State: TabState.Running or TabState.Starting }
            ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.Content = tab is { IsEnded: true } ? "↻  Resume" : "↻  Restart";
        RestartButton.ToolTip = tab is { IsEnded: true }
            ? "Start the agent again in this tab, continuing the same conversation (Ctrl+Shift+R)"
            : "Stop the agent and start it again, continuing the same conversation (Ctrl+Shift+R)";

        HeaderTitle.Foreground = tab?.TitleBrush ?? TabCategories.DefaultText;
        if (tab is { HasCategory: true })
        {
            HeaderCategory.Background = tab.CategoryBrush;
            HeaderCategoryText.Text = tab.CategoryLabel;
            HeaderCategory.Visibility = Visibility.Visible;
        }
        else HeaderCategory.Visibility = Visibility.Collapsed;

        if (tab is null)
        {
            HeaderTitle.Text = AppPaths.IsDev ? "Agmen (dev)" : "Agmen";
            HeaderDetail.Text = "Ctrl+Shift+T new session   ·   Ctrl+Tab next tab";
        }
        else
        {
            HeaderTitle.Text = tab.IsPrimary ? "Primary session" : tab.Title;
            var parts = new List<string>();
            // First, so a long folder path can't push it out of view.
            if (tab.HasToolsMissing) parts.Add("no Agmen tools");
            parts.AddRange(new[]
            {
                tab.IsPrimary ? $"{AgentProfiles.DisplayName(tab.Agent)}, reads and directs your sessions" : $"{AgentProfiles.DisplayName(tab.Agent)} in {tab.Cwd}",
                tab.StatusText,
            });
            if (tab.ParentTitle is { } parentTitle) parts.Add($"child of {parentTitle}");
            if (tab.HasChildren) parts.Add($"{tab.ChildCount} child session{(tab.ChildCount == 1 ? "" : "s")}");
            if (!string.IsNullOrEmpty(tab.BranchText)) parts.Add(tab.BranchText);
            if (tab.HasNote) parts.Add(tab.Note);
            if (tab.Session is { } s && s.UsageText != "no usage yet") parts.Add(s.UsageText);
            if (tab.HasQueued) parts.Add($"{tab.QueuedCount} queued: {tab.HoldReason}");
            HeaderDetail.Text = string.Join("   ·   ", parts);
        }
    }

    private void UpdateFooter()
    {
        if (DateTime.UtcNow <= _noticeUntil) return;
        int busy = _tabs.Count(t => t.IsBusy), ended = _tabs.Count(t => t.IsEnded);
        SidebarFooter.Text = _tabs.Count == 0
            ? "Ctrl+Shift+T starts a session"
            : $"{_tabs.Count} tab{(_tabs.Count == 1 ? "" : "s")} · {busy} working"
              + (ended > 0 ? $" · {ended} ended" : "") + " · Ctrl+Tab switches";
    }

    private void Notice(string text)
    {
        SidebarFooter.Text = text;
        _noticeUntil = DateTime.UtcNow.AddSeconds(6);
        Log.Write("notice: " + text);
    }

    // ----------------------------------------------------------- keyboard

    /// <summary>
    /// Workspace shortcuts, and the clipboard behavior Windows Terminal users expect.
    /// Runs before the terminal sees the key, so handled keys never reach Claude.
    /// </summary>
    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled || msg.message != WM_KEYDOWN || !IsActive) return;
        int vk = (int)msg.wParam;
        bool ctrl = Down(VK_CONTROL), shift = Down(VK_SHIFT), alt = Down(VK_MENU);
        var tab = SelectedTab;

        // Workspace shortcuts. These never reach Claude.
        if (ctrl && !alt)
        {
            if (vk == VK_TAB)   // Ctrl+Tab / Ctrl+Shift+Tab
            {
                CycleTab(shift ? -1 : 1);
                handled = true;
                return;
            }
            if (vk == VK_T && shift)   // Ctrl+Shift+T
            {
                handled = true;
                Dispatcher.InvokeAsync(() => NewSessionDialog());
                return;
            }
            if (vk == VK_R && shift && tab is not null)   // Ctrl+Shift+R restarts the session, or resumes it if it ended
            {
                handled = true;
                Dispatcher.InvokeAsync(() => RestartOrResume(tab));
                return;
            }
            if (vk == VK_H && shift)   // Ctrl+Shift+H shows the session history
            {
                handled = true;
                Dispatcher.InvokeAsync(ShowHistory);
                return;
            }
            if (shift && vk >= VK_0 && vk <= VK_4 && tab is not null)   // Ctrl+Shift+1..4 color code, Ctrl+Shift+0 clears
            {
                SetCategory(tab, vk == VK_0 ? TabCategory.None : TabCategories.All[vk - VK_0 - 1]);
                handled = true;
                return;
            }
        }

        if (tab is null || !tab.OwnsWindow(msg.hwnd)) return;   // the rest only apply inside the terminal

        // WPF treats Tab and the arrow keys as focus navigation and would move focus out of
        // the terminal before it ever saw them, so Tab could not accept Claude's suggestions.
        // Hand those keys straight to the terminal window, exactly as Windows would have.
        if (!alt && ((vk == VK_TAB && !ctrl) || (vk >= VK_LEFT && vk <= VK_DOWN)))
        {
            tab.NoteUserKey(clearsInput: false);
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            handled = true;
            return;
        }

        if (!ctrl || alt)
        {
            NoteTyping(tab, vk, newLine: shift || alt);
            return;
        }

        if (vk == VK_V)   // Ctrl+V / Ctrl+Shift+V paste text
        {
            tab.NoteUserKey(clearsInput: false);
            string? text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch (Exception) { /* clipboard busy */ }
            if (string.IsNullOrEmpty(text)) return;   // an image: let Claude paste it itself
            tab.Paste(text);
            handled = true;
        }
        else if (vk == VK_C)   // copy the terminal's own selection; plain Ctrl+C otherwise reaches Claude
        {
            var selection = tab.SelectedText;
            if (string.IsNullOrEmpty(selection))
            {
                if (shift) handled = true;
                else tab.NoteUserKey(clearsInput: true);   // Ctrl+C clears Claude's prompt
                return;
            }
            TrySetClipboard(selection);
            handled = true;
        }
        else NoteTyping(tab, vk, newLine: true);
    }

    /// <summary>
    /// Remember that the user may have text half-typed in this tab, so Agmen doesn't type a
    /// queued message into the middle of it. Enter sends what was typed, or runs a command such
    /// as /model, so the prompt is empty again; with Shift, Ctrl or Alt it only adds a line.
    /// Modifiers and Esc change nothing.
    /// </summary>
    private static void NoteTyping(HostedTab tab, int vk, bool newLine)
    {
        if (vk is VK_SHIFT or VK_CONTROL or VK_MENU or 0x14 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5) or 0x1B)
            return;
        tab.NoteUserKey(clearsInput: vk == 0x0D && !newLine);
    }

    // ----------------------------------------------------------- buttons and menus

    /// <summary>The + button offers every agent; the one picked last is in bold.</summary>
    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        var menu = AgentMenu(agent => NewSessionDialog(agent), withConnect: true);
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// ★ shows the primary. With no primary yet and no agent chosen for it, it asks which agent
    /// should run it, when there is more than one to choose from.
    /// </summary>
    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (Primary is null && _state.PrimaryAgent is null && AgentProfiles.All.Count(AgentProfiles.IsInstalled) > 1) ShowPrimaryMenu();
        else OpenPrimary();
    }

    private void OnPrimaryRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowPrimaryMenu();
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RecentButton, Placement = PlacementMode.Bottom };
        var history = new MenuItem { Header = "Session history…", InputGestureText = "Ctrl+Shift+H" };
        history.Click += (_, _) => ShowHistory();
        menu.Items.Add(history);
        menu.Items.Add(new Separator());
        void Add(string header, TimeSpan window)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => OpenRecent(window);
            menu.Items.Add(item);
        }
        Add("Claude Code sessions active in the last hour", TimeSpan.FromHours(1));
        Add("Claude Code sessions active in the last 4 hours", TimeSpan.FromHours(4));
        Add("Claude Code sessions active in the last 24 hours", TimeSpan.FromHours(24));
        if (RestoreService.LoadSet() is { } set)
        {
            menu.Items.Add(new Separator());
            var item = new MenuItem { Header = $"Saved session set ({set.Entries.Count})" };
            item.Click += (_, _) => OpenResume(set.Entries);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTab is { } tab) RestartOrResume(tab);
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTab is { } tab) CloseTab(tab);
    }

    private void OnCardCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is HostedTab tab) CloseTab(tab);
    }

    private ContextMenu BuildTabMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(object header, Action<HostedTab> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => { if (SelectedTab is { } t) action(t); };
            menu.Items.Add(item);
            return item;
        }

        // Color code: what this session is working on.
        var colorItems = new List<(MenuItem Item, TabCategory Category)>();
        foreach (var category in TabCategories.All.Append(TabCategory.None))
        {
            var c = category;
            var item = Item(ColorHeader(c), t => SetCategory(t, c));
            int shortcut = c == TabCategory.None ? 0 : Array.IndexOf(TabCategories.All, c) + 1;
            item.InputGestureText = $"Ctrl+Shift+{shortcut}";
            colorItems.Add((item, c));
        }
        menu.Items.Add(new Separator());

        var resume = Item("Resume session", ResumeTab);
        resume.InputGestureText = "Ctrl+Shift+R";
        var restart = Item("Restart session", t => RestartTabs(new[] { t }));
        restart.InputGestureText = "Ctrl+Shift+R";
        restart.ToolTip = "Stop the agent and start it again, continuing the same conversation";
        var restartTree = Item("Restart it and the sessions under it", t => RestartTabs(DescendantsOf(t).Prepend(t).ToList()));
        var sameFolder = new MenuItem { Header = "New session in this folder" };
        foreach (var agent in AgentProfiles.All.Where(AgentProfiles.IsInstalled))
        {
            var a = agent;
            var item = new MenuItem { Header = AgentProfiles.DisplayName(a) };
            item.Click += (_, _) => { if (SelectedTab is { } t) OpenNew(t.Cwd, agent: a); };
            sameFolder.Items.Add(item);
        }
        menu.Items.Add(sameFolder);
        // On the primary's own card: which agent runs it.
        var primaryAs = new MenuItem { Header = "Run the primary session as" };
        menu.Items.Add(primaryAs);
        AddTreeMenuItems(menu);
        menu.Items.Add(new Separator());
        Item("Open folder in Explorer", t => OpenFolder(t.Cwd));
        var copyId = Item("Copy session ID", t => TrySetClipboard(t.SessionId ?? ""));
        var queueSeparator = new Separator();
        menu.Items.Add(queueSeparator);
        var deliver = Item("Type the queued message now", t =>
        {
            if (!t.TryDeliver(force: true)) Notice($"Not yet: {t.HoldReason}.");
        });
        var discard = Item("Discard queued messages", t => Notice($"Discarded {t.ClearQueue()} queued message(s)."));
        menu.Items.Add(new Separator());
        Item("Close tab", CloseTab);
        menu.Opened += (_, _) =>
        {
            var t = SelectedTab;
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = t is not null;
            foreach (var (item, category) in colorItems)
            {
                item.IsChecked = t?.Category == category;
                item.IsEnabled = t is { IsPrimary: false };
            }
            resume.Visibility = t is { IsEnded: true } ? Visibility.Visible : Visibility.Collapsed;
            restart.Visibility = t is { IsEnded: true } ? Visibility.Collapsed : Visibility.Visible;
            restart.IsEnabled = t is { State: TabState.Running or TabState.Starting };
            restartTree.Visibility = t is not null && ChildrenOf(t).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            copyId.IsEnabled = t?.SessionId is not null;
            primaryAs.Visibility = t is { IsPrimary: true } ? Visibility.Visible : Visibility.Collapsed;
            primaryAs.Items.Clear();
            if (t is { IsPrimary: true })
                foreach (var item in PrimaryAgentItems()) primaryAs.Items.Add(item);
            deliver.Visibility = discard.Visibility = queueSeparator.Visibility =
                t is { HasQueued: true } ? Visibility.Visible : Visibility.Collapsed;
        };
        return menu;
    }

    /// <summary>A menu header with a color swatch, so the menu itself shows the code.</summary>
    private static object ColorHeader(TabCategory category)
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = TabCategories.BrushFor(category) ?? System.Windows.Media.Brushes.Transparent,
            Stroke = System.Windows.Media.Brushes.Gray,
            StrokeThickness = category == TabCategory.None ? 1 : 0,
        });
        panel.Children.Add(new TextBlock { Text = TabCategories.MenuText(category), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"open folder failed: {ex.Message}"); }
    }

    private static void TrySetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception) { /* clipboard busy */ }
    }

    private bool Confirm(string text, string caption)
    {
        var result = IsVisible
            ? MessageBox.Show(this, text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.OK;
    }

    // ----------------------------------------------------------- persistence and shutdown

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Write the workspace now. Also used when Windows is logging off.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        _state.Tabs = _tabs.Select(t => new SavedTab(
            t.Id, t.Cwd, t.SessionId, t.IsPrimary ? null : t.Title, t.IsEnded, t.HasCategory ? t.Category.ToString() : null,
            t.IsPrimary, t.HasNote ? t.Note : null, t.Agent == AgentKind.Claude ? null : AgentProfiles.Id(t.Agent),
            t.ParentId, t.IsCollapsed, t.LaunchModel, t.LaunchEffort)).ToList();
        if (Primary is { } primary)
        {
            RememberPrimarySession(primary);
            _state.PrimaryAgent = AgentProfiles.Id(primary.Agent);
        }
        _state.Selected = SelectedTab?.Id;
        _state.Bounds = CaptureBounds();
        WorkspaceStore.Save(_state);
    }

    /// <summary>Exit path: save the tabs as they are, so the next start resumes them, then stop every session.</summary>
    public void ShutdownTabs()
    {
        SaveNow();
        foreach (var tab in _tabs) tab.Stop();
        Log.Write($"shutdown: stopped {_tabs.Count} tabs");
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveNow();
        if (_allowClose) return;
        // Closing the window only hides it. Its sessions keep running, and alerts still arrive via the tray.
        e.Cancel = true;
        Hide();
        int running = _tabs.Count(t => t.IsRunning);
        if (running > 0 && !_backgroundNoticeShown)
        {
            _backgroundNoticeShown = true;
            BackgroundNotice?.Invoke(
                $"{running} session{(running == 1 ? " keeps" : "s keep")} running in the background. "
                + "Reopen the workspace from the tray icon. Exit stops them.");
        }
    }

    private void ApplyBounds(SavedBounds? b)
    {
        if (b is null || b.Width < 400 || b.Height < 300) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.Contains(new System.Windows.Point(b.Left + 60, b.Top + 30))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    private SavedBounds? CaptureBounds()
    {
        if (!IsLoaded) return _state.Bounds;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (r.IsEmpty || double.IsNaN(r.Left) || double.IsNaN(r.Top)) return _state.Bounds;
        return new SavedBounds(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized);
    }

    // ----------------------------------------------------------- Win32

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (AppPaths.IsDev) return;   // the release copy owns the hotkey
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        if (!RegisterHotKey(handle, HotkeyId, 0x2 | 0x1, VK_W)) Log.Write("Ctrl+Alt+W is taken by another app");
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            if (IsActive) WindowState = WindowState.Minimized;
            else ShowWorkspace();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Flash()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !IsVisible) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            dwFlags = 0x2 | 0xC,   // FLASHW_TRAY | FLASHW_TIMERNOFG: flash the taskbar button until the window comes forward
        };
        FlashWindowEx(ref info);
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool TranslateMessage([In] ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage([In] ref MSG msg);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
