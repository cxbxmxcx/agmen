# Security

## Report a vulnerability

Please report it privately: open a [security advisory](https://github.com/cxbxmxcx/agmen/security/advisories/new) instead of a public issue. Say what you found, how to reproduce it, and which version of Agmen you used. You'll get an answer within a week.

Agmen is maintained by one person in their own time. There is no bug bounty.

## What Agmen can do on your computer

Agmen runs agent CLIs that can read, change and run things in the folders you start them in, with the permissions you give them. Know what Agmen adds on top:

- **It starts and types into agent sessions.** A session can start child sessions and send them messages through Agmen's tools. Each session can direct only its own children, and limits apply: 4 levels deep, 12 children per session and 12 new children per session per hour.
- **Agmen's own tools run without an approval prompt.** That covers only the `agmen` tools, such as `spawn_child` and `message_parent`. Everything else an agent does still follows the permission mode you set for it.
- **A child's permissions are yours to set.** A parent can choose a child's model and effort, never its permission mode.
- **The tool pipe is yours alone.** Tool calls reach Agmen over a named pipe that only your Windows account can open. Agmen finds the calling tab from the caller's process tree, so a session can't act as another one.
- **A contextual fork or agent switch shares recorded conversation content with the destination CLI.** This includes prompts, replies and tool inputs and outputs, which can contain sensitive project data. Agmen stores handoffs under `%APPDATA%\Agmen\conversation-history` (or the selected instance's data folder). The destination CLI can send that content to its provider as part of its normal operation. A fork captures records up to the time of the fork.
- **Handoff access follows the calling tab.** `read_handoff` accepts a page offset and count. Agmen resolves the saved conversation from the live caller, including after a restore. The tab cannot select a transcript file or another tab's snapshot through that tool. Local files remain subject to the permissions of your Windows account and the receiving CLI.
- **Sibling messages carry the text the sender supplies.** Direct children of the same top-level tab can share an excerpt using the card menu or `message_sibling`. Agmen checks the sender and recipient against the current tree. The message identifies the sending sibling and waits in the recipient's ordinary delivery queue.
- **Named workspaces separate Agmen session trees and handoffs.** The tool server and hooks identify the workspace hosting the agent process. Workspaces still run under the same Windows account and use the agent CLIs' shared sign-ins and global configuration.
- **Connect changes two other programs' config.** For Kimi Code and Antigravity, Connect adds Agmen's tool server, a permission for Agmen's tools, an `agmen` agent, Antigravity's status hooks and your shared MCP servers to their global config. It shows exactly what it writes before you choose, keeps a `*.agmen-backup` copy of each file it first changes, and Disconnect removes what it added.
- **Your MCP servers reach every agent.** Agmen shares Claude Code's user-level MCP servers with the other agents. A server with env values or headers isn't put on Codex's command line, because those can hold secrets.
- **Nothing is sent anywhere by Agmen.** It has no telemetry and makes no network requests of its own. The agent CLIs talk to their own services, as they do outside Agmen.

Releases aren't code-signed. Each release lists the SHA-256 of its zip, and `install.ps1` checks the download against it.
