# Agmen plan

*Agmen* is Latin for an army column on the march. It comes from *agere*, "to drive, to act", which is also the root of "agent".

Agmen is a personal Windows workspace for running agent CLIs side by side and directing them from one place. It grows out of an earlier app that hosted Claude Code sessions as tabs and let one primary session direct the others. Agmen keeps that core and adds two things: other agent CLIs, and a tree of sessions instead of one flat list.

## Decisions

- **Agents.** Claude Code, Codex, Kimi Code and Antigravity CLI (`agy`). Third-party guides report that Antigravity replaced the hosted Gemini CLI for personal Google accounts in June 2026. The Gemini CLI can be added later as another profile.
- **Tree.** Any session can be a parent or a child. Names for each position:
  - **orchestrator**: a session with children;
  - **primary**: the orchestrator at the top;
  - **worker**: a session without children.
- **Authority.** A parent directs only its immediate children.
- **Kept from the earlier app:**
  - the workspace window with sessions as tabs;
  - the floating panel and the tray icon;
  - restore after a restart or crash;
  - the pipe and tool server;
  - the safe-typing message queue.
- **Dropped:** the earlier app's other features, and its branding.
- **Global config.** Agmen may add its tool server, hooks, permission for its own tools and an `agmen` agent to Kimi's and agy's global config. Neither offers a per-launch option for that. The entries do nothing in sessions that run outside Agmen.
- **Closing.** Closing a session closes every session under it.
- **Repository.** github.com/cxbxmxcx/agmen. `main` holds the base. After that, every phase or feature gets its own branch and pull request. Each one is built and run before it merges.

## How sessions talk

Every session gets Agmen's tools over MCP, which all four CLIs support. A child calls `message_parent`. A parent calls `spawn_child`, `message_child`, `read_child` and `list_children`.

Agmen delivers a message by typing it into the receiving session's terminal when that session is free and you have nothing half-typed there. This works for any CLI.

Each CLI reports when a turn finishes. That tells Agmen when it can deliver, and when to tell a parent that its child is done.

Agmen identifies the caller from the process tree. Its tool server runs as a child process of the CLI in that tab, so Agmen walks up the process tree to find which tab made the call. A session can act only on its own direct children, whatever the model claims.

```
Parent (any CLI)                Agmen                            Child (any CLI)
 message_child ──tool call──▶ is it a direct child? ──types into──▶ child's prompt
 parent's prompt ◀──types into── routes it up ◀──tool call── message_parent
                              child's turn finished ──▶ "child finished" note to parent
```

Built-in channels such as Claude's `SendMessage` and `codex queue` stay optional. `SendMessage` can reach any Claude session on the PC, which would skip the direct-children rule. So Agmen's tools are the one channel for every CLI.

## What each CLI supports

This table was checked on 2026-10-23 against each CLI's `--help` on this PC and its docs. Phase 2 confirms it in practice.

| | Claude Code | Codex | Kimi Code | Antigravity (`agy`) |
|---|---|---|---|---|
| Version here | 2.1.309 | 0.164.0 | 2.1.1 | 1.4.6 |
| Agmen tools connected | per launch: `--mcp-config` | per launch: `-c mcp_servers.…` ✓ | `mcp.json`, global or in the project ✓ | `mcp_config.json`, global ✓ |
| Turn-finished signal | `~/.claude/sessions` status, and hooks via `--settings` | `notify` setting, per launch with `-c` ✓ | its session log, `wire.jsonl` ✓ (hooks are global only) | hooks: `SessionStart`, `PreInvocation`, `PostInvocation`, `Stop` ✓ |
| Session id comes from | session registry | `notify` payload (`thread-id`) ✓ | `session_index.jsonl` | hook `conversationId` ✓ |
| First message at launch | `claude "…"` | `codex "…"` | none; typed once it starts | `agy -i "…"` |
| Resume | `--resume <id>` | `codex resume <id>` | `-S <id>` | `--conversation <id>` |
| Role instructions | `--append-system-prompt` ✓ | `-c developer_instructions` ✓ | an `agmen` agent and `--agent`, which 2.1 ignores when interactive | an `agmen` agent and `--agent` ✓ |

