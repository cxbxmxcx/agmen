using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Agmen.Agents;
using Agmen.Models;
using Agmen.Services;
using EasyWindowsTerminalControl;
using EasyWindowsTerminalControl.Internals;
using Microsoft.Terminal.Wpf;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace Agmen.Hosting;

public enum TabState { Pending, Starting, Running, Ended, Failed }

/// <summary>
/// One agent session living in an Agmen tab: a terminal control running the agent's CLI
/// (Claude Code, Codex, Kimi Code or Antigravity) on a pseudo-console, plus everything the
/// sidebar card shows about it.
/// </summary>
public sealed class HostedTab : INotifyPropertyChanged
{
    /// <summary>Windows Terminal's default Campbell scheme, as COLORREF values (0x00BBGGRR).</summary>
    private static readonly TerminalTheme Campbell = new()
    {
        DefaultBackground = 0x0C0C0C,
        DefaultForeground = 0xCCCCCC,
        DefaultSelectionBackground = 0x5A5A5A,
        CursorStyle = CursorStyle.BlinkingBar,
        ColorTable = new uint[]
        {
            0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1, 0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
            0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9, 0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2,
        },
    };

    private static readonly string TerminalFont =
        Fonts.SystemFontFamilies.Any(f => f.Source == "Cascadia Mono") ? "Cascadia Mono" : "Consolas";

    private volatile bool _bracketedPaste;
    private string _tail = "";
    // Claude's question whether to trust the folder, seen on screen before its prompt came up.
    // Set on the read thread; the next poll turns it into an alert, once.
    private volatile bool _trustQuestion;
    private bool _trustQuestionTaken;
    private string _screenTail = "";
    private string? _lastStatus;
    // A session reports "busy" briefly while it starts or resumes. Only a busy spell that
    // begins after the session has been seen at rest is real work worth an alert.
    private bool _seenAtRest;
    private bool _busyIsWork;
    private IntPtr _hwnd;
    private DateTime _startedAt;
    private bool _needsAttention;
    private string? _attentionKind;
    private TabCategory _category;

    public HostedTab(string cwd, string? sessionId, Guid? id = null, string? knownTitle = null,
                     TabCategory category = TabCategory.None, bool isPrimary = false, AgentKind agent = AgentKind.Claude)
    {
        Id = id ?? Guid.NewGuid();
        Cwd = cwd;
        SessionId = sessionId;
        KnownTitle = knownTitle;
        _category = category;
        IsPrimary = isPrimary;
        Agent = agent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }
    public string Cwd { get; }
    /// <summary>The agent CLI this tab runs.</summary>
    public AgentKind Agent { get; }
    /// <summary>The orchestrating session that reads and directs the others.</summary>
    public bool IsPrimary { get; }
    /// <summary>Extra command-line arguments for every start, such as the primary's MCP configuration.</summary>
    public IReadOnlyList<string> ExtraArgs { get; set; } = Array.Empty<string>();
    /// <summary>A first message passed on the command line when this tab starts a brand-new session. Used once.</summary>
    public string? InitialPrompt { get; set; }
    /// <summary>Session name for brand-new sessions (claude --name): its title and its address for other sessions.</summary>
    public string? LaunchName { get; set; }
    /// <summary>A model of this session's own, such as one its parent chose; null uses the agent's setting.</summary>
    public string? LaunchModel { get; set; }
    /// <summary>A reasoning effort of this session's own; null uses the agent's setting.</summary>
    public string? LaunchEffort { get; set; }

    /// <summary>How the agent starts: the user's settings for it, with this session's own model and effort.</summary>
    private AgentLaunch Launch
    {
        get
        {
            var settings = AgentSettings.For(Agent);
            return settings with { Model = LaunchModel ?? settings.Model, Effort = LaunchEffort ?? settings.Effort };
        }
    }
    /// <summary>
    /// For agents other than Claude: whether Agmen can be sure the agent's own prompt, not a
    /// trust question, would take typed text. Set from the agent monitor after every poll.
    /// </summary>
    public bool AgentReady { get; set; }
    /// <summary>The conversation this tab resumes; once the agent reports it, the one it is actually running.</summary>
    public string? SessionId { get; private set; }
    /// <summary>Last name seen for the session, shown before it registers and saved with the workspace.</summary>
    public string? KnownTitle { get; private set; }
    public TabState State { get; private set; } = TabState.Pending;
    public string? StateNote { get; private set; }
    public int Pid { get; private set; }
    public EasyTerminalControl? Terminal { get; private set; }
    /// <summary>Registry and transcript data for the running process, matched by pid.</summary>
    public SessionViewModel? Session { get; private set; }

    // ------------------------------------------------------------ lifecycle

