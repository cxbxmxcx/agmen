# Launch video plan

A 3 to 3.5 minute demo that follows the README from top to bottom. It uses the same demo state as the README's screenshots, so the two match.

- **Goal.** A Windows developer who uses one or more agent CLIs sees, in the first 15 seconds, sessions of different agents starting and answering each other, and by the end knows how to install Agmen.
- **Format.** Screen recording of the workspace window with a voice-over. No webcam needed. 1080p or 1440p, 30 fps.
- **Length.** 2 to 4 minutes. The outline below runs about 3:20 after waits are trimmed.

## Before you record

### 1. Build the demo state

```powershell
# From the repository root. Builds Agmen into C:\agmen-demo\Agmen and starts it as "Agmen-demo".
.\docs\launch\demo-setup.ps1 -Build -Reset
```

`demo-setup.ps1` creates two sample projects and a separate copy of Agmen:

| What | Where |
|---|---|
| `weather-cli`, a tiny Python project on the branch `feature/forecast-cache`, with a cache bug to find | `C:\agmen-demo\weather-cli` |
| `weather-site`, a one-page site with a TODO for install and usage sections | `C:\agmen-demo\weather-site` |
| The demo copy of Agmen | `C:\agmen-demo\Agmen\Agmen.exe` |
| Its data, apart from your real tabs and settings | `%APPDATA%\Agmen-demo` |

The demo copy starts with one tab, the primary, run by Claude Code, in a 1400 × 880 window. `-PrimaryAgent codex`, `kimi` or `antigravity` starts it as that agent, and `-PrimaryAgent none` starts with no primary, so **★** asks which agent should run it. Its agent settings are Claude Code on `sonnet` with file edits accepted, and Codex at low effort asking when needed. That keeps turns short on screen and still leaves a command for a session to ask about, which shot 6 needs.

Run the script with `-Reset` again to return to exactly this state. `-Remove` deletes the sample projects and the demo data.

### 2. Answer the one-time questions off camera

Each agent asks once whether to trust a new folder. Do a dry run first, so the recording doesn't stop on them:

1. In the primary's tab, answer Claude's trust question for its folder.
2. Start a Claude Code, a Codex, a Kimi Code and an Antigravity session in each sample folder, answer the trust question, and close the tab.
3. If Codex shows **Update available**, choose **Skip**, or update it before recording. A Codex child that opens on that question loses its first task.
4. Exit the demo copy from its tray icon, then run `demo-setup.ps1 -Reset` and repeat step 1.

### 3. Privacy check

The demo copy shares your agent sign-ins and your PC, so a few things can still show something private. Check each one before you publish a recording or a screenshot.

| Where | What can show | What to do |
|---|---|---|
| Antigravity's banner | Your Google account email | Keep an Antigravity tab unselected, or scroll its banner out of view before selecting it |
| Claude Code's banner | Your plan name | Fine to show; crop if you'd rather not |
| Codex's tool output | Paths under `C:\Users\<you>` | Don't hold on a Codex tab while it runs commands; show its card in the tree instead |
| A Kimi Code primary | Its status line shows the primary's folder, under `C:\Users\<you>`, and it lists your MCP servers as they start | Show a Kimi Code primary's card, not its terminal. Claude Code and Codex write that folder as `~\AppData\…` |
| The session panel | Every Claude Code session on the PC, by name | Keep it closed, or record on a clean Windows account |
| The session history window | Every agent's sessions from the past week | Same |
| The primary's answer to "what's everyone working on?" | Claude Code sessions outside Agmen, by name and folder, since `list_sessions` covers them | Ask about "your children" on camera, or record on a clean Windows account |
| The window title | `Agmen (Agmen-demo)` | Record the client area, or leave it |
| Tray notifications | Demo session names only | Fine to show |

**Connect moves Kimi Code and Antigravity to the demo copy.** Their global config points at one copy of Agmen. If you click **Connect here** in the demo copy for shot 9, open your real copy afterwards and Connect there again.

### 4. Recording setup

