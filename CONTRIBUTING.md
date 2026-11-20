# Contributing to Agmen

Thanks for helping. Agmen is a small WPF app with one maintainer, so the most useful contributions are bug reports with a log, fixes for an agent CLI that changed its behavior, and small focused pull requests.

## Report a bug

Open an [issue](https://github.com/cxbxmxcx/agmen/issues/new/choose) and include:

- your Agmen version, from the file properties of `Agmen.exe` or the first line of a run in the log;
- the agent and its version, such as `claude --version`, `codex --version`, `kimi --version` or `agy --version`;
- what you did, what you expected and what happened;
- the lines of `%APPDATA%\Agmen\agmen.log` from around that time.

**Read the log before you paste it.** It records folder paths, session names, notifications the primary sent, and the command lines Agmen starts. Those include your Windows user name and can include the start of a child's first task. It doesn't record your conversations. Remove anything you don't want public.

For a security problem, don't open an issue. See [SECURITY.md](SECURITY.md).

## Build and run

You need 64-bit Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/cxbxmxcx/agmen
cd agmen
dotnet build
.\bin\Debug\net10.0-windows\Agmen.exe
```

A Debug build is a separate **dev** copy. It keeps its data in `%APPDATA%\Agmen-dev`, has its own single-instance lock and a purple-ringed tray icon, and leaves the global shortcut keys to your installed copy. You can rebuild and run it while your real sessions keep running in the installed one.

Two things to know:

- **A running copy locks its build output.** If you use the dev copy for real work, build your changes somewhere else: `dotnet build --artifacts-path ..\agmen-build`.
- **Kimi Code and Antigravity point at one copy at a time.** Their global config holds the path of the Agmen that connected them. **Connect** in a second copy moves them to it, and the sessions in the first copy lose Agmen's tools until you Connect there again.

To try a release build, run `.\publish.ps1`, which writes `out\Agmen\Agmen.exe` and the zip a release ships.

To test without touching your own tabs, set `AGMEN_INSTANCE` to a name of your choice, such as `Agmen-test`. That copy gets its own data folder, lock and tool pipe. `docs\launch\demo-setup.ps1` builds a complete sample workspace that way.

## Where things are

| Folder | What's in it |
|---|---|
| `Agents/` | One profile per agent CLI: finding it, its command line, its status and transcripts, Connect, and the MCP servers every session shares |
| `Hosting/` | A tab: the terminal, the agent's process, the message queue, and what is saved for restore |
| `Orchestration/` | The tool calls sessions make, the primary session's folder, and reading transcripts |
| `Mcp/`, `Control/`, `Hooks/` | The tool server each agent starts (`Agmen.exe --mcp`), the pipe it talks to Agmen over, and the hook command (`Agmen.exe --hook`) |
| `Services/` | Claude Code's session registry, restore, git info, the login environment, the log |
| `WorkspaceWindow.*`, `MainWindow.*` | The workspace window with its tree of tabs, and the floating session panel |
| `docs/PLAN.md` | The design, what each CLI supports, and what testing found |

## Pull requests

- Open an issue first for anything larger than a fix, so we agree on the approach before you write it.
- Keep a pull request to one change. Say what you tested and with which agent versions.
- Match the code around you: file-scoped namespaces, nullable enabled, a summary comment on each type that says why it exists. Comments explain reasons, not what the next line does.
- There are no automated tests yet. Run the app and try the change with a real session. If you touch an agent's profile, say which version of that CLI you ran.
- Update `README.md` when behavior a user can see changes.

**Changes to another program's config need extra care.** Connect writes to Kimi Code's and Antigravity's global config. Anything it adds must be shown in the Connect preview, be removed by Disconnect, and do nothing in sessions outside Agmen.

By contributing, you agree that your contribution is licensed under the [MIT license](LICENSE).