Notes:

- Codex has a shared background server, but `--no-daemon` turned out not to matter. With or without it, Codex started Agmen's tool server and its `notify` command from its own process.
- Codex hooks need a one-time trust approval in `/hooks`. The older `notify` setting is set per launch and gives the turn-finished event with the last reply.
- Some CLIs can also send a terminal bell or an OSC 9 notification when a turn ends. Agmen hosts the terminal, so it sees them, which makes them a fallback signal.

## Phases

0. **Baseline.** *Done 2026-10-23.* Imported the earlier app unchanged, moved it to .NET 10, and built it. The app starts, and a tool call reaches it end to end through the tool server and the pipe.
1. **Strip and rename.** *Done 2026-10-23.* Removed the features Agmen doesn't need and the branding, about a third of the code. Renamed the app, namespace, exe, data folder, pipe, tray, tool server and primary to Agmen, at version 0.1.0. Session summaries now live on the tabs and are saved with the workspace. The primary was checked end to end: it starts, launches `Agmen.exe --mcp` as a child of `claude.exe`, and its tools reach the app.
2. **Quick tests.** *Done 2026-10-23*, using a test kit on the `phase-2/agent-checks` branch: `--hook`, `--spike` tabs and `probe.jsonl`. The tests ran in a sandbox folder, with project-level config only. See [Phase 2 findings](#phase-2-findings).
3. **Agent profiles.** *Built 2026-10-23 on `feature/agent-profiles`.* One profile per CLI covering:
   - finding and launching it, and resuming a session;
   - passing a first message and role instructions;
   - connecting Agmen's tools;
   - reading its status and session id.

   "New session" asks for a folder and an agent, and each card shows its agent. Kimi and Antigravity also get a one-time **Connect** step:
   - you run it yourself;
   - it shows exactly what it adds to that CLI's global config, and **Disconnect** removes it;
   - the entries do nothing outside Agmen: the tool server offers no tools, and the hooks exit at once, unless the process chain leads to an Agmen tab.
4. **The tree.** *Built 2026-10-23 on `feature/agent-profiles`; the round trips below are being tested.* This phase adds:
   - parent links on tabs, a tree sidebar, and saving and restoring the tree, where closing a session closes its subtree;
   - tools scoped to direct children, with messages routed both ways;
   - "child finished" notes to the parent;
   - limits on depth, number of children and how fast sessions can be started.

   It's done when parents and children talk both ways in every pairing that matters:
   - a Claude primary with a Codex child, which has a Kimi grandchild;
   - an Antigravity parent with a Claude child.

   In each case the parent sends work down, the child replies up, and the parent gets the "finished" note.
5. **Customization.** A settings file and a settings screen for:
   - agent profiles;
   - your own color labels in place of the fixed four;
   - theme and terminal colors, font and size;
   - hotkeys, notifications and tree limits.
6. **Panel and polish.** The floating panel lists sessions from all four CLIs. Also a new icon, a personal build and install script, and a README.

## Phase 2 findings

Tested on 2026-10-23 with Codex 0.164.0, Kimi Code 2.0.2 and Antigravity 1.4.6, each running in an Agmen tab.

- **They run fine in a tab.**
  - Drawing, typing, Enter, Shift+Enter for a new line, and paste all work in Codex, Kimi and Antigravity.
  - Agmen's typed delivery sends a message in each one. It works by pasting the text, then pressing Enter.
  - That includes Kimi once it has started, so Kimi's lack of a first-message flag doesn't matter.
- **Codex and Kimi called Agmen's tools directly.** Each called `list_sessions` as asked.
- **Antigravity connected but didn't call the tools.**
  - It started Agmen's tool server from the project config and connected to it, but its model never called the tools.
  - Instead, it wrote a PowerShell script that ran `Agmen.exe --mcp` itself.
  - `agy mcp list` shows only global servers. Phase 3 checks whether a global entry lets the model see the tools.
- **Every call traces back to its tab.** Every tool server and hook process had the tab in its process chain:
  - Codex: `Agmen.exe --mcp` ← `codex.exe` ← `node.exe` ← `cmd.exe` (the tab) ← Agmen.
  - Kimi: `Agmen.exe --mcp` ← `kimi.exe` (the tab) ← Agmen.
  - Antigravity: `Agmen.exe --mcp` ← `agy.exe` (the tab) ← Agmen. Its hooks run through `cmd.exe` ← `agy.exe`.

  Antigravity's improvised script traced back to the agy tab too, so a session can't escape its tab by starting the tool server itself.
- **Every CLI says when a turn finishes.**
  - **Codex `notify`** sends one `agent-turn-complete` per turn. It carries `thread-id` (the session id), `turn-id`, `cwd`, the prompt, and `last-assistant-message`, the reply text a parent needs.
  - **Kimi's session log** is `wire.jsonl`, in `~/.kimi-code/sessions/<workspace>/<session>/agents/main/`. It records:
    - `agent.turn.started`;
    - `interaction.request` while Kimi waits on you for an approval, and `interaction.resolved` after;
    - `turn.ended` and `prompt.completed`.

    No hooks or config changes are needed.
  - **Antigravity's hooks** fire `SessionStart`, then `PreInvocation` and `PostInvocation` around every model call. When the turn ends, `Stop` arrives with `fullyIdle: true`, `conversationId` and `transcriptPath`.
- **Global config needs you to run it.** Claude Code's safety check blocked me from adding Agmen to Kimi's and Antigravity's global config, because that makes Agmen start in every session of theirs. That's why Phase 3 has a Connect step that you run.

## Phase 3 and 4 findings

Tested on 2026-10-23 and 24 with Kimi Code 2.1.1 and Antigravity 1.4.6, connected through their global config, in Agmen tabs.

- **Unconnected, Kimi and Antigravity have no Agmen tools at all.** That's why their sessions knew nothing about Agmen. Agmen now offers Connect before it starts either one, and `spawn_child` refuses to start an unconnected one: it opens Connect and tells the parent to try again afterwards.
- **Connected, both see all eight tree tools.**
  - Kimi lists them as `mcp__agmen__*`.
  - Antigravity's model reads each tool's schema from `~/.gemini/antigravity-cli/mcp/agmen/` and calls it through `call_mcp_tool`. So the global entry does what the Phase 2 project entry didn't.
  - Kimi also reloads `mcp.json` while it runs. Tabs of either agent that were already running pick the tools up when restarted, and Agmen offers that restart when Connect finishes.
- **Antigravity takes Agmen's guide from a custom agent.** Connect writes `~/.gemini/config/agents/agmen/agent.md`, and Agmen starts agy with `--agent agmen`, also on resume, which agy accepts. A probe agent's instructions reached the model in `agy -p`.
- **Kimi 2.1 ignores the agent when interactive.**
  - Its v2 engine creates every interactive session with the default agent, whatever `--agent` or `--agent-file` says; only `kimi -p` honors them.
  - Connect still installs `~/.kimi-code/agents/agmen.md`, and Agmen still passes `--agent agmen`, so the guide arrives once Kimi fixes this.
  - Meanwhile a Kimi tab learns Agmen from the tools' descriptions, and a Kimi child also from the note on its first message.
- **Antigravity runs a hook as `cmd /c "<command>"`.** Quotes inside the command reach cmd as `\"` and break it, so the Agmen path in a hook command must be unquoted. It uses the 8.3 short path when the real one has spaces. With that fixed, the hooks give Agmen each conversation's id, status and transcript.
- **Every agent asks before each Agmen tool call, unless told otherwise.** A child stalls until the user switches to its tab and approves `whoami`, `message_parent` and the rest. So Agmen pre-approves its own tools, and only those:

  | Agent | How |
  |---|---|
  | Claude Code | `--allowedTools mcp__agmen` on the command line |
  | Codex | `-c mcp_servers.agmen.default_tools_approval_mode='approve'` |
  | Kimi Code | Connect appends a `[[permission.rules]]` table to `~/.kimi-code/config.toml`, with `decision = "allow"` and `pattern = "mcp__agmen__*"` |
  | Antigravity | Connect adds `mcp(agmen/*)` to `permissions.allow` in `~/.gemini/antigravity-cli/settings.json` |

  Antigravity has no hook for a pending confirmation, so its card shows busy, not waiting.
- **Claude never saves trust for the home folder.** Its own code calls home trust "session-only". Every Claude process started there asks whether to trust the folder, including after each Agmen restart, and its default answer is "No, exit". Claude registers its session only after that answer. So Agmen:
  - types into a Claude tab only once it has registered;
  - flags the tab "trust folder?" when the question shows;
  - tells a parent that a Claude child in the home folder waits for the user.
- **A fresh agent tab is ready when it trusts its folder.** Agmen used to wait for a first finished turn, so a message to an agy or Codex tab nobody had typed in never went out. Now it checks each agent's own record:
  - Kimi: its `workspace-trust` files;
  - Antigravity: `trustedWorkspaces` in its settings;
  - Codex: it asks only inside a git repository, and saves the answer under `[projects.'<folder>']`.
- **Older Kimi sessions store `createdAt` as text.** One of them stopped Agmen from following Kimi tabs at all, until the monitor learned to skip it.
- **Antigravity starts children too.** In the user's test, an agy session started a Claude child with `spawn_child`.
- **Children didn't have the user's usual MCP servers.** Only Claude sessions loaded them, from Claude's user level. Agmen now shares Claude Code's user-level servers with every agent.
  - **Merging duplicates.** Two entries with the same command or URL count as one server, because a Claude config can hold one server under two names.
  - **Codex** has no `--mcp-config` but takes `-c mcp_servers.<name>.…` overrides, and `codex mcp list` with them showed the servers. Servers with env values or headers stay off the command line, because those can be secrets.
  - **Kimi 2.1** has no MCP flags at all, so Connect writes the servers into `~/.kimi-code/mcp.json`. It writes remote ones as `{"url": …}`, and the user signs in with `/mcp-config login <name>`.
  - **Antigravity** gets them in `~/.gemini/config/mcp_config.json`. It writes remote ones as `{"serverUrl": …}`, and the user signs in through `/mcp`.
  - **What Agmen tracks.** It records in `shared-servers.json` what it added, as it wrote it, and changes or removes only those entries. A server the user removed stays out until Connect runs again.
- **Closed tabs left their console host running.** EasyWindowsTerminalControl starts two readers for a session's output: one from the WPF terminal's connection, and one on its own start thread. When the start thread wins, it never reaches `WaitForExit` and the `ClosePseudoConsole` after it. ConPTY then keeps `OpenConsole.exe` alive until `ClosePseudoConsole` is called. Closing 10 tabs left 8 hosts behind. Agmen now:
  - holds the start thread until the terminal's reader has started;
  - closes the pseudo-console itself once the process has gone.

## Any agent as the primary

Built and tested on 2026-11-20 with Claude Code 2.1.329, Codex 0.167, Kimi Code 2.5.2 and Antigravity 1.5.3, in a copy of Agmen started with `AGMEN_INSTANCE`. Each agent ran as the primary, started a child, got the child's report, and resumed its conversation after Agmen was killed and started again.

- **One folder, two instruction files.** The primary's folder holds `CLAUDE.md` for Claude Code and `AGENTS.md` for Codex, Kimi Code and Antigravity, which all read that name from the folder they start in. `AGENTS.md` can't import a file, so Agmen copies `primary-notes.md` into it at each start.
- **The tools follow the tab, not the agent.** The tool server asks Agmen which tab started it, and the primary's tab gets the four extra tools. Kimi Code listed 12 tools as the primary.
- **Switching keeps the tree.** The new primary's tab takes the old one's id, so the sessions under the primary stay under it. Each agent's primary conversation is saved under that agent.
- **Kimi Code and Antigravity need Connect first.** Their tools come only from their global config.
- **Kimi Code 2.5 asks before Agmen's tools.** The rule Connect writes to `config.toml` no longer stops the question.
- **Hooks need the instance name.** A hook command finds Agmen's pipe by `AGMEN_INSTANCE`. Agmen rebuilt its tabs' environment without it, so in a named copy the Antigravity hooks reached no one, and its primary came back without its conversation. The variable now stays.
- **Models name the first task their own way.** A parent sent `spawn_child` its task as `task`. The child started with nothing to do, and the parent waited. `spawn_child` now takes `message`, `task` or `prompt`, and says so when it got none.
- **Codex asks about updates only when it starts without a task.** That question takes Enter as "Update now". A Codex tab now takes typed messages only once Codex has started Agmen's tool server, which it does after its startup questions.
- **The card list reacted to typing.** With the list focused, typed letters jumped between cards. Type-to-select is off.

## Antigravity 1.5

Checked on 2026-11-21 with Antigravity 1.5.13, which had updated itself from 1.5.3 the day before.

- **An agent without `tools` is read-only.** A markdown agent that names no tools gets a small default set: it can read files and call MCP tools, but it has no `edit_file`, `write_file`, `run_command` or `ask_question`. The `agmen` agent named none, so Antigravity sessions in Agmen couldn't change anything. It now names 13 tools: the default agent's own, apart from the three an agent may not ask for by name. With them, a probe agent listed the same tools as the default agent, apart from `swarm`.
- **An unknown tool name stops the agent.** When an agent names a tool agy doesn't have, agy can't build it: "failed to construct executor: … tool … not found in registry". In print mode agy exits. In a tab it stays open, answers every message with that error and fires no hooks. `swarm`, `list_resources` and `read_resource` are in the default agent's list but aren't names an agent may ask for. So Agmen reads that refusal from agy's log and starts a new session again as agy's default agent, with the message that failed. A resumed conversation keeps its agent whatever the command line says, so it can't fall back.
- **A question shows in the log.** `ask_question` logs "Surfacing ask_question", as a tool confirmation logs "Surfacing tool confirmation". Agmen shows both as waiting, in a connected tab: only hooks end what the log starts, and an unconnected tab fires none.
- **A turn can end while it waits.** When the user cancels a turn or dismisses a question, the status goes from waiting straight to idle. That now counts as the end of the turn, so the parent hears of it.
- **Hooks still work.** All four fire in 1.5.13, with `conversationId`, `transcriptPath` and `modelName`.
- **`inheritCustomizations` exists now.** It decides whether an agent adopts the user's skills, rules, plugins, subagents and MCP servers. `inheritMcp` still brings the MCP servers on its own, so the agent keeps it.
- **A session without tools looked like any other.** A Kimi Code or Antigravity session that starts while its agent points at another copy of Agmen has none of this copy's tools. Agmen offered Connect once per run and not at all for restored tabs. The card now says "No Agmen tools".

## Sources

- Kimi Code: [MCP](https://moonshotai.github.io/kimi-code/en/customization/mcp), [hooks](https://moonshotai.github.io/kimi-code/en/customization/hooks)
- Codex: [hooks](https://learn.chatgpt.com/docs/hooks.md), [notify payload](https://www.mintlify.com/aannoo/hcom/integration/codex), [MCP tool approval](https://learn.chatgpt.com/docs/extend/mcp.md?surface=cli)
- Antigravity: [getting started](https://antigravity.google/docs/getting-started/), [hooks](https://antigravity.google/docs/hooks?tab=cli), [command reference](https://computingforgeeks.com/antigravity-cli-cheat-sheet/)
- Gemini CLI: [replaced for personal accounts](https://computingforgeeks.com/gemini-cli-cheat-sheet/), [hooks](https://geminicli.com/docs/hooks/reference.md), [system settings override](https://geminicli.com/docs/cli/enterprise/)