- Capture the workspace window only, not the whole screen. In OBS, use **Window Capture** with the client area.
- Keep the window at the size the script sets, so shots match the screenshots.
- Models answer in their own time. Record each shot in full and cut the waits in the edit; say "a few seconds later" where you cut.
- Record the voice-over after the screen. The lines below are a guide, not a script to read word for word.

## Outline

| Time | Part | README section |
|---|---|---|
| 0:00 | Hook: a tree of four agents at work | Hero image |
| 0:15 | The problem | Opening paragraph |
| 0:30 | Install and first run | Quick start |
| 0:50 | A session of any agent | Requirements, What Agmen does |
| 1:10 | The primary starts children | The session tree, The primary session |
| 1:55 | It tells you when a session needs you | Messages, Status, colors and alerts |
| 2:20 | Any agent can be a parent | Any agent, as parent or child |
| 2:40 | It all comes back | Restore |
| 3:00 | Settings and Connect | Agent settings, Connecting Kimi Code and Antigravity |
| 3:15 | Close | Quick start, Contributing |

## Shot list

Each shot names the README section it illustrates, what to do, and what to say.

### 1. Hook (0:00 to 0:15), hero image

- **On screen.** The finished tree from shot 7, sped up: cards appearing under the primary, dots pulsing amber, a report arriving in the primary's terminal. Record this last and place it first.
- **Say.** "This is Claude Code, Codex, Kimi Code and Antigravity, working in one window. And they're not just side by side. They're handing each other work."

### 2. The problem (0:15 to 0:30), opening paragraph

- **On screen.** A still of four separate terminal windows, then cut to the Agmen workspace.
- **Say.** "If you run more than one coding agent, you know the routine. Which terminal finished? Which one is waiting on me? And when I want one agent to check another's work, I'm the one copying text between them. Agmen is a Windows app that fixes that."

### 3. Install and first run (0:30 to 0:50), Quick start

- **On screen.** The README's Quick start on GitHub. Paste the install command into PowerShell. Cut to the installer's last lines, which list the agent CLIs it found. The workspace opens empty: "No sessions open".
- **Say.** "Install is one PowerShell line. No admin rights, no .NET to install. Agmen uses the agent CLIs you already have, signed in as you. It needs no API keys and sends nothing anywhere."

### 4. A session of any agent (0:50 to 1:10), What Agmen does

- **On screen.** In the demo copy: click **+ New session ▾**. Hold on the menu with the four agents. Pick **Claude Code**, choose `C:\agmen-demo\weather-cli`. The real Claude Code starts in a tab. Point at the card: status dot, model, branch.
- **Say.** "Every tab is the real CLI, in Windows Terminal's own terminal. Slash commands, plugins, permission prompts: all there. The card shows its status, model and git branch."
- **Then.** Close that tab, so the next shot starts from the primary alone.

### 5. The primary starts children (1:10 to 1:55), The session tree

- **On screen.** Select **★ Primary**. Type the first prompt:

  > In C:\agmen-demo\weather-cli, start three children and tell me when they report back: a Codex session named api-tests to add pytest tests for forecast(), a Claude Code session named cache-fix, colored bug, to find why the cache never expires and fix it, and a Claude Code session named site-docs in C:\agmen-demo\weather-site to add install and usage sections to index.html. Put a one-line summary on each card.

  Three cards appear under the primary, with colors and summaries. Their dots pulse while they work. Cut to the first report arriving in the primary's terminal: `[From your child, cache-fix] …`.
- **Say.** "This is the primary session. Here Claude Code runs it, but any of the four agents can. I ask for three sessions: one Codex, two Claude. They appear under it as a tree, each with a color and a one-line summary. I didn't open a single terminal. When a child finishes, its report comes back to the primary on its own."
- **Matches.** `docs/images/child-report.png`.

### 6. It tells you when a session needs you (1:55 to 2:20), Status, colors and alerts

