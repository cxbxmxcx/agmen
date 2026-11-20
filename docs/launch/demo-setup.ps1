<#
.SYNOPSIS
    Builds the clean demo state used for Agmen's screenshots and launch video.

.DESCRIPTION
    Creates two tiny sample projects under -Root, seeds a separate Agmen data folder
    (%APPDATA%\Agmen-demo) with a primary session, and starts a copy of Agmen as the
    "Agmen-demo" instance. That copy runs beside the one you use every day: it has its own
    data folder, its own single-instance lock and its own tool pipe, so your real tabs,
    history and settings are never touched.

    Run it again with -Reset to get back to exactly the same starting state.

    What the demo copy still shares with your real one: the agent CLIs and their sign-ins.
    Sessions you start in the demo are real sessions on your plans, and they show up in each
    agent's own history. The session panel and the session history window list sessions from
    the whole PC, so keep them out of frame unless you record on a clean Windows account.

.EXAMPLE
    # Build Agmen from this repository into C:\agmen-demo\Agmen, then start the demo.
    .\docs\launch\demo-setup.ps1 -Build

.EXAMPLE
    # Start over from the same clean state, using an Agmen you installed.
    .\docs\launch\demo-setup.ps1 -Reset -Exe "$env:LOCALAPPDATA\Programs\Agmen\Agmen.exe"

.EXAMPLE
    # Remove the sample projects and the demo data folder.
    .\docs\launch\demo-setup.ps1 -Remove
#>
[CmdletBinding()]
param(
    # Where the sample projects (and with -Build, the demo copy of Agmen) live. Keep it short and
    # neutral: the folder shows in every session's header.
    [string]$Root = 'C:\agmen-demo',
    # The Agmen.exe to run. Default: <Root>\Agmen\Agmen.exe, then the installed copy.
    [string]$Exe,
    # The instance name: the data folder is %APPDATA%\<Instance>.
    [string]$Instance = 'Agmen-demo',
    # Publish this repository into <Root>\Agmen first. Needs the .NET 10 SDK.
    [switch]$Build,
    # Delete the demo data folder and the sample projects, then set them up again.
    [switch]$Reset,
    # Delete the demo data folder and the sample projects, and stop.
    [switch]$Remove,
    # Set everything up but don't start Agmen.
    [switch]$NoLaunch,
    # Switch off tray notifications in the demo copy.
    [switch]$Quiet,
    # The agent the demo's primary session runs as. 'none' starts with no primary, so the star
    # button asks which agent should run it.
    [ValidateSet('claude', 'codex', 'kimi', 'antigravity', 'none')]
    [string]$PrimaryAgent = 'claude'
)

$ErrorActionPreference = 'Stop'
function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

if ($Instance -notmatch '^[A-Za-z0-9_-]+$' -or $Instance -in 'Agmen', 'Agmen-dev') {
    throw "Pick an instance name of your own, such as Agmen-demo. 'Agmen' and 'Agmen-dev' are the copies you use."
}
$Root = [IO.Path]::GetFullPath($Root)
$dataDir = Join-Path $env:APPDATA $Instance
$projects = @('weather-cli', 'weather-site')

function Get-DemoProcess {
    @(Get-CimInstance Win32_Process -Filter "Name='Agmen.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)
    })
}

# ---------------------------------------------------------------- reset and remove

