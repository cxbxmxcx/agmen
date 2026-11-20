using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Agmen.Models;
using Agmen.Services;

namespace Agmen;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xC1AD;
    private const uint MOD_CONTROL = 0x2, MOD_ALT = 0x1;
    private const uint VK_C = 0x43;

    /// <summary>Raised after each poll with (total, busy) so the tray icon can reflect activity.</summary>
    public event Action<int, int>? SessionsRefreshed;

    private readonly SessionMonitor _monitor = new();
    private readonly WorkspaceWindow _workspace;
    private readonly ObservableCollection<SessionViewModel> _sessions = new();
    private readonly DispatcherTimer _timer;
    private bool _refreshing;
    private DateTime _suppressAutoHideUntil = DateTime.MinValue;
    private DateTime _footerOverrideUntil = DateTime.MinValue;
    private bool _userMoved;
    private List<RestoreService.Entry> _pendingRestore = new();
    private List<Models.SessionInfo> _lastSnapshot = new();
    private bool _recentOffered;
    private int _unregisteredLive;
    private static string SettingsPath => AppPaths.PathOf("settings.json");

    public MainWindow(WorkspaceWindow workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        LoadSettings();
        PinButton.Checked += (_, _) => SaveSettings();
        PinButton.Unchecked += (_, _) => SaveSettings();
        HideDormantButton.Checked += (_, _) => { SaveSettings(); _ = RefreshAsync(); };
        HideDormantButton.Unchecked += (_, _) => { SaveSettings(); _ = RefreshAsync(); };

        // Sessions recorded as alive last time Agmen ran; anything still
        // dead after the first poll is offered for one-click restore.
        // Sessions that never got a message saved nothing, so there is nothing to restore.
        _pendingRestore = RestoreService.LoadPending().Where(p => RestoreService.HasTranscript(p.SessionId)).ToList();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        Deactivated += (_, _) =>
        {
            // Don't auto-hide when the focus loss was caused by our own
            // click-to-focus handoff to a session's terminal window.
            if (PinButton.IsChecked != true && DateTime.UtcNow > _suppressAutoHideUntil) Hide();
        };
        KeyDown += (_, a) =>
        {
            if (a.Key == Key.Escape) Hide();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (AppPaths.IsDev) return;   // the release copy owns the hotkey
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
        if (!RegisterHotKey(source.Handle, HotkeyId, MOD_CONTROL | MOD_ALT, VK_C))
            SetFooterNotice("Ctrl+Alt+C is taken by another app — use the tray icon to toggle");
    }

    protected override void OnClosed(EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            UnregisterHotKey(source.Handle, HotkeyId);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        if (!_userMoved) PositionBottomRight();
        Show();
        Activate();
        _ = RefreshAsync();
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - ActualHeight - 12;
        // SizeToContent means height settles after layout; re-anchor then.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_userMoved) Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12;
        });
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            bool scanTranscripts = !_recentOffered && _pendingRestore.Count == 0;
            var agentTabs = _workspace.AgentTabRefs();
            var (snapshot, unregistered, recent, agentInfos) = await Task.Run(() => (
                _monitor.Snapshot(),
                _monitor.UnregisteredLiveCount(),
                scanTranscripts ? RestoreService.RecentFromTranscripts(TimeSpan.FromHours(3)) : null,
                _workspace.Agents.Poll(agentTabs)));
            _lastSnapshot = snapshot;
            _unregisteredLive = unregistered;
            // The workspace first, so the hosted flags below reflect this poll.
            _workspace.UpdateFromSnapshot(snapshot, agentInfos);
            bool hideDormant = HideDormantButton.IsChecked == true;
            var display = hideDormant ? snapshot.Where(s => !s.IsDormant).ToList() : snapshot;
            Reconcile(display);
            int busy = snapshot.Count(s => s.IsBusy);
            int dormant = snapshot.Count(s => s.IsDormant);
            int hosted = snapshot.Count(s => _workspace.IsHostedPid(s.Pid));
            // Unregistered = running in child-session mode (transcript saving off); see SessionMonitor.UnregisteredLiveCount.
            string unreg = _unregisteredLive > 0 ? $" · +{_unregisteredLive} unregistered (not saving!)" : "";
            if (DateTime.UtcNow > _footerOverrideUntil)
                FooterText.Text = snapshot.Count == 0
                    ? (_unregisteredLive > 0 ? $"{_unregisteredLive} session{(_unregisteredLive == 1 ? "" : "s")} running unregistered, not saving transcripts" : "No sessions · ⊞ opens the workspace")
                    : $"{snapshot.Count} session{(snapshot.Count == 1 ? "" : "s")} · {busy} busy"
                      + (hosted > 0 ? $" · {hosted} in tabs" : "")
                      + (dormant > 0 ? $" · {dormant} dormant{(hideDormant ? " (hidden)" : "")}" : "")
                      + unreg
                      + " · click a card to focus it";
            EmptyText.Text = snapshot.Count == 0
                ? "No active Claude Code sessions"
                : $"{dormant} dormant session{(dormant == 1 ? "" : "s")} hidden";
            EmptyText.Visibility = display.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SessionsRefreshed?.Invoke(snapshot.Count, busy);

            // Anything from the previous run that is alive again needs no restore.
            if (_pendingRestore.Count > 0)
                _pendingRestore.RemoveAll(p => snapshot.Any(s => s.SessionId == p.SessionId)
                                               || _workspace.HasSession(p.SessionId)
                                               || !System.IO.Directory.Exists(p.Cwd));

            // Nothing tracked from a previous run (Agmen wasn't running
            // before a crash): fall back to Claude Code's own transcripts.
            // Skipped when unregistered sessions exist, since those can't be
            // told apart from dead ones and we'd risk double-resuming.
            if (!_recentOffered)
            {
                _recentOffered = true;
                if (recent is not null && _unregisteredLive == 0)
                {
                    var live = snapshot.Select(s => s.SessionId).ToHashSet();
                    foreach (var r in recent)
                        if (!live.Contains(r.Entry.SessionId) && !_workspace.HasSession(r.Entry.SessionId)
                            && _pendingRestore.All(p => p.SessionId != r.Entry.SessionId))
                            _pendingRestore.Add(r.Entry);
                    if (_pendingRestore.Count > 0)
                        SetFooterNotice($"{_pendingRestore.Count} recently active session{(_pendingRestore.Count == 1 ? "" : "s")} not running — ↻ restores, right-click ↻ dismisses");
                }
            }

            RestoreButton.Content = $"↻ Restore {_pendingRestore.Count}";
            RestoreButton.Visibility = _pendingRestore.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RestoreButton.ToolTip = _pendingRestore.Count == 0 ? null :
                "Reopen these as workspace tabs, resuming each conversation (right-click to dismiss):\n"
                + string.Join("\n", _pendingRestore.Select(p => $"  • {p.Name} — {p.Cwd}"));
            UpdateSetButtons();

            RestoreService.SaveCurrent(snapshot, _pendingRestore);
            if (IsVisible && !_userMoved)
                Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12;
        }
        catch (Exception ex)
        {
            // One bad poll must never take the whole widget down; say so and
            // try again on the next tick.
            SetFooterNotice($"refresh failed: {ex.Message}");
        }
        finally { _refreshing = false; }
    }

    private void Reconcile(List<Models.SessionInfo> snapshot)
    {
        var byId = _sessions.ToDictionary(vm => vm.SessionId);
        var seen = new HashSet<string>();

        for (int i = 0; i < snapshot.Count; i++)
        {
            var info = snapshot[i];
            seen.Add(info.SessionId);
            if (byId.TryGetValue(info.SessionId, out var vm))
            {
                vm.Update(info);
                int cur = _sessions.IndexOf(vm);
                if (cur != i && i < _sessions.Count) _sessions.Move(cur, i);
            }
            else
            {
                vm = new SessionViewModel(info);
                _sessions.Insert(Math.Min(i, _sessions.Count), vm);
            }
            vm.IsHosted = _workspace.IsHostedPid(info.Pid);
            vm.Category = _workspace.CategoryOfPid(info.Pid);
        }

        for (int i = _sessions.Count - 1; i >= 0; i--)
            if (!seen.Contains(_sessions[i].SessionId))
                _sessions.RemoveAt(i);

        long maxOutput = snapshot.Count == 0 ? 1 : snapshot.Max(s => s.OutputTokens);
        foreach (var vm in _sessions)
        {
            vm.SetOutputScale(maxOutput);
            vm.RefreshClock();
        }
    }

    private void OnSessionClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionViewModel vm) return;
        e.Handled = true;
        _suppressAutoHideUntil = DateTime.UtcNow.AddSeconds(2);
        if (_workspace.FocusPid(vm.Pid))
        {
            SetFooterNotice($"showing '{vm.Name}' in the workspace");
            return;
        }
        bool ok = WindowFocusService.FocusSession(vm.Pid);
        SetFooterNotice(ok
            ? $"brought '{vm.Name}' to the front"
            : $"couldn't locate a window for '{vm.Name}' (pid {vm.Pid})");
    }

    private void SetFooterNotice(string text)
    {
        FooterText.Text = text;
        _footerOverrideUntil = DateTime.UtcNow.AddSeconds(4);
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    /// <summary>Remember the current CLI sessions as the saved set.</summary>
    public void SaveSessionSet()
    {
        int saved = RestoreService.SaveSet(_lastSnapshot);
        SetFooterNotice(saved > 0
            ? $"remembered {saved} CLI session{(saved == 1 ? "" : "s")} — ▶ relaunches them any time"
            : "no CLI sessions to remember (desktop sessions can't be relaunched externally)");
        UpdateSetButtons();
    }

    /// <summary>Relaunch every saved-set session that isn't already running.</summary>
    public void LaunchSessionSet()
    {
        var set = RestoreService.LoadSet();
        if (set is null || set.Entries.Count == 0)
        {
            SetFooterNotice("no saved session set yet — use 💾 to remember the current ones");
            return;
        }
        var live = _lastSnapshot.Select(s => s.SessionId).ToHashSet();
        var toLaunch = set.Entries.Where(en => !live.Contains(en.SessionId)).ToList();
        int alreadyRunning = set.Entries.Count - toLaunch.Count;
        if (toLaunch.Count == 0)
        {
            SetFooterNotice($"all {set.Entries.Count} saved sessions are already running");
            return;
        }
        int launched = _workspace.OpenResume(toLaunch);
        if (launched > 0) _workspace.ShowWorkspace();
        SetFooterNotice(launched > 0
            ? $"opening {launched} session{(launched == 1 ? "" : "s")} as tabs"
              + (alreadyRunning > 0 ? $" ({alreadyRunning} already running)" : "") + "…"
            : "nothing opened; see the workspace for details");
    }

    private void OnWorkspaceClick(object sender, RoutedEventArgs e) => _workspace.ShowWorkspace();

    private void UpdateSetButtons()
    {
        var set = RestoreService.LoadSet();
        LaunchSetButton.Visibility = set is null ? Visibility.Collapsed : Visibility.Visible;
        if (set is not null)
            LaunchSetButton.ToolTip =
                $"Relaunch saved session set ({set.Entries.Count} sessions, saved {set.SavedAt.LocalDateTime:g}):\n"
                + string.Join("\n", set.Entries.Select(en => $"  • {en.Name} — {en.Cwd}"));
    }

    private void OnSaveSetClick(object sender, RoutedEventArgs e) => SaveSessionSet();
    private void OnLaunchSetClick(object sender, RoutedEventArgs e) => LaunchSessionSet();

    /// <summary>
    /// Relaunch sessions whose transcripts were active within the window and
    /// that aren't registered as running — the crash-recovery path that works
    /// even when Agmen wasn't running beforehand.
    /// </summary>
    public void RestoreRecent(TimeSpan window)
    {
        string span = window.TotalHours >= 2 ? $"{window.TotalHours:0} hours" : "hour";
        var live = _lastSnapshot.Select(s => s.SessionId).ToHashSet();
        var candidates = RestoreService.RecentFromTranscripts(window)
            .Where(r => !live.Contains(r.Entry.SessionId))
            .Select(r => r.Entry)
            .ToList();
        if (candidates.Count == 0)
        {
            SetFooterNotice($"no stopped sessions with activity in the last {span}");
            return;
        }
        int launched = _workspace.OpenResume(candidates);
        if (launched > 0) _workspace.ShowWorkspace();
        string warn = _unregisteredLive > 0
            ? $" ({_unregisteredLive} running session{(_unregisteredLive == 1 ? "" : "s")} unregistered — some may already have been open)"
            : "";
        SetFooterNotice($"restoring {launched} session{(launched == 1 ? "" : "s")} active in the last {span}…{warn}");
    }

    private void OnRestoreDismiss(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _pendingRestore.Clear();
        RestoreButton.Visibility = Visibility.Collapsed;
        SetFooterNotice("restore list dismissed");
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var toRestore = _pendingRestore.ToList();
        _pendingRestore.Clear();
        RestoreButton.Visibility = Visibility.Collapsed;
        int launched = _workspace.OpenResume(toRestore);
        if (launched > 0) _workspace.ShowWorkspace();
        SetFooterNotice(launched > 0
            ? $"restoring {launched} session{(launched == 1 ? "" : "s")} as workspace tabs…"
            : "nothing restored; they may already be running or their folders are gone");
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: SessionViewModel }) return;
        if (e.ButtonState != MouseButtonState.Pressed) return;
        double left = Left, top = Top;
        DragMove(); // blocks until the mouse button is released
        if (Math.Abs(Left - left) > 2 || Math.Abs(Top - top) > 2)
        {
            _userMoved = true;
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            System.IO.File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                left = Left,
                top = Top,
                pinned = PinButton.IsChecked == true,
                hideDormant = HideDormantButton.IsChecked == true,
            }));
        }
        catch (Exception) { /* best-effort persistence */ }
    }

    private void LoadSettings()
    {
        try
        {
            if (!System.IO.File.Exists(SettingsPath)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(SettingsPath));
            var r = doc.RootElement;
            double left = r.GetProperty("left").GetDouble();
            double top = r.GetProperty("top").GetDouble();
            if (r.TryGetProperty("pinned", out var pin))
                PinButton.IsChecked = pin.GetBoolean();
            if (r.TryGetProperty("hideDormant", out var hd))
                HideDormantButton.IsChecked = hd.GetBoolean();

            // Only restore a position that is still on a connected screen.
            var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (virt.Contains(new System.Windows.Point(left + 40, top + 40)))
            {
                Left = left;
                Top = top;
                _userMoved = true;
            }
        }
        catch (Exception) { /* corrupt settings — fall back to defaults */ }
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
