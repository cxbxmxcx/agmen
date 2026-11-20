<#
.SYNOPSIS
    Installs or updates Agmen for the current user from a GitHub release.

.DESCRIPTION
    Downloads the self-contained Agmen build from the cxbxmxcx/agmen releases, checks it against
    the release's SHA-256 checksum, installs it to %LOCALAPPDATA%\Programs\Agmen, adds Start menu
    and sign-in shortcuts, and starts it. Nothing needs administrator rights, and .NET doesn't
    need to be installed. Run it again at any time to update.

    Your settings and open tabs live in %APPDATA%\Agmen and are never touched.

.EXAMPLE
    irm https://github.com/cxbxmxcx/agmen/releases/latest/download/install.ps1 -OutFile "$env:TEMP\agmen-install.ps1"
    powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\agmen-install.ps1"

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\agmen-install.ps1" -Version v0.1.0 -NoStartup

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\agmen-install.ps1" -Uninstall
#>
[CmdletBinding()]
param(
    # Release tag to install, for example v0.1.0. Defaults to the latest release.
    [string]$Version = 'latest',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Agmen'),
    [string]$Repo = 'cxbxmxcx/agmen',
    # Install from a zip you already have, such as one publish.ps1 built, instead of downloading.
    [string]$ZipPath,
    # Do not start Agmen when you sign in to Windows.
    [switch]$NoStartup,
    # Do not create any shortcuts.
    [switch]$NoShortcuts,
    # Do not start Agmen after installing.
    [switch]$NoLaunch,
    # Stop a running copy instead of asking you to exit it. This stops the sessions in its tabs.
    [switch]$Force,
    # Remove Agmen and its shortcuts. Settings and saved tabs are kept.
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar slows downloads in Windows PowerShell
trap {
    Write-Host "ERROR: $_" -ForegroundColor Red
    exit 1
}

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

$zipName = 'Agmen-win-x64.zip'
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$exe = Join-Path $InstallDir 'Agmen.exe'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Agmen.lnk'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Agmen.lnk'

# Refuse to delete a folder that does not look like an Agmen install.
function Assert-SafeInstallDir {
    if (-not (Test-Path $InstallDir)) { return }
    $hasContent = @(Get-ChildItem -LiteralPath $InstallDir -Force).Count -gt 0
    if ($hasContent -and -not (Test-Path $exe)) {
        throw "$InstallDir already exists and does not contain Agmen.exe. Choose another -InstallDir."
    }
}

function Get-RunningCopy {
    @(Get-Process Agmen -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq $exe) } catch { $false }
    })
}

