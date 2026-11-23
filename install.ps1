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
$managedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Agmen_is1'
$registered = Get-ItemProperty -LiteralPath $managedKey -ErrorAction SilentlyContinue

function Resolve-InstallDirectory([string]$Requested, [bool]$Explicit, [string]$RegisteredDir, [bool]$PortableOperation) {
    $requestedPath = [IO.Path]::GetFullPath($Requested)
    if (-not $RegisteredDir) { return $requestedPath }
    if (-not [IO.Path]::IsPathRooted($RegisteredDir)) { throw 'The registered Agmen install location is invalid.' }
    $registeredPath = [IO.Path]::GetFullPath($RegisteredDir)
    if (-not $Explicit) { return $registeredPath }
    if (-not $PortableOperation -and $requestedPath.TrimEnd('\') -ine $registeredPath.TrimEnd('\')) {
        throw "Agmen is registered in $registeredPath. Omit -InstallDir to update it there. Use -ZipPath for a separate portable copy."
    }
    return $requestedPath
}

$InstallDir = Resolve-InstallDirectory $InstallDir ($PSBoundParameters.ContainsKey('InstallDir')) $registered.InstallLocation ([bool]($ZipPath -or $Uninstall))
$exe = Join-Path $InstallDir 'Agmen.exe'
$manifestPath = Join-Path $InstallDir '.agmen-portable-files.json'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Agmen.lnk'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Agmen.lnk'

# Refuse to delete a folder that does not look like an Agmen install.
function Assert-SafeInstallDir {
    if (-not (Test-Path $InstallDir)) { return }
    $root = Get-Item -LiteralPath $InstallDir -Force
    if (-not $root.PSIsContainer -or $root.Parent -eq $null -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Choose a regular directory for the installation: $InstallDir"
    }
    $hasContent = @(Get-ChildItem -LiteralPath $InstallDir -Force).Count -gt 0
    if ($hasContent -and -not (Test-Path $exe)) {
        throw "$InstallDir already exists and does not contain Agmen.exe. Choose another -InstallDir."
    }
}

# Legacy zip builds shipped these binaries before the PowerShell installer recorded ownership.
# Their notices have generic names, so leave those in place when the old install has no manifest.
function Get-PortableFiles {
    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.Format -ne 'AgmenPortable/1' -or -not $manifest.Files) { throw 'Invalid Agmen portable install manifest.' }
        return $manifest.Files
    }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return }
    foreach ($name in 'Agmen.exe', 'conpty.dll', 'Microsoft.Terminal.Control.dll', 'D3DCompiler_47_cor3.dll',
        'PenImc_cor3.dll', 'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll', 'x64\OpenConsole.exe') {
        if (Test-Path -LiteralPath (Join-Path $InstallDir $name) -PathType Leaf) { [pscustomobject]@{Path=$name; SHA256=$null} }
    }
}

function Resolve-PayloadFile([string]$Relative) {
    $full = $InstallDir
    foreach ($part in ($Relative -split '[/\\]')) {
        if (-not $part -or $part -in '.', '..' -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $part -match '[ .]$') {
            throw "Invalid portable package path: $Relative"
        }
        $full = Join-Path $full $part
        $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Unexpected link in the install: $full" }
    }
    return $full
}