    /// <summary>
    /// Create a fresh terminal running the tab's agent, resuming <see cref="SessionId"/> when
    /// known. The process starts once the control is loaded into the window. Returns null
    /// when the agent is not installed.
    /// </summary>
    public EasyTerminalControl? Start()
    {
        Stop();
        LoginEnvironment.Apply();   // the child inherits our environment, so refresh it from the registry
        bool withPrompt;
        var commandLine = Agent == AgentKind.Claude ? ClaudeCommand(out withPrompt) : OtherAgentCommand(out withPrompt);
        if (commandLine is null) return null;

        var ctl = new EasyTerminalControl
        {
            StartupCommandLine = commandLine,
            Win32InputMode = true,
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey | EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
            FontFamilyWhenSettingTheme = new FontFamily(TerminalFont),
            FontSizeWhenSettingTheme = 12,
        };
        ctl.SetValue(EasyTerminalControl.WorkingDirectoryProperty, Cwd);
        ctl.Theme = Campbell;
        ctl.ConPTYTerm.InterceptOutputToUITerminal = WatchOutput;
        // The library starts two output readers and keeps whichever runs first. When its start
        // thread wins, that thread never gets to close the console host, so every ended session
        // left an OpenConsole.exe behind. Hold the start thread until the terminal's reader runs.
        var term = ctl.ConPTYTerm;
        term.TermReady += (_, _) => SpinWait.SpinUntil(() => term.ReadLoopStarted, TimeSpan.FromSeconds(5));

        if (!string.IsNullOrEmpty(Session?.Name)) KnownTitle = Session.Name;
        Terminal = ctl;
        Session = null;
        Pid = 0;
        AgentReady = false;
        _hwnd = IntPtr.Zero;
        _bracketedPaste = false;
        _tail = "";
        _trustQuestion = false;
        _trustQuestionTaken = false;
        _screenTail = "";
        _lastStatus = null;
        _seenAtRest = false;
        _busyIsWork = false;
        _startedAt = DateTime.Now;
        _userDraft = false;
        _restSince = DateTime.MaxValue;
        _deliveredAt = null;
        _inFlight = null;
        _inFlightStarted = false;
        if (_promptOnCommandLine is not null && ReportFirstPromptTo is { } reportTo)
        {
            // The first message went in on the command line: follow its turn as if it had been typed.
            _inFlight = new OutboxMessage { Text = _promptOnCommandLine, Origin = "parent", ReportTo = reportTo };
            _deliveredAt = DateTime.Now;
            _deliveredOrigin = "parent";
        }
        _turnStartedBy = "user";
        _promptOnCommandLine = null;
        ReportFirstPromptTo = null;
        ToolServerSeen = false;
        _restarting = false;
        State = TabState.Starting;
        StateNote = null;
        Raise(string.Empty);
        var shown = commandLine;
        if (withPrompt) shown = shown[..Math.Min(shown.Length, 160)] + (shown.Length > 160 ? "…\"" : "");
        Log.Write($"tab {ShortId}: start {shown} in {Cwd}");
        return ctl;
    }

    /// <summary>claude.exe with the tab's extra arguments, resuming or starting fresh. Null when Claude Code isn't installed.</summary>
    private string? ClaudeCommand(out bool withPrompt)
    {
        withPrompt = false;
        var claude = AgentProfiles.Locate(AgentKind.Claude);
        if (claude is null)
        {
            Fail("claude.exe was not found on PATH");
            return null;
        }

        // A session that never got a message has nothing to resume, and claude --resume would
        // exit at once. Start a fresh session in the same folder instead.
        if (SessionId is not null && !RestoreService.HasTranscript(SessionId))
        {
            Log.Write($"tab {ShortId}: {SessionId} has no saved conversation yet, starting a new session instead");
            SessionId = null;
        }

        var cmd = new System.Text.StringBuilder(Quote(claude));
        foreach (var arg in AgentSettings.ClaudeArgs(Launch).Concat(ExtraArgs)) cmd.Append(' ').Append(QuoteArg(arg));
        if (SessionId is null && !string.IsNullOrWhiteSpace(LaunchName)) cmd.Append(" --name ").Append(QuoteArg(LaunchName));
        if (SessionId is not null) cmd.Append(" --resume ").Append(SessionId);
        else if (!string.IsNullOrWhiteSpace(InitialPrompt)) cmd.Append(' ').Append(QuoteArg(InitialPrompt));
        // A prompt on the command line waits for Claude to be ready, including any folder trust question.
        withPrompt = SessionId is null && !string.IsNullOrWhiteSpace(InitialPrompt);
        InitialPrompt = null;
        return cmd.ToString();
    }

