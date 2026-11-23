using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Agmen.Agents;
using Agmen.Control;
using Agmen.Hosting;
using Agmen.Services;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;
using Color = System.Drawing.Color;
using MessageBox = System.Windows.MessageBox;

namespace Agmen;

public partial class App : Application
{
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _window;
    private WorkspaceWindow? _workspace;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _workspaceSignal;
    private ControlServer? _control;
    private Icon? _iconIdle, _iconBusy, _iconNone;
    private HostedTab? _lastAlert;
    private string _label = "Agmen";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second launch for the same workspace focuses its current window.
        bool wantsWorkspace = e.Args.Contains("--workspace", StringComparer.OrdinalIgnoreCase);
        _instanceMutex = new Mutex(true, AppPaths.InstanceName + "_Instance", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, AppPaths.InstanceName + "_Show");
        _workspaceSignal = new EventWaitHandle(false, EventResetMode.AutoReset, AppPaths.InstanceName + "_Workspace");
        if (!isFirst)
        {
            (wantsWorkspace ? _workspaceSignal : _showSignal).Set();
            Shutdown();
            return;
        }
        var waitThread = new Thread(() =>
        {
            var signals = new WaitHandle[] { _showSignal, _workspaceSignal };
            while (true)
            {
                int which = WaitHandle.WaitAny(signals);
                Dispatcher.BeginInvoke(() =>
                {
                    if (which == 0) _window?.ShowPanel();
                    else _workspace?.ShowWorkspace();
                });
            }
        }) { IsBackground = true };
        waitThread.Start();