function Stop-RunningCopy {
    $running = Get-RunningCopy
    # Right after you exit Agmen, the tool servers it ran for its sessions (Agmen.exe --mcp)
    # can take a moment to close. Give them a few seconds before calling it a running copy.
    for ($i = 0; $i -lt 12 -and $running.Count -gt 0; $i++) {
        Start-Sleep -Milliseconds 500
        $running = Get-RunningCopy
    }
    if ($running.Count -eq 0) { return }
    if (-not $Force) {
        throw ("Agmen is running from $InstallDir. Exit it from its tray icon first " +
               "(sessions in its tabs resume on the next start), or rerun with -Force to stop it now.")
    }
    Write-Step 'Stopping the running copy (-Force)'
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# The agents whose global config names this copy's Agmen.exe as their agmen tool server.
function Get-ConnectedAgents {
    $configs = [ordered]@{
        'Kimi Code'   = Join-Path $env:USERPROFILE '.kimi-code\mcp.json'
        'Antigravity' = Join-Path $env:USERPROFILE '.gemini\config\mcp_config.json'
    }
    foreach ($name in $configs.Keys) {
        try {
            if (-not (Test-Path -LiteralPath $configs[$name])) { continue }
            $command = (Get-Content -LiteralPath $configs[$name] -Raw | ConvertFrom-Json).mcpServers.agmen.command
            if ($command -and ([IO.Path]::GetFullPath($command) -ieq $exe)) { $name }
        }
        catch { }   # a config we can't read is not ours to judge
    }
}

function New-Shortcut([string]$Path, [string]$Target, [string]$Arguments = '') {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.Arguments = $Arguments
    $link.WorkingDirectory = Split-Path $Target
    $link.Description = 'Agmen - agent sessions as a tree of tabs'
    $link.Save()
}

# ---------------------------------------------------------------- uninstall

if ($Uninstall) {
    Assert-SafeInstallDir
    # Connect put this copy's path into Kimi Code's and Antigravity's config. Removing the exe
    # first would leave them starting a tool server, and hooks, that no longer exist.
    $connected = @(Get-ConnectedAgents)
    if ($connected.Count -gt 0 -and -not $Force) {
        throw ("$($connected -join ' and ') still point at this copy of Agmen. Open Agmen, choose Connect agents... on the + menu " +
               "and Disconnect, then uninstall again. Or rerun with -Force to uninstall anyway and clean their config by hand.")
    }
    Stop-RunningCopy
    foreach ($link in $startMenuLink, $startupLink) {
        if (Test-Path $link) {
            $target = (New-Object -ComObject WScript.Shell).CreateShortcut($link).TargetPath
            if ($target -ieq $exe) { Remove-Item $link -Force }
        }
    }
    if (Test-Path $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    Write-Host "Agmen removed. Settings and saved tabs remain in $env:APPDATA\Agmen." -ForegroundColor Green
    if ($connected.Count -gt 0) {
        Write-Warning ("$($connected -join ' and ') still have an agmen entry in their config. Remove it from " +
                       '~\.kimi-code\mcp.json, or from ~\.gemini\config\mcp_config.json and hooks.json.')
    }
    exit 0
}

# ---------------------------------------------------------------- prerequisites

if (-not [Environment]::Is64BitOperatingSystem) { throw 'Agmen needs 64-bit Windows.' }
Assert-SafeInstallDir

# ---------------------------------------------------------------- download and install

$work = Join-Path ([IO.Path]::GetTempPath()) ('Agmen-install-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if ($ZipPath) {
        $zip = Get-Item -LiteralPath $ZipPath
        Write-Step "Installing from $($zip.FullName)"
    }
    else {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
        $base = if ($Version -eq 'latest') { "https://github.com/$Repo/releases/latest/download" }
                else { "https://github.com/$Repo/releases/download/$Version" }
        Write-Step "Downloading Agmen ($Version) from github.com/$Repo"
        $zipFile = Join-Path $work $zipName
        try { Invoke-WebRequest -Uri "$base/$zipName" -OutFile $zipFile -UseBasicParsing }
        catch { throw "Download failed: $($_.Exception.Message) Check https://github.com/$Repo/releases for that release." }
        $zip = Get-Item -LiteralPath $zipFile

        # The release carries the zip's SHA-256. A release without one is installed unchecked.
        $expected = $null
        try { $expected = (Invoke-WebRequest -Uri "$base/$zipName.sha256" -UseBasicParsing).Content }
        catch { Write-Warning 'This release has no checksum file, so the download was not verified.' }
        if ($expected) {
            if ($expected -is [byte[]]) { $expected = [Text.Encoding]::ASCII.GetString($expected) }
            $expected = ($expected.Trim() -split '\s+')[0].ToLowerInvariant()
            $actual = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $expected) { throw "The download's SHA-256 is $actual, but the release says $expected. Nothing was installed." }
            Write-Step 'Checksum matches the release'
        }
    }

    Write-Step "Extracting $($zip.Name)"
    $extract = Join-Path $work 'extracted'
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $extract -Force
    $newExe = Get-ChildItem -LiteralPath $extract -Filter 'Agmen.exe' -Recurse | Select-Object -First 1
    if (-not $newExe) { throw 'The zip does not contain Agmen.exe.' }
    if (-not (Test-Path (Join-Path $newExe.DirectoryName 'x64\OpenConsole.exe'))) {
        throw 'The zip is missing x64\OpenConsole.exe, so terminal input would misbehave. Rebuild the release.'
    }

    Stop-RunningCopy
    Write-Step "Installing to $InstallDir"
    if (Test-Path $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $newExe.DirectoryName '*') -Destination $InstallDir -Recurse -Force
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- shortcuts and launch

if (-not $NoShortcuts) {
    # From the Start menu, Agmen opens its workspace. At sign-in it starts quietly in the tray.
    New-Shortcut $startMenuLink $exe '--workspace'
    Write-Step 'Added a Start menu shortcut'
    if (-not $NoStartup) {
        New-Shortcut $startupLink $exe
        Write-Step 'Agmen will start when you sign in to Windows'
    }
}

$installed = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
Write-Host ''
Write-Host "Agmen $installed is installed in $InstallDir" -ForegroundColor Green

# Agmen runs the agent CLIs it finds on PATH. Say which ones it will find.
$agents = [ordered]@{
    'Claude Code' = @{ File = 'claude.exe'; Hint = 'https://claude.com/claude-code' }
    'Codex'       = @{ File = 'codex.cmd';  Hint = 'npm install -g @openai/codex' }
    'Kimi Code'   = @{ File = 'kimi.exe';   Hint = 'https://moonshotai.github.io/kimi-code/' }
    'Antigravity' = @{ File = 'agy.exe';    Hint = 'https://antigravity.google/docs/getting-started/' }
}
$path = @([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User')) -join ';'
$dirs = $path.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_.Trim().Trim('"')) }
$found = 0
Write-Host ''
Write-Host 'Agent CLIs on your PATH:'
foreach ($name in $agents.Keys) {
    $file = $agents[$name].File
    $hit = $dirs | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ $file) -ErrorAction SilentlyContinue) } | Select-Object -First 1
    if ($hit) {
        $found++
        Write-Host ("  [x] {0,-12} {1}" -f $name, (Join-Path $hit $file))
    }
    else { Write-Host ("  [ ] {0,-12} not found. Install: {1}" -f $name, $agents[$name].Hint) -ForegroundColor DarkGray }
}
if ($found -eq 0) {
    Write-Warning 'No agent CLI was found. Install at least one, sign in to it, and then start a session in Agmen.'
}

if (-not $NoLaunch) {
    Start-Process -FilePath $exe -ArgumentList '--workspace'
    Write-Host ''
    Write-Host 'Agmen is running: its workspace is open, and its icon is in the tray. Ctrl+Alt+W shows the workspace again.'
}