function Remove-PortableFiles {
    $dirs = @{}
    $files = @(Get-PortableFiles)
    # Validate every manifest path before touching the first file.
    $targets = @(foreach ($file in $files) { Resolve-PayloadFile $file.Path })
    for ($i = 0; $i -lt $targets.Count; $i++) {
        $target = $targets[$i]
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        if ($files[$i].SHA256 -and (Get-FileHash -LiteralPath $target).Hash -ne $files[$i].SHA256) {
            Write-Host "Keeping modified file: $target"
            continue
        }
        Remove-Item -LiteralPath $target -Force
        for ($dir = Split-Path $target -Parent; $dir -and $dir.TrimEnd('\') -ine $InstallDir.TrimEnd('\'); $dir = Split-Path $dir -Parent) {
            $dirs[$dir] = $true
        }
    }
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { Remove-Item -LiteralPath $manifestPath -Force }
    $dirs[$InstallDir] = $true
    foreach ($dir in ($dirs.Keys | Sort-Object Length -Descending)) {
        if ([IO.Directory]::Exists($dir) -and [IO.Directory]::GetFileSystemEntries($dir).Length -eq 0) {
            [IO.Directory]::Delete($dir, $false)
        }
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

# Keep the native installer's record of the files it owns when this script is used for an update or removal.
$managedHere = $registered -and $registered.InstallLocation -and
    $registered.InstallLocation.TrimEnd('\') -ieq $InstallDir.TrimEnd('\')
if (-not $managedHere -and (Test-Path -LiteralPath $InstallDir -PathType Container) -and
    @(Get-ChildItem -LiteralPath $InstallDir -Filter 'unins*.dat' -File).Count -gt 0) {
    throw "The directory $InstallDir already has a native uninstaller. Use its own uninstaller or repair the Agmen registration."
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
    if ($managedHere) {
        $remove = $registered.UninstallString.Trim('"')
        if ((Split-Path $remove -Parent) -ine $InstallDir.TrimEnd('\') -or
            (Split-Path $remove -Leaf) -notmatch '^unins\d{3}\.exe$') { throw 'Unexpected registered Agmen uninstaller path.' }
        $removeOptions = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        if ($Force) { $removeOptions += '/KEEPAGENTCONFIG=1' }
        $result = Start-Process -FilePath $remove -ArgumentList $removeOptions -WindowStyle Hidden -Wait -PassThru
        if ($result.ExitCode -ne 0) { throw "Uninstallation failed ($($result.ExitCode))." }
        Write-Host "Agmen removed. Saved workspaces remain in $env:APPDATA." -ForegroundColor Green
        exit 0
    }
    foreach ($link in $startMenuLink, $startupLink) {
        if (Test-Path $link) {
            $target = (New-Object -ComObject WScript.Shell).CreateShortcut($link).TargetPath
            if ($target -ieq $exe) { Remove-Item $link -Force }
        }
    }
    Remove-PortableFiles
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
        if ($managedHere) { throw 'Run Agmen-Setup-x64.exe to update this installer-managed copy. Use a separate folder for a portable zip.' }
        $zip = Get-Item -LiteralPath $ZipPath
        if (Test-Path -LiteralPath ($zip.FullName + '.sha256') -PathType Leaf) {
            $expected = ((Get-Content -LiteralPath ($zip.FullName + '.sha256') -Raw).Trim() -split '\s+')[0]
            if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $zip.FullName).Hash -ne $expected) {
                throw 'The portable zip failed its checksum.'
            }
        }
        Write-Step "Installing from $($zip.FullName)"
    }
    else {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
        $base = if ($Version -eq 'latest') { "https://github.com/$Repo/releases/latest/download" }
                else { "https://github.com/$Repo/releases/download/$Version" }
        Write-Step "Downloading Agmen ($Version) from github.com/$Repo"
        # Modern releases use the same installer for scripted and interactive upgrades.
        $setupName = 'Agmen-Setup-x64.exe'
        $setupPath = Join-Path $work $setupName
        $haveSetup = $false
        try {
            Invoke-WebRequest -Uri "$base/$setupName" -OutFile $setupPath -UseBasicParsing
            $haveSetup = $true
        }
        catch {
            if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
            if ($managedHere) { throw 'This release predates the Windows installer and cannot upgrade this installed copy.' }
            # Old release tags only ship the portable zip.
        }
        if ($haveSetup) {
            $digest = (Invoke-WebRequest -Uri "$base/$setupName.sha256" -UseBasicParsing).Content
            if ($digest -is [byte[]]) { $digest = [Text.Encoding]::ASCII.GetString($digest) }
            $digest = ($digest.Trim() -split '\s+')[0]
            if ($digest -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash -ne $digest) {
                throw 'The setup executable failed its release checksum.'
            }
            Stop-RunningCopy
            $options = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/DIR="' + $InstallDir + '"'))
            if ($NoStartup -or $NoShortcuts) { $options += '/TASKS="!startup"' }
            if ($NoShortcuts) { $options += '/NOICONS' }
            $result = Start-Process -FilePath $setupPath -ArgumentList $options -WindowStyle Hidden -Wait -PassThru
            if ($result.ExitCode -ne 0) { throw "Agmen Setup failed ($($result.ExitCode))." }
            Write-Host "Agmen is installed in $InstallDir." -ForegroundColor Green
            if (-not $NoLaunch) { Start-Process -FilePath $exe -ArgumentList '--workspace' }
            return
        }
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
    # Keep unrelated files in a legacy installation during an update.
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $owned = @{}
    foreach ($file in @(Get-PortableFiles)) { $owned[$file.Path] = $file }
    foreach ($source in Get-ChildItem -LiteralPath $newExe.DirectoryName -File -Recurse -Force) {
        $relative = $source.FullName.Substring($newExe.DirectoryName.Length + 1)
        if ($relative -ieq '.agmen-portable-files.json') { throw 'The package contains a reserved installer filename.' }
        $target = Resolve-PayloadFile $relative
        [void][IO.Directory]::CreateDirectory((Split-Path $target -Parent))
        Copy-Item -LiteralPath $source.FullName -Destination $target -Force
        $owned[$relative] = [pscustomobject]@{ Path = $relative; SHA256 = (Get-FileHash -LiteralPath $source.FullName).Hash }
    }
    $manifest = @{ Format='AgmenPortable/1'; Files=@($owned.Values) } | ConvertTo-Json -Depth 3
    $stagedManifest = Join-Path $InstallDir ('.agmen-manifest-' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($stagedManifest, $manifest)
    if (Test-Path -LiteralPath $manifestPath) { [IO.File]::Replace($stagedManifest, $manifestPath, [NullString]::Value) }
    else { [IO.File]::Move($stagedManifest, $manifestPath) }
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
