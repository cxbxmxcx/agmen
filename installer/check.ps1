<# Exercise the production setup in a disposable Windows user profile (the Windows CI runner). #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Setup, [Parameter(Mandatory)][string]$PublishedApp)
$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path -LiteralPath $Setup).ProviderPath
$PublishedApp = (Resolve-Path -LiteralPath $PublishedApp).ProviderPath
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Agmen_is1'
if (Test-Path -LiteralPath $key) { throw 'This installer check needs a profile without a registered Agmen installation.' }

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempRoot ('Agmen-setup-check-' + [guid]::NewGuid().ToString('N'))
$destination = Join-Path $testRoot 'program'
$installedExe = Join-Path $destination 'Agmen.exe'
$sentinel = Join-Path $destination 'existing-user-file.txt'
New-Item -ItemType Directory -Path $destination | Out-Null
Set-Content -LiteralPath $installedExe -Value 'legacy install placeholder'
Set-Content -LiteralPath $sentinel -Value 'preserve'
$options = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS="!startup"', ('/DIR="' + $destination + '"'))

function Invoke-Install([string]$Action, [bool]$ExpectSuccess) {
    $log = Join-Path $testRoot "$Action.log"
    $result = Start-Process -FilePath $Setup -ArgumentList ($options + ('/LOG="' + $log + '"')) -WindowStyle Hidden -PassThru -Wait
    if (($result.ExitCode -eq 0) -ne $ExpectSuccess) { throw "$Action returned $($result.ExitCode). See $log." }
}

Invoke-Install 'install' $true
$registration = Get-ItemProperty -LiteralPath $key
if ($registration.InstallLocation.TrimEnd('\') -ne $destination -or $registration.DisplayName -notlike 'Agmen*') {
    throw 'The per-user uninstall registration does not describe this installation.'
}
foreach ($published in Get-ChildItem -LiteralPath $PublishedApp -File -Recurse) {
    $relative = $published.FullName.Substring($PublishedApp.TrimEnd('\').Length + 1)
    $installed = Join-Path $destination $relative
    if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $published.FullName).Hash) {
        throw "Installed payload differs: $relative"
    }
}
$start = New-Object Diagnostics.ProcessStartInfo $installedExe, '--mcp'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.EnvironmentVariables['AGMEN_INSTANCE'] = 'Agmen-installer-check'
$hosted = [Diagnostics.Process]::Start($start)
try {
    Invoke-Install 'upgrade-while-open' $false
    if ($hosted.HasExited) { throw 'Setup interrupted a running process.' }
} finally {
    $hosted.StandardInput.Close()
    if (-not $hosted.WaitForExit(10000)) { throw 'The MCP test process did not exit after stdin closed.' }
    $hosted.Dispose()
}
Invoke-Install 'upgrade' $true
$uninstaller = $registration.UninstallString.Trim('"')
if ((Split-Path $uninstaller -Parent) -ne $destination) { throw 'Unexpected uninstall target.' }
$installerScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'install.ps1'
# The custom native installation above must remain the target of the scripting entry point.
$wrongTarget = Join-Path $testRoot 'other'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installerScript -InstallDir $wrongTarget -NoLaunch -NoShortcuts
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $wrongTarget)) { throw 'The script accepted a conflicting native install directory.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File $installerScript -Uninstall -NoShortcuts
if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $installedExe) -or (Test-Path -LiteralPath $key) -or
    (Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'preserve') { throw 'Uninstall did not remove only the installed files.' }

# Exercise portable ownership with a small synthetic package and an existing unrelated file.
$payload = Join-Path $testRoot 'portable-source\Agmen'
New-Item -ItemType Directory -Path (Join-Path $payload 'x64') -Force | Out-Null
foreach ($name in 'Agmen.exe', 'x64\OpenConsole.exe', 'new-helper.dll', 'guide.txt') {
    Set-Content -LiteralPath (Join-Path $payload $name) -Value 'payload'
}
$archive = Join-Path $testRoot 'portable.zip'
Compress-Archive -LiteralPath $payload -DestinationPath $archive
Set-Content -LiteralPath $installedExe -Value 'legacy'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installerScript -InstallDir $destination -Uninstall -NoShortcuts
if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $installedExe) -or !(Test-Path -LiteralPath $sentinel)) { throw 'Legacy removal lost an unrelated file.' }
Set-Content -LiteralPath $installedExe -Value 'legacy'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installerScript -InstallDir $destination -ZipPath $archive -NoLaunch -NoShortcuts
if ($LASTEXITCODE -ne 0) { throw 'Portable install failed.' }
$guide = Join-Path $destination 'guide.txt'
Set-Content -LiteralPath $guide -Value 'edited locally'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installerScript -InstallDir $destination -Uninstall -NoShortcuts
if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath (Join-Path $destination 'new-helper.dll')) -or
    (Get-Content -LiteralPath $guide -Raw).Trim() -ne 'edited locally' -or !(Test-Path -LiteralPath $sentinel)) {
    throw 'Manifest-based portable removal did not preserve unrelated and modified files.'
}
$checkedRoot = (Resolve-Path -LiteralPath $testRoot).ProviderPath
if ((Split-Path $checkedRoot -Parent).TrimEnd('\') -ne $tempRoot.TrimEnd('\') -or
    (Split-Path $checkedRoot -Leaf) -notmatch '^Agmen-setup-check-[0-9a-f]{32}$') { throw 'Unexpected test directory.' }
Remove-Item -LiteralPath $checkedRoot -Recurse -Force
Write-Host 'Passed native and portable installation, process protection, update and uninstall checks.'