    /// <summary>
    /// The command line for Codex, Kimi Code or Antigravity, resuming <see cref="SessionId"/>
    /// when known. Codex gets Agmen's tool server and turn-finished notify command as -c
    /// overrides; Kimi and Antigravity read theirs from the global config that Connect writes,
    /// and start as the agmen agent that Connect installs.
    /// Null when the agent isn't installed.
    /// </summary>
    private string? OtherAgentCommand(out bool withPrompt)
    {
        withPrompt = false;
        var prompt = SessionId is null && !string.IsNullOrWhiteSpace(InitialPrompt) ? InitialPrompt : null;
        InitialPrompt = null;
        var cmd = new System.Text.StringBuilder();
        switch (Agent)
        {
            case AgentKind.Codex:
                if (AgentProfiles.CodexEntryPoint() is not { } entry)
                {
                    Fail("codex was not found on PATH");
                    return null;
                }
                cmd.Append(Quote(entry.Node)).Append(' ').Append(QuoteArg(entry.Script));
                if (SessionId is not null) cmd.Append(" resume");
                // Like a Claude Code primary, a Codex one gets Agmen's tools and not the user's other
                // MCP servers: it directs sessions and does no work of its own.
                var servers = IsPrimary ? Enumerable.Empty<string>() : SharedServers.CodexOverrides();
                foreach (var arg in AgentSettings.CodexArgs(Launch).Concat(ToolServer.CodexOverrides(IsPrimary)).Concat(servers))
                    cmd.Append(' ').Append(QuoteArg(arg));
                if (SessionId is not null) cmd.Append(' ').Append(QuoteArg(SessionId));
                else if (prompt is not null)
                {
                    cmd.Append(' ').Append(QuoteArg(prompt));
                    withPrompt = true;
                    _promptOnCommandLine = prompt;
                }
                break;
            case AgentKind.Kimi:
                if (AgentProfiles.Locate(AgentKind.Kimi) is not { } kimi)
                {
                    Fail("kimi.exe was not found on PATH");
                    return null;
                }
                AgentConnect.KeepServersShared(AgentKind.Kimi);
                cmd.Append(Quote(kimi));
                if (SessionId is not null) cmd.Append(" -S ").Append(QuoteArg(SessionId));
                // A new session asks for the agmen agent, Kimi's default with Agmen's guide; a resumed
                // one keeps the agent it started with. Kimi 2.1's interactive mode still binds its
                // default agent whatever --agent or --agent-file says, so until it honors them a
                // Kimi tab learns Agmen from the tools' descriptions and a child's first message.
                else if (AgentConnect.AgentFor(AgentKind.Kimi) is { } kimiAgent) cmd.Append(" --agent ").Append(kimiAgent);
                foreach (var arg in AgentSettings.KimiArgs(Launch)) cmd.Append(' ').Append(QuoteArg(arg));
                // Kimi has no flag for a first message, so it waits in the queue until Kimi is ready for it.
                if (prompt is not null) Enqueue(new OutboxMessage { Text = prompt, Origin = "parent", ReportTo = ReportFirstPromptTo });
                // Nor for its effort: that comes only from the environment, which this tab's process
                // can get only through cmd. Every part of the command line here is free of cmd's
                // special characters: paths, ids, and model names the settings checked.
                if (AgentSettings.Checked(AgentKind.Kimi, Launch).Effort is { } effort)
                {
                    var shell = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec ? comspec : "cmd.exe";
                    var kimiCommand = cmd.ToString();
                    cmd.Clear().Append(Quote(shell)).Append(" /d /s /c \"set \"").Append(AgentSettings.KimiEffortVariable)
                       .Append('=').Append(effort).Append("\" && ").Append(kimiCommand).Append('"');
                }
                break;
            case AgentKind.Antigravity:
                if (AgentProfiles.Locate(AgentKind.Antigravity) is not { } agy)
                {
                    Fail("agy.exe was not found on PATH");
                    return null;
                }
                AgentConnect.KeepServersShared(AgentKind.Antigravity);
                cmd.Append(Quote(agy));
                // The agmen agent, Antigravity's default with Agmen's guide, also on resume, so a
                // conversation started before Connect picks it up.
                if (AgentConnect.AgentFor(AgentKind.Antigravity) is { } agyAgent) cmd.Append(" --agent ").Append(agyAgent);
                foreach (var arg in AgentSettings.AntigravityArgs(Launch)) cmd.Append(' ').Append(QuoteArg(arg));
                if (SessionId is not null) cmd.Append(" --conversation ").Append(QuoteArg(SessionId));
                else if (prompt is not null)
                {
                    cmd.Append(" -i ").Append(QuoteArg(prompt));
                    withPrompt = true;
                    _promptOnCommandLine = prompt;
                }
                break;
        }
        return cmd.ToString();
    }

    /// <summary>Kill the session's process, if one is running.</summary>
    public void Stop()
    {
        var term = Terminal?.ConPTYTerm;
        try { term?.StopExternalTermOnly(); }
        catch (Exception ex) { Log.Write($"tab {ShortId}: stop failed: {ex.Message}"); }
        if (term is not null) _ = CloseConsoleAsync(term);
    }

