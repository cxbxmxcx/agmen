# Repository settings for the launch

What to set on github.com/cxbxmxcx/agmen when it goes public, and the commands that set it.

## Description

> Windows workspace for coding agents: run Claude Code, Codex, Kimi Code and Antigravity CLI sessions as a tree of tabs, where any session can start and direct child sessions of any agent.

## Website

https://github.com/cxbxmxcx/agmen/releases/latest

Change it to the launch video's address once the video is up.

## Topics

```
claude-code  codex  kimi-code  antigravity  ai-agents  coding-agents  agent-orchestration
multi-agent  mcp  model-context-protocol  ai-coding  developer-tools  windows  wpf
dotnet  csharp  terminal  conpty
```

## Social preview

[social-preview.png](social-preview.png), 1280 × 640. GitHub has no command for this one: upload it under **Settings → General → Social preview**.

## Commands

```powershell
gh repo edit cxbxmxcx/agmen `
  --description "Windows workspace for coding agents: run Claude Code, Codex, Kimi Code and Antigravity CLI sessions as a tree of tabs, where any session can start and direct child sessions of any agent." `
  --homepage "https://github.com/cxbxmxcx/agmen/releases/latest" `
  --add-topic claude-code,codex,kimi-code,antigravity,ai-agents,coding-agents,agent-orchestration,multi-agent,mcp,model-context-protocol,ai-coding,developer-tools,windows,wpf,dotnet,csharp,terminal,conpty `
  --enable-issues --enable-discussions=false --enable-wiki=false

# SECURITY.md sends reports to private advisories, which must be switched on.
gh api -X PUT repos/cxbxmxcx/agmen/private-vulnerability-reporting

# Only after everything above, and the release, are in place.
gh repo edit cxbxmxcx/agmen --visibility public --accept-visibility-change-consequences
```

## Check as a visitor

In a private browser window, with no GitHub sign-in:

1. https://github.com/cxbxmxcx/agmen shows the README with its images and the description and topics on the right.
2. https://github.com/cxbxmxcx/agmen/releases/latest lists `Agmen-win-x64.zip`, `Agmen-win-x64.zip.sha256` and `install.ps1`.
3. The install command from the README runs in a fresh PowerShell window and ends with Agmen's workspace open.
4. Paste the repository's address into a chat app or a social post draft: the preview shows the social image.