        // Every hosted session parks pool threads on its pseudo-console for its whole life.
        // Raise the floor so registry polling never queues behind them.
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), io);

        var (removed, kept) = LoginEnvironment.Apply();
        Log.Write($"start {AppPaths.InstanceName} {typeof(App).Assembly.GetName().Version} (environment: dropped {removed} inherited, {kept} from login)");

        _label = AppPaths.WorkspaceTitle;
        _workspace = new WorkspaceWindow();
        _window = new MainWindow(_workspace);

        // The tool servers and hook commands that agents run in the tabs reach the workspace through this pipe.
        _control = new ControlServer(_workspace.Orchestrator.HandleAsync);
        _control.Start();

        // A purple ring marks the dev instance's tray icon.
        var ring = AppPaths.IsDev ? Color.FromArgb(178, 132, 255) : Color.FromArgb(236, 236, 241);
        _iconIdle = CreateTrayIcon(ring, Color.FromArgb(51, 209, 122));   // green: sessions, none busy
        _iconBusy = CreateTrayIcon(ring, Color.FromArgb(229, 165, 10));   // amber: at least one busy
        _iconNone = CreateTrayIcon(ring, Color.FromArgb(120, 120, 140));  // gray: no sessions
        _workspace.Icon = Imaging.CreateBitmapSourceFromHIcon(
            _iconIdle.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        _tray = new WinForms.NotifyIcon
        {
            Icon = _iconNone,
            Text = TrayText($"{_label} — Claude Code sessions"),
            Visible = true,
        };
        _window.SessionsRefreshed += (total, busy) =>
        {
            _tray.Icon = busy > 0 ? _iconBusy : total > 0 ? _iconIdle : _iconNone;
            var text = total == 0
                ? $"{_label} — no Claude Code sessions"
                : $"{_label} — {total} session{(total == 1 ? "" : "s")}, {busy} busy";
            _tray.Text = TrayText(text);
        };
        _tray.MouseUp += (_, a) =>
        {
            if (a.Button == WinForms.MouseButtons.Left) _window.Toggle();
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastAlert is not null) _workspace.FocusTab(_lastAlert);
            else _workspace.ShowWorkspace();
        };
        _workspace.Attention += OnAttention;
        _workspace.BackgroundNotice += text => _tray.ShowBalloonTip(6000, _label, text, WinForms.ToolTipIcon.Info);
        _workspace.NotifyRequested += (text, tab) =>
        {
            _lastAlert = tab ?? _workspace.Primary;
            Log.Write($"primary notification: {text}");
            _tray.ShowBalloonTip(8000, $"{_label} · primary session", text, WinForms.ToolTipIcon.Info);
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open workspace" + (AppPaths.OwnsGlobalHotkeys ? "  (Ctrl+Alt+W)" : ""), null, (_, _) => _workspace.ShowWorkspace());
        var workspaces = new WinForms.ToolStripMenuItem("Workspaces");
        workspaces.DropDownItems.Add("New workspace…", null, (_, _) => _workspace.NewNamedWorkspace());
        workspaces.DropDownItems.Add(new WinForms.ToolStripSeparator());
        workspaces.DropDownOpening += (_, _) =>
        {
            for (int i = workspaces.DropDownItems.Count - 1; i >= 2; i--)
            {
                var previous = workspaces.DropDownItems[i];
                workspaces.DropDownItems.RemoveAt(i);
                previous.Dispose();
            }
            foreach (var name in _workspace.WorkspaceNames())
                workspaces.DropDownItems.Add(new WinForms.ToolStripMenuItem(name.Replace("&", "&&"), null,
                    (_, _) => _workspace.OpenNamedWorkspace(name)) { Checked = name == AppPaths.WorkspaceName });
        };
        menu.Items.Add(workspaces);
        var newSession = new WinForms.ToolStripMenuItem("New session");
        foreach (var agent in AgentProfiles.All)
        {
            var a = agent;
            var item = newSession.DropDownItems.Add(AgentProfiles.DisplayName(a) + "…", null, (_, _) =>
            {
                _workspace.ShowWorkspace();
                _workspace.NewSessionDialog(a);
            });
            item.Enabled = AgentProfiles.IsInstalled(a);
        }
        menu.Items.Add(newSession);
        menu.Items.Add("Primary session", null, (_, _) => _workspace.OpenPrimary());
        menu.Items.Add("Session history…", null, (_, _) => _workspace.ShowHistory());
        menu.Items.Add("Restart all sessions", null, (_, _) => _workspace.RestartTabs(_workspace.Tabs.ToList()));
        menu.Items.Add("Agent settings…", null, (_, _) => _workspace.ShowAgentSettings());
        menu.Items.Add("Connect agents…", null, (_, _) => _workspace.ShowConnect());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Show / Hide panel" + (AppPaths.OwnsGlobalHotkeys ? "  (Ctrl+Alt+C)" : ""), null, (_, _) => _window.Toggle());
        menu.Items.Add("Remember current sessions", null, (_, _) => _window.SaveSessionSet());
        menu.Items.Add("Relaunch saved sessions", null, (_, _) => _window.LaunchSessionSet());
        var recent = new WinForms.ToolStripMenuItem("Restore recently active Claude Code sessions");
        recent.DropDownItems.Add("Last hour", null, (_, _) => _window.RestoreRecent(TimeSpan.FromHours(1)));
        recent.DropDownItems.Add("Last 4 hours", null, (_, _) => _window.RestoreRecent(TimeSpan.FromHours(4)));
        recent.DropDownItems.Add("Last 24 hours", null, (_, _) => _window.RestoreRecent(TimeSpan.FromHours(24)));
        menu.Items.Add(recent);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        var notify = new WinForms.ToolStripMenuItem("Notify me when a session needs me")
        {
            CheckOnClick = true,
            Checked = _workspace.Notifications,
        };
        notify.CheckedChanged += (_, _) => _workspace.Notifications = notify.Checked;
        menu.Items.Add(notify);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;

        // Windows logging off or restarting: keep the tab list so the next start resumes it.
        SessionEnding += (_, _) => _workspace?.SaveNow();

        int restored = _workspace.RestoreSavedTabs();
        bool opened = OpenFromArgs(e.Args);
        // The first time Agmen runs there is nothing to restore, so show where sessions start.
        if (!AppPaths.IsDefaultWorkspace || restored > 0 || opened || wantsWorkspace || WorkspaceStore.FirstRun) _workspace.ShowWorkspace();
        if (AppPaths.IsDefaultWorkspace && !wantsWorkspace) _window.ShowPanel();
    }

    private static string TrayText(string text) => text.Length <= 63 ? text : text[..62] + "…";

    /// <summary>
    /// Command line: <c>--open &lt;folder&gt;</c> starts a new session there, and
    /// <c>--resume &lt;session id&gt;</c> reopens a conversation. Both can repeat.
    /// <c>--open-agent &lt;agent&gt; &lt;folder&gt;</c> starts a session of another agent: codex, kimi or antigravity.
    /// </summary>
    private bool OpenFromArgs(string[] args)
    {
        bool any = false;
        for (int i = 0; i < args.Length; i++)
        {
            bool hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--open" when hasValue:
                    any |= _workspace!.OpenNew(args[i + 1]) is not null;
                    break;
                case "--open-agent" when i + 2 < args.Length:
                    if (AgentProfiles.Parse(args[i + 1]) is { } agent)
                        any |= _workspace!.OpenNew(args[i + 2], agent: agent) is not null;
                    else Log.Write($"--open-agent {args[i + 1]}: not an agent Agmen runs");
                    break;
                case "--resume" when hasValue:
                    if (RestoreService.FindSession(args[i + 1]) is { } entry)
                    {
                        _workspace!.OpenResume(new[] { entry });
                        any = true;
                    }
                    else Log.Write($"--resume {args[i + 1]}: no transcript found");
                    break;
            }
        }
        return any;
    }

    private void OnAttention(HostedTab tab, string kind)
    {
        _lastAlert = tab;
        Log.Write($"attention: {tab.Title} {kind}");
        if (_tray is null || _workspace?.Notifications != true) return;
        string text = kind switch
        {
            "waiting" => $"{tab.Title} needs your input.",
            "trust" => $"{tab.Title} is asking whether to trust its folder.",
            "ended" => $"{tab.Title} has ended.",
            _ => $"{tab.Title} finished. It's your turn.",
        };
        _tray.ShowBalloonTip(6000, _label, text, WinForms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        if (_workspace is { } ws)
        {
            int busy = ws.Tabs.Count(t => t.IsBusy);
            if (busy > 0)
            {
                var answer = MessageBox.Show(
                    $"{busy} session{(busy == 1 ? " is" : "s are")} still working.\n\n" +
                    "Exiting Agmen stops every session in its tabs. They resume automatically " +
                    "next time, but work in progress is interrupted.",
                    "Exit Agmen", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
            }
            ws.ShutdownTabs();
        }
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _control?.Dispose();
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Draws the tray icon in code so no .ico asset needs shipping.</summary>
    private static Icon CreateTrayIcon(Color ringColor, Color dotColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new Pen(ringColor, 3.5f);
            g.DrawEllipse(ring, 4, 4, 24, 24);
            using var dot = new SolidBrush(dotColor);
            g.FillEllipse(dot, 11, 11, 10, 10);
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