    /// <summary>
    /// Close the pseudo-console once its process has gone, which ends its OpenConsole.exe host.
    /// The library does this itself only when its start thread wasn't left reading (see <see cref="Start"/>).
    /// </summary>
    private async Task CloseConsoleAsync(TermPTY term)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && StillRunning()) await Task.Delay(100);
        // Closing waits for the host to flush its output and exit, so keep it off the UI thread.
        await Task.Run(() =>
        {
            try { ((ITerminalConnection)term).Close(); }
            catch (Exception ex) { Log.Write($"tab {ShortId}: closing the console failed: {ex.Message}"); }
        });

        bool StillRunning()
        {
            try { return term.Process is { HasExited: false }; }
            catch (Exception) { return false; }   // already gone
        }
    }

    // Set while a restart waits for the old process to exit, so the tab doesn't count as ended.
    private bool _restarting;

    /// <summary>
    /// Kill the session's process for a restart and wait, up to a limit, until it has exited, so
    /// its conversation is never open in two processes at once.
    /// </summary>
    public async Task StopForRestartAsync(TimeSpan limit)
    {
        _restarting = true;
        int pid = Pid;
        Stop();
        if (pid <= 0) return;
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (process.HasExited) return;
            }
            catch (Exception) { return; }   // already gone
            await Task.Delay(100);
        }
        Log.Write($"tab {ShortId}: process {pid} still running after {limit.TotalSeconds:0} s; restarting anyway");
    }

    /// <summary>Show the tab as ended without starting it, e.g. restored after the user exited Claude.</summary>
    public void MarkEnded(string? note)
    {
        State = TabState.Ended;
        StateNote = note;
        Raise(string.Empty);
    }

    /// <summary>Refresh process state. Returns true when the session has just ended.</summary>
    public bool Poll()
    {
        if (Terminal is null || _restarting || State is TabState.Pending or TabState.Ended or TabState.Failed) return false;
        try
        {
            if (Terminal.ConPTYTerm?.Process is ProcessFactory.WrappedProcess wp)
            {
                if (Pid == 0)
                {
                    Pid = wp.Pid;
                    State = TabState.Running;
                    Raise(string.Empty);
                    Log.Write($"tab {ShortId}: running as pid {Pid}");
                }
                bool exited;
                try { exited = wp.HasExited; }
                catch (Exception) { exited = true; }   // already gone before we first looked
                if (!exited) return false;

                int? code = null;
                try { code = wp.Process.ExitCode; } catch (Exception) { /* exit code unavailable */ }
                if (_inFlight is not null) _turnReport = (_inFlight, "ended");
                _inFlight = null;
                _deliveredAt = null;
                State = TabState.Ended;
                StateNote = code is null or 0 ? "session ended" : $"session ended (exit code {code})";
                Raise(string.Empty);
                Log.Write($"tab {ShortId}: {StateNote}");
                return true;
            }
            if ((DateTime.Now - _startedAt).TotalSeconds > 45)
                Fail($"{AgentProfiles.DisplayName(Agent)} did not start");
        }
        catch (Exception ex) { Log.Write($"tab {ShortId}: poll failed: {ex.Message}"); }
        return false;
    }

    /// <summary>
    /// Apply the latest registry data for this tab's process. Returns "finished" or
    /// "waiting" when Claude has just handed the turn back to the user.
    /// </summary>
    public string? UpdateFrom(SessionInfo info)
    {
        if (Session is null) Session = new SessionViewModel(info);
        else Session.Update(info);
        if (!string.IsNullOrEmpty(info.SessionId)) SessionId = info.SessionId;
        if (!string.IsNullOrEmpty(info.Name)) KnownTitle = info.Name;

        var status = info.EffectiveStatus;
        string? alert = null;
        if (status == "busy" && _lastStatus != "busy")
        {
            _busyIsWork = _seenAtRest;
            // A turn began, so whatever the user had typed has been sent.
            _userDraft = false;
            // Started by a message Agmen typed in, or else by the user.
            _turnStartedBy = _deliveredAt is not null ? _deliveredOrigin ?? "user" : "user";
            if (_deliveredAt is not null)
            {
                _deliveredAt = null;
                _inFlightStarted = _inFlight is not null;
            }
        }
        if (_lastStatus == "busy" && status != "busy" && _busyIsWork) alert = status == "waiting" ? "waiting" : "finished";
        else if (status == "waiting" && _lastStatus is not null && _lastStatus != "waiting") alert = "waiting";
        // A turn its parent started is the parent's to follow up, not the user's.
        if (alert == "finished" && _turnStartedBy == "parent") alert = null;
        if (_lastStatus == "busy" && AtRest(status) && _inFlight is not null && _inFlightStarted)
        {
            _turnReport = (_inFlight, "idle");
            _inFlight = null;
            _inFlightStarted = false;
        }
        if (AtRest(status)) { if (!AtRest(_lastStatus)) _restSince = DateTime.Now; }
        else _restSince = DateTime.MaxValue;
        if (status != "busy") _seenAtRest = true;
        _lastStatus = status;
        Raise(string.Empty);
        return alert;
    }

    public void Flag(string kind)
    {
        _attentionKind = kind;
        _needsAttention = true;
        Raise(nameof(NeedsAttention));
        Raise(nameof(AttentionText));
    }

    private void Fail(string message)
    {
        State = TabState.Failed;
        StateNote = message;
        Raise(string.Empty);
        Log.Write($"tab {ShortId}: failed: {message}");
    }

    // ------------------------------------------------------ clipboard

    /// <summary>
    /// Track whether Claude turned on bracketed paste (DECSET 2004), the way a terminal
    /// does, so pasted text can be wrapped and its line breaks do not send the message, and
    /// notice Claude asking whether to trust the folder. Runs on the pseudo-console read thread.
    /// </summary>
    private void WatchOutput(ref Span<char> str)
    {
        var s = _tail + str.ToString();
        int on = s.LastIndexOf("\x1b[?2004h", StringComparison.Ordinal);
        int off = s.LastIndexOf("\x1b[?2004l", StringComparison.Ordinal);
        if (on >= 0 || off >= 0) _bracketedPaste = on > off;
        _tail = s.Length > 16 ? s[^16..] : s;

        // The question comes before Claude registers its session, and only then.
        if (Agent == AgentKind.Claude && Session is null && !_trustQuestion)
        {
            var text = _screenTail + Visible(str.ToString());
            if (TrustQuestion.IsMatch(text)) _trustQuestion = true;
            _screenTail = text.Length > 80 ? text[^80..] : text;
        }
    }

    private static readonly Regex TrustQuestion = new(@"trust\s+this\s+folder", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CursorForward = new(@"\x1b\[\d*C", RegexOptions.Compiled);
    private static readonly Regex Escape = new(@"\x1b(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)|[@-Z\\-_])", RegexOptions.Compiled);

    /// <summary>Terminal output as the text it shows: the pseudo-console writes some spaces as cursor moves.</summary>
    private static string Visible(string output) => Escape.Replace(CursorForward.Replace(output, " "), "");

    /// <summary>True once, when Claude has just asked whether to trust the folder.</summary>
    public bool TakeTrustQuestion()
    {
        if (!_trustQuestion || _trustQuestionTaken) return false;
        _trustQuestionTaken = true;
        return true;
    }

    /// <summary>Paste text the way Windows Terminal does. The control itself would send Ctrl+V as a keystroke.</summary>
    public void Paste(string text)
    {
        if (State != TabState.Running || Terminal?.ConPTYTerm is not { } term) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        term.WriteToTerm(_bracketedPaste ? "\x1b[200~" + text + "\x1b[201~" : text);
    }

    /// <summary>Text selected with the terminal's own selection (Shift+drag), if any.</summary>
    public string? SelectedText
    {
        get
        {
            try { return Terminal?.Terminal?.GetSelectedText(); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>True when the window handle belongs to this tab's terminal, i.e. it has keyboard focus.</summary>
    public bool OwnsWindow(IntPtr hwnd)
    {
        if (Terminal is null || hwnd == IntPtr.Zero) return false;
        if (_hwnd == IntPtr.Zero) _hwnd = FindHwndHost(Terminal)?.Handle ?? IntPtr.Zero;
        return _hwnd != IntPtr.Zero && (hwnd == _hwnd || IsChild(_hwnd, hwnd));
    }

    // ------------------------------------------------------ messages typed in for the primary

    private readonly List<OutboxMessage> _outbox = new();
    // The user pressed keys in this tab since the last turn began: there may be a half-typed
    // message in Claude's prompt, and typing into it would mangle both.
    private bool _userDraft;
    private DateTime _restSince = DateTime.MaxValue;
    private DateTime? _deliveredAt;
    // Who typed in the message that went in last ("parent", "child" or "agmen"), and who started
    // the turn under way or just over: that sender, or "user".
    private string? _deliveredOrigin;
    private string _turnStartedBy = "user";
    private OutboxMessage? _inFlight;
    private bool _inFlightStarted;
    private (OutboxMessage Message, string Outcome)? _turnReport;
    private bool _trusted;
    private DateTime _trustCheckedAt = DateTime.MinValue;

    public int QueuedCount => _outbox.Count;
    public bool HasQueued => _outbox.Count > 0;
    public bool HasQueuedKey(string key) => _outbox.Any(m => m.Key == key);

    /// <summary>Queue a message for this session's prompt. A keyed message replaces an older one with the same key.</summary>
    public void Enqueue(OutboxMessage message)
    {
        if (message.Key is not null) _outbox.RemoveAll(m => m.Key == message.Key);
        _outbox.Add(message);
        RaiseQueue();
    }

    public int ClearQueue()
    {
        int n = _outbox.Count;
        _outbox.Clear();
        RaiseQueue();
        return n;
    }

    /// <summary>Why queued messages have not gone out yet, in words for the card and the primary.</summary>
    public string HoldReason
    {
        get
        {
            if (State is TabState.Pending or TabState.Starting || StillStarting) return "the session is still starting";
            if (State != TabState.Running) return "the session isn't running";
            if (_lastStatus is null) return IsTrustedFolder() ? "the session hasn't reached its prompt yet" : NotReadyReason;
            if (_lastStatus == "busy") return "the session is working, and gets it when it finishes this turn";
            if (_lastStatus == "waiting") return "the session is waiting for the user to answer it";
            if (_userDraft) return "the user has typed in that tab without sending, so it waits until they send or clear it";
            if (!IsTrustedFolder()) return NotReadyReason;
            if (_deliveredAt is not null) return "the previous message is still being taken in";
            return "it goes out within a few seconds";
        }
    }

    /// <summary>
    /// Type the next queued message into Claude's prompt and send it, if that is safe now: the
    /// session is idle and has been for the message's settle time, it isn't waiting on the
    /// user, the user has nothing half-typed here, and the folder has been trusted. With
    /// <paramref name="force"/> (the user's own "deliver now") a possible draft is ignored.
    /// </summary>
    public bool TryDeliver(bool force = false)
    {
        if (_outbox.Count == 0 || State != TabState.Running || Terminal is not { } ctl) return false;
        var now = DateTime.Now;
        if (_deliveredAt is { } at)
        {
            if (now - at < TimeSpan.FromSeconds(20)) return false;   // the last one has not started a turn yet
            Log.Write($"tab {ShortId}: a delivered message started no turn");
            _deliveredAt = null;
            _inFlight = null;
        }
        if (!AtRest(_lastStatus) || !_seenAtRest || StillStarting) return false;
        if (_userDraft && !force) return false;
        var next = _outbox[0];
        if (!force && now - _restSince < next.MinIdle) return false;
        if (!IsTrustedFolder()) return false;

        _outbox.RemoveAt(0);
        _deliveredAt = now;
        _deliveredOrigin = next.Origin;
        _inFlight = next.ReportBack ? next : null;
        _inFlightStarted = false;
        TypeMessage(ctl, next.Text);
        Log.Write($"tab {ShortId}: delivered a message from {next.Origin} ({next.Text.Length} chars)");
        RaiseQueue();
        return true;
    }

    /// <summary>
    /// Claude is at its prompt: "idle", or "shell" when its turn is over but a background
    /// command it started is still running.
    /// </summary>
    private static bool AtRest(string? status) => status is "idle" or "shell";

    /// <summary>The message whose turn just finished, and how ("idle" or "ended"), once.</summary>
    public (OutboxMessage Message, string Outcome)? TakeTurnReport()
    {
        var report = _turnReport;
        _turnReport = null;
        return report;
    }

    /// <summary>
    /// The user pressed a key that reached this terminal. Anything but Enter and the modifiers
    /// may leave text in Claude's prompt; Ctrl+C clears it.
    /// </summary>
    public void NoteUserKey(bool clearsInput)
    {
        bool before = _userDraft;
        _userDraft = !clearsInput;
        if (before != _userDraft) RaiseQueue();
    }

    /// <summary>
    /// True when typed text would reach the agent's prompt rather than a question it asks first.
    /// For Claude: the folder is trusted. For the others: the agent monitor says so.
    /// </summary>
    private bool IsTrustedFolder()
    {
        if (Agent != AgentKind.Claude) return AgentReady;
        // Claude registers its session only once it is past the trust question; in the home
        // folder, whose trust it never saves, that is the only sign.
        if (_trusted || Session is not null) return true;
        if (DateTime.UtcNow - _trustCheckedAt < TimeSpan.FromSeconds(15)) return false;
        _trustCheckedAt = DateTime.UtcNow;
        _trusted = Orchestration.ClaudeConfig.IsTrusted(Cwd);
        return _trusted;
    }

    /// <summary>
    /// Agents other than Claude report themselves idle before their first turn, while their
    /// screen may still be drawing; give them a few seconds before typing anything in.
    /// </summary>
    private bool StillStarting => Agent != AgentKind.Claude && DateTime.Now - _startedAt < TimeSpan.FromSeconds(6);

    private string NotReadyReason => Agent switch
    {
        AgentKind.Codex when !ToolServerSeen =>
            "Codex hasn't started Agmen's tools yet, so it may be asking the user something first, such as whether to update or to trust the folder",
        AgentKind.Claude when Orchestration.ClaudeConfig.IsHome(Cwd) =>
            "Claude asks the user to trust the home folder every time it starts there, and is waiting for that answer",
        AgentKind.Claude => "Claude is asking the user whether to trust this folder",
        _ => $"{AgentProfiles.DisplayName(Agent)} hasn't finished a turn here and doesn't trust the folder yet, so it may be asking the user to trust it",
    };

    // ------------------------------------------------------ Codex's startup questions

    /// <summary>
    /// The agent has started Agmen's tool server in this run of the tab. Codex starts it only
    /// once it is past its own startup questions, such as whether to update or to trust the
    /// folder, so from then on typed text reaches its prompt and not one of those questions,
    /// where Enter would pick the first answer.
    /// </summary>
    public bool ToolServerSeen { get; private set; }

    public void NoteToolServer() => ToolServerSeen = true;

    /// <summary>
    /// Paste the text, so its line breaks stay inside the message, then press Enter on its
    /// own a moment later, once Claude has taken the paste in.
    /// </summary>
    private async void TypeMessage(EasyTerminalControl ctl, string text)
    {
        try
        {
            text = text.Replace("\r\n", "\n").Trim();
            var term = ctl.ConPTYTerm;
            term.WriteToTerm(_bracketedPaste ? "\x1b[200~" + text.Replace('\n', '\r') + "\x1b[201~" : text.Replace('\n', ' '));
            await Task.Delay(500);
            if (Terminal == ctl && State == TabState.Running) term.WriteToTerm("\r");
        }
        catch (Exception ex) { Log.Write($"tab {ShortId}: typing a message failed: {ex.Message}"); }
    }

    private void RaiseQueue()
    {
        Raise(nameof(QueuedCount));
        Raise(nameof(HasQueued));
        Raise(nameof(QueueText));
        Raise(nameof(QueueTooltip));
    }

    public string QueueText => $"✉ {_outbox.Count}";

    public string QueueTooltip => _outbox.Count == 0 ? "" :
        $"{_outbox.Count} message{(_outbox.Count == 1 ? "" : "s")} waiting to be typed in: {HoldReason}.\n"
        + "Right-click the tab to deliver now or discard.";

    // ------------------------------------------------------ what the primary says about it

    private string? _note;

    /// <summary>The primary's one-line summary of what this session is doing, shown on its card.</summary>
    public string Note
    {
        get => _note ?? "";
        set
        {
            var note = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_note == note) return;
            _note = note;
            Raise();
            Raise(nameof(HasNote));
            Raise(nameof(Tooltip));
        }
    }

    public bool HasNote => !string.IsNullOrEmpty(_note);

    // ------------------------------------------------------ what the card shows

    public string FolderLeaf
    {
        get
        {
            var leaf = Path.GetFileName(Cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(leaf) ? Cwd : leaf;
        }
    }

    public string Title => IsPrimary ? "Primary"
        : !string.IsNullOrEmpty(Session?.Name) ? Session!.Name
        : !string.IsNullOrEmpty(KnownTitle) ? KnownTitle : FolderLeaf;

    /// <summary>The agent's short name on the card, in its own color.</summary>
    public string AgentLabel => AgentProfiles.ShortName(Agent);
    public Brush AgentBrush => AgentProfiles.BadgeBrush(Agent);
    public Visibility AgentBadgeVisibility => Visibility.Visible;

    public DateTime StartedAt => _startedAt;

    /// <summary>The process the tab started, also in the seconds before the poll first records it in <see cref="Pid"/>.</summary>
    public int CurrentPid
    {
        get
        {
            if (Pid > 0) return Pid;
            try { return Terminal?.ConPTYTerm?.Process is ProcessFactory.WrappedProcess wp ? wp.Pid : 0; }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>This tab as the agent monitor sees it.</summary>
    public AgentTabRef AgentRef() => new(Id, Agent, Cwd, CurrentPid, _startedAt, SessionId);

    // ------------------------------------------------------------ place in the tree

    private Guid? _parentId;
    private int _depth, _childCount;
    private bool _collapsed, _hiddenByCollapse;
    private string? _parentTitle;

    /// <summary>The session that started this one, or that the user put it under; null at the top level.</summary>
    public Guid? ParentId
    {
        get => _parentId;
        set
        {
            if (_parentId == value) return;
            _parentId = value;
            Raise();
        }
    }

    /// <summary>Set by the workspace after every change to the tree.</summary>
    public void SetTreePlace(int depth, int childCount, bool hiddenByCollapse, string? parentTitle)
    {
        if (_depth == depth && _childCount == childCount && _hiddenByCollapse == hiddenByCollapse && _parentTitle == parentTitle) return;
        _depth = depth;
        _childCount = childCount;
        _hiddenByCollapse = hiddenByCollapse;
        _parentTitle = parentTitle;
        Raise(string.Empty);
    }

    public int Depth => _depth;
    public int ChildCount => _childCount;
    public bool HasChildren => _childCount > 0;
    public string? ParentTitle => _parentTitle;
    /// <summary>A collapsed parent above this card folds it out of the list.</summary>
    public bool HiddenByCollapse => _hiddenByCollapse;

    /// <summary>The user folded this session's children away under its card.</summary>
    public bool IsCollapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            Raise();
            Raise(nameof(ChildToggleText));
        }
    }

    /// <summary>Indents a child's card under its parent's.</summary>
    public Thickness TreeMargin => new(_depth * 16, 0, 0, 6);
    public Visibility ChildMarkVisibility => _depth > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChildToggleVisibility => _childCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ChildToggleText => (_collapsed ? "▸ " : "▾ ") + _childCount;

    /// <summary>
    /// The session has messaged its parent since the parent's latest message reached it, so
    /// the parent needs no separate note that the turn is over.
    /// </summary>
    public bool RepliedToParent { get; set; }

    /// <summary>
    /// Who to tell when the turn started by a first message passed on the command line is
    /// over. Codex and Antigravity take a first message that way; it is tracked as if typed.
    /// </summary>
    public Guid? ReportFirstPromptTo { get; set; }
    private string? _promptOnCommandLine;

    /// <summary>Middle-truncated, because same-folder sessions differ only in their last characters.</summary>
    public string DisplayTitle
    {
        get
        {
            if (IsPrimary) return "★ Primary";
            var t = Title;
            return t.Length <= 30 ? t : $"{t[..22]}…{t[^6..]}";
        }
    }

    public bool IsRunning => State == TabState.Running;
    public bool IsBusy => IsRunning && Session?.IsBusy == true;
    public bool IsWaiting => IsRunning && Session?.StatusText == "waiting";
    public bool IsEnded => State is TabState.Ended or TabState.Failed;

    public string StatusText => State switch
    {
        TabState.Pending => "queued",
        TabState.Starting => "starting…",
        TabState.Ended or TabState.Failed => StateNote ?? "ended",
        _ => Session?.StatusText switch
        {
            null => "running",
            "shell" => "idle · background command running",
            var s => s,
        },
    };

    public string DetailLine
    {
        get
        {
            var place = IsPrimary ? "orchestrator" : FolderLeaf;
            var model = Session?.ModelText;
            return IsRunning && !string.IsNullOrEmpty(model) ? $"{place} · {model}" : $"{place} · {StatusText}";
        }
    }

    public string BranchText => Session?.BranchText ?? "";
    public string Elapsed => IsRunning ? Session?.Elapsed ?? "" : "";

    public bool NeedsAttention
    {
        get => _needsAttention;
        set
        {
            if (_needsAttention == value) return;
            _needsAttention = value;
            if (!value) _attentionKind = null;
            Raise();
            Raise(nameof(AttentionText));
        }
    }

    /// <summary>
    /// Take down a "your turn" badge that no longer holds: the session is working again, or
    /// sessions under it are. Badges asking the user for something stay.
    /// </summary>
    public void ClearYourTurn()
    {
        if (_needsAttention && _attentionKind is "finished" or null) NeedsAttention = false;
    }

    public string AttentionText => _attentionKind switch
    {
        "waiting" => "needs input",
        "trust" => "trust folder?",
        "ended" => "ended",
        _ => "your turn",
    };

    /// <summary>What the session is working on, as the user color-coded it.</summary>
    public TabCategory Category
    {
        get => _category;
        set
        {
            if (_category == value) return;
            _category = value;
            Raise(string.Empty);
        }
    }

    public bool HasCategory => _category != TabCategory.None;
    public Brush? CategoryBrush => TabCategories.BrushFor(_category);
    public string CategoryLabel => TabCategories.Label(_category);
    public Brush TitleBrush => CategoryBrush ?? TabCategories.DefaultText;

    public string Tooltip =>
        (IsPrimary ? $"Primary session ({AgentProfiles.DisplayName(Agent)}): reads and directs your other sessions" : $"{Title} ({AgentProfiles.DisplayName(Agent)})")
        + (HasCategory ? $"  [{CategoryLabel}]" : "")
        + (_parentTitle is not null ? $"\nchild of {_parentTitle}" : "")
        + (_childCount > 0 ? $"\n{_childCount} child session{(_childCount == 1 ? "" : "s")}" : "")
        + (HasNote ? $"\n{Note}" : "")
        + $"\n{Cwd}\nsession {SessionId ?? "(new, not registered yet)"}" + (Pid > 0 ? $"\npid {Pid}" : "")
        + "\nRight-click to color-code or for more actions";

    /// <summary>Re-evaluate everything the card shows, e.g. so elapsed time ticks.</summary>
    public void Refresh() => Raise(string.Empty);

    private string ShortId => Id.ToString()[..8];

    // ------------------------------------------------------------ helpers

    private static string Quote(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;

    /// <summary>Quote one argument by the usual Windows command-line rules, so any text survives intact.</summary>
    private static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"' }) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static HwndHost? FindHwndHost(DependencyObject root)
    {
        if (root is HwndHost host) return host;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindHwndHost(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
}