- **On screen.** While the children work, one asks to run a command. Its dot turns blue and its card says **needs input**. Click the card, approve the command, click back to the primary. If a card shows a ✉ badge, hover over it to show why the message is waiting.
- **Say.** "Amber means working, green idle, blue waiting on me. When a session needs an answer, its card says so, and the taskbar flashes if I'm elsewhere. Agmen types a message into a session only when it's idle and I have nothing half-typed there. Until then it waits, and the badge tells me why."
- **Matches.** `docs/images/needs-input.png`.

### 7. Any agent can be a parent (2:20 to 2:40), Any agent, as parent or child

- **On screen.** In the primary, type the second prompt:

  > Have cache-fix start a Codex child named diff-review to review its uncommitted change. Also start a Claude Code child of your own named changelog in C:\agmen-demo\weather-cli, colored planning, to draft CHANGELOG.md. Tell me when they report back.

  `diff-review` appears indented under `cache-fix`: a third level. Click `cache-fix` to show the messages it exchanged with the primary. Then start a Kimi Code session and an Antigravity session from **+ New session ▾**, so all four badges are in the tree. Keep the Antigravity tab unselected.
- **Say.** "Children can have children. Here a Claude session puts a Codex session on reviewing its own fix. The review goes back up the tree: Codex to Claude, Claude to the primary, the primary to me. Each session can direct only its own children. Agmen checks that from the process tree, not from what a model claims."
- **Matches.** `docs/images/workspace.png`, `docs/images/orchestrator.png`, `docs/images/tree.gif`.

### 7b. Any agent can run the primary (optional, 15 seconds), The primary session

- **On screen.** Right-click **★** and choose **Codex**. Confirm. The primary's card changes its badge to Codex, and every session stays under it. Ask it "which of your children are done?".
- **Say.** "The primary doesn't have to be Claude. Switch it to Codex, Kimi Code or Antigravity, and the tree stays where it is. Your notes carry over."
- **Matches.** `docs/images/primary-agent.png`.
- **Mind.** Kimi Code and Antigravity need Connect in the demo copy first, which moves them away from your real copy. See the note above.

### 8. It all comes back (2:40 to 3:00), Restore

- **On screen.** Right-click the tray icon and choose **Exit**. The window closes. Start the demo copy again with `.\docs\launch\demo-setup.ps1`. Every card returns, in the same tree, with its color and summary, and each terminal shows its conversation resuming.
- **Say.** "Exit Agmen, reboot, or lose power. On the next start every tab comes back, with its conversation and its place in the tree."

### 9. Settings and Connect (3:00 to 3:15), Agent settings

- **On screen.** **+ New session ▾**, then **Agent settings…**: model, effort and permissions for each agent. Close it. Then **Connect agents…**: expand **What Connect writes** for Antigravity.
- **Say.** "Set each agent's model, effort and permissions once, and every session starts that way. Kimi Code and Antigravity need a one-time Connect. It shows exactly what it will write to their config before you choose, and Disconnect removes it."
- **Matches.** `docs/images/agent-settings.png`, `docs/images/connect.png`.

### 10. Close (3:15 to 3:25)

- **On screen.** The finished tree, then the GitHub page with the install command.
- **Say.** "Agmen is free and open source, for Windows 10 and 11. The link is below. If it's useful, a star helps other people find it."

## What the README's screenshots show

All were taken from the demo state above, with the two prompts in shots 5 and 7.

| Image | Taken |
|---|---|
| `workspace.png` | After shot 7, with a Kimi Code and an Antigravity session added, as the primary relays a follow-up to `cache-fix` |
| `tree.gif` | The primary's tab at the start of shot 7, at about three times real speed |
| `child-report.png` | During shot 5, as `api-tests` and `cache-fix` reported while `api-tests` was still working |
| `needs-input.png` | During shot 5, with `cache-fix` selected while it asked to run a command |
| `orchestrator.png` | After shot 7, with `cache-fix` selected |
| `new-session-menu.png`, `primary-agent.png` | The **+ New session ▾** menu and the **★** menu; each composed from two captures, the window and its menu, at their on-screen positions |
| `agent-settings.png`, `connect.png` | The two windows from shot 9 |

To retake one, repeat the shot and capture the workspace window's client area at the script's window size.