if ($Reset -or $Remove) {
    if ((Get-DemoProcess).Count -gt 0) {
        throw "The demo copy of Agmen is still running from $Root. Exit it from its tray icon first."
    }
    Write-Step "Removing $dataDir"
    if (Test-Path $dataDir) { Remove-Item -LiteralPath $dataDir -Recurse -Force }
    foreach ($name in $projects) {
        $dir = Join-Path $Root $name
        if (Test-Path $dir) {
            Write-Step "Removing $dir"
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
    }
    if ($Remove) {
        Write-Host "Demo state removed. $Root\Agmen, if you built it, is still there." -ForegroundColor Green
        exit 0
    }
}

# ---------------------------------------------------------------- the demo copy of Agmen

if ($Build) {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $out = Join-Path $Root 'Agmen'
    if ((Get-DemoProcess).Count -gt 0) { throw "The demo copy of Agmen is running from $out. Exit it first." }
    Write-Step "Publishing $repo to $out"
    # The same build a release ships, so the demo shows what people will download.
    & (Join-Path $repo 'publish.ps1') -OutDir $Root -NoZip
}

if (-not $Exe) {
    $candidates = @((Join-Path $Root 'Agmen\Agmen.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Agmen\Agmen.exe'))
    $Exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $NoLaunch -and (-not $Exe -or -not (Test-Path $Exe))) {
    throw 'No Agmen.exe found. Pass -Exe <path>, or -Build to publish this repository.'
}

# ---------------------------------------------------------------- sample projects

function Write-File([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
}

function Invoke-Git([string]$Dir, [string[]]$GitArgs) {
    & git -C $Dir -c user.name='Agmen Demo' -c user.email='demo@example.com' -c init.defaultBranch=main `
        -c core.autocrlf=false -c commit.gpgsign=false @GitArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in $Dir" }
}

function New-WeatherCli([string]$Dir) {
    Write-File (Join-Path $Dir 'README.md') @'
# weather-cli

A tiny command-line forecast. It answers from a built-in table, so it runs offline.

    weather Lisbon
    weather Oslo --days 3
'@
    Write-File (Join-Path $Dir 'pyproject.toml') @'
[project]
name = "weather-cli"
version = "0.3.0"
description = "A tiny command-line weather forecast"
requires-python = ">=3.10"

[project.scripts]
weather = "weather.cli:main"

[tool.pytest.ini_options]
testpaths = ["tests"]
'@
    Write-File (Join-Path $Dir 'weather\__init__.py') @'
"""A tiny command-line weather forecast."""
'@
    Write-File (Join-Path $Dir 'weather\forecast.py') @'
"""Look up a forecast and keep it in a small cache."""

import time

CACHE_TTL_SECONDS = 600

# Offline data, so the demo never needs a network.
_CLIMATE = {
    "lisbon": (19, "sunny"),
    "oslo": (6, "snow showers"),
    "nairobi": (24, "partly cloudy"),
    "toronto": (9, "rain"),
}

_cache = {}


def fetch_forecast(city, days=1):
    """Return one line per day for a city, or raise KeyError for a city we don't know."""
    base, sky = _CLIMATE[city.lower()]
    return [f"day {n + 1}: {base + n} C, {sky}" for n in range(days)]


def forecast(city, days=1, now=None):
    """Like fetch_forecast, but reuse an answer that is less than CACHE_TTL_SECONDS old."""
    now = time.time() if now is None else now
    key = (city.lower(), days)
    entry = _cache.get(key)
    if entry and now - entry["saved_at"] < CACHE_TTL_SECONDS * 1000:
        return entry["lines"]
    lines = fetch_forecast(city, days)
    _cache[key] = {"saved_at": now, "lines": lines}
    return lines
'@
    Write-File (Join-Path $Dir 'weather\cli.py') @'
"""The weather command."""

import argparse
import sys

from .forecast import forecast


def main(argv=None):
    parser = argparse.ArgumentParser(prog="weather", description="Show a forecast for a city.")
    parser.add_argument("city")
    parser.add_argument("--days", type=int, default=1)
    args = parser.parse_args(argv)
    try:
        for line in forecast(args.city, args.days):
            print(line)
    except KeyError:
        print(f"No forecast for {args.city}.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
'@
    Write-File (Join-Path $Dir 'tests\test_forecast.py') @'
from weather.forecast import fetch_forecast


def test_one_line_per_day():
    assert len(fetch_forecast("Oslo", days=3)) == 3
'@
    Write-File (Join-Path $Dir '.gitignore') @'
__pycache__/
.pytest_cache/
*.egg-info/
'@
    Invoke-Git $Dir @('init', '-q')
    Invoke-Git $Dir @('add', '-A')
    Invoke-Git $Dir @('commit', '-q', '-m', 'Add the weather command')
    Invoke-Git $Dir @('checkout', '-q', '-b', 'feature/forecast-cache')
}

function New-WeatherSite([string]$Dir) {
    Write-File (Join-Path $Dir 'README.md') @'
# weather-site

The documentation site for weather-cli: plain HTML, no build step.
'@
    Write-File (Join-Path $Dir 'index.html') @'
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>weather-cli</title>
  <link rel="stylesheet" href="style.css">
</head>
<body>
  <h1>weather-cli</h1>
  <p>A tiny command-line forecast.</p>
  <!-- TODO: an install section and a usage section -->
</body>
</html>
'@
    Write-File (Join-Path $Dir 'style.css') @'
body { font-family: system-ui, sans-serif; max-width: 40rem; margin: 3rem auto; }
'@
    Invoke-Git $Dir @('init', '-q')
    Invoke-Git $Dir @('add', '-A')
    Invoke-Git $Dir @('commit', '-q', '-m', 'Start the site')
}

New-Item -ItemType Directory -Path $Root -Force | Out-Null
foreach ($name in $projects) {
    $dir = Join-Path $Root $name
    if (Test-Path $dir) {
        Write-Step "Keeping $dir (use -Reset to rebuild it)"
        continue
    }
    Write-Step "Creating $dir"
    if ($name -eq 'weather-cli') { New-WeatherCli $dir } else { New-WeatherSite $dir }
}

# ---------------------------------------------------------------- the demo data folder

$workspaceFile = Join-Path $dataDir 'workspace.json'
if (Test-Path $workspaceFile) {
    Write-Step "Keeping $dataDir (use -Reset to start from the clean state)"
}
else {
    Write-Step "Seeding $dataDir"
    $primaryDir = Join-Path $dataDir 'primary'
    New-Item -ItemType Directory -Path $primaryDir -Force | Out-Null
    $primaryId = 'a9e00000-0000-4000-8000-000000000001'

    # One tab, the primary, in a 1400 x 880 window: the size every screenshot uses.
    # In a saved tab, no agent means Claude Code.
    $agentId = if ($PrimaryAgent -in 'claude', 'none') { $null } else { $PrimaryAgent }
    $tabs = @()
    if ($PrimaryAgent -ne 'none') {
        $tabs = @([ordered]@{
            Id = $primaryId; Cwd = $primaryDir; SessionId = $null; Title = $null; Ended = $false
            Category = $null; IsPrimary = $true; Note = $null; Agent = $agentId; ParentId = $null
            Collapsed = $false; Model = $null; Effort = $null
        })
    }
    $workspace = [ordered]@{
        Tabs = $tabs
        Selected = $primaryId
        PrimaryAgent = $(if ($PrimaryAgent -eq 'none') { $null } else { $PrimaryAgent })
        Bounds = [ordered]@{ Left = 40; Top = 10; Width = 1400; Height = 880; Maximized = $false }
        LastFolder = (Join-Path $Root 'weather-cli')
        LastAgent = 'claude'
        Notifications = (-not $Quiet)
        PrimarySessionId = $null
    }
    Write-File $workspaceFile ($workspace | ConvertTo-Json -Depth 5)

    # Launch settings that keep a demo moving: edits go through, commands still ask.
    $agents = [ordered]@{
        claude = [ordered]@{ Model = 'sonnet'; Effort = $null; Permissions = 'acceptEdits' }
        codex = [ordered]@{ Model = $null; Effort = 'low'; Permissions = 'on-request' }
    }
    Write-File (Join-Path $dataDir 'agents.json') ($agents | ConvertTo-Json -Depth 5)
}

# ---------------------------------------------------------------- launch

Write-Host ''
Write-Host "Sample projects: $Root\weather-cli (branch feature/forecast-cache) and $Root\weather-site" -ForegroundColor Green
Write-Host "Demo data:       $dataDir" -ForegroundColor Green
if ($NoLaunch) { exit 0 }

Write-Step "Starting $Exe as $Instance"
$env:AGMEN_INSTANCE = $Instance
try { Start-Process -FilePath $Exe -ArgumentList '--workspace' }
finally { Remove-Item Env:AGMEN_INSTANCE }

Write-Host ''
Write-Host 'The demo copy is starting with its primary session. Each agent asks once whether to trust a new folder.'
Write-Host 'The prompts for each shot are in docs\launch\video-plan.md. The first one:'
Write-Host ''
Write-Host "  In $Root\weather-cli, start three children and tell me when they report back:" -ForegroundColor Yellow
Write-Host '  a Codex session named api-tests to add pytest tests for forecast(), a Claude Code session' -ForegroundColor Yellow
Write-Host '  named cache-fix, colored bug, to find why the cache never expires, and a Claude Code session' -ForegroundColor Yellow
Write-Host "  named site-docs in $Root\weather-site to add install and usage sections to index.html." -ForegroundColor Yellow
