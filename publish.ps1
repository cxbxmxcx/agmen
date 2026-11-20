<#
.SYNOPSIS
    Builds the self-contained Agmen release: a folder you can run, and the zip a release ships.

.DESCRIPTION
    Publishes Agmen for 64-bit Windows with the .NET runtime inside Agmen.exe, so the people who
    download it don't need .NET. The native terminal files stay beside the exe, where the
    terminal looks for them. The script then checks the build starts, and zips it.

    The Release workflow runs this same script, so a zip you build here matches a released one.

.EXAMPLE
    .\publish.ps1
    # out\Agmen\Agmen.exe and out\Agmen-win-x64.zip

.EXAMPLE
    .\publish.ps1 -OutDir C:\Temp\agmen-build -NoZip
#>
[CmdletBinding()]
param(
    # Where the build goes: <OutDir>\Agmen and <OutDir>\Agmen-win-x64.zip. Default: out, beside this script.
    [string]$OutDir,
    # Build the folder only.
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

$project = Join-Path $PSScriptRoot 'Agmen.csproj'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'out' }
$OutDir = [IO.Path]::GetFullPath($OutDir)
$app = Join-Path $OutDir 'Agmen'
$zip = Join-Path $OutDir 'Agmen-win-x64.zip'

if (Test-Path $app) { Remove-Item -LiteralPath $app -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

Write-Step 'Publishing'
# Its own build folders, so this never touches bin\ and obj\, which a running Debug copy may hold.
& dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
    --artifacts-path (Join-Path $OutDir '.build') -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Native debug symbols from the terminal package (about 80 MB) aren't needed to run.
Get-ChildItem -LiteralPath $app -Filter '*.pdb' | Remove-Item -Force
foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    $source = Join-Path $PSScriptRoot $doc
    if (Test-Path $source) { Copy-Item $source $app }
}

Write-Step 'Checking the build'
# Without OpenConsole.exe beside conpty.dll, Windows falls back to its older console host and
# Shift+Enter sends a message instead of adding a line.
foreach ($file in 'Agmen.exe', 'conpty.dll', 'x64\OpenConsole.exe', 'Microsoft.Terminal.Control.dll') {
    if (-not (Test-Path (Join-Path $app $file))) { throw "The build is missing $file." }
}

# The tool server needs no window, so it shows the exe starts and answers, also on a build machine.
$request = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"publish-check","version":"1"}}}'
$start = New-Object Diagnostics.ProcessStartInfo (Join-Path $app 'Agmen.exe'), '--mcp'
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.CreateNoWindow = $true
$start.EnvironmentVariables['AGMEN_INSTANCE'] = 'Agmen-publish-check'   # no running copy answers to this name
$server = [Diagnostics.Process]::Start($start)
$server.StandardInput.WriteLine($request)
$server.StandardInput.Close()
$reading = $server.StandardOutput.ReadToEndAsync()
if (-not $server.WaitForExit(30000)) {
    $server.Kill()
    throw "Agmen.exe --mcp didn't exit within 30 seconds."
}
$reply = $reading.Result
if ($reply -notmatch '"name":"agmen"') { throw "Agmen.exe --mcp didn't answer as the agmen tool server. It said: $reply" }

$version = (Get-Item (Join-Path $app 'Agmen.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$files = @(Get-ChildItem -LiteralPath $app -Recurse -File)
$size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Agmen $version in $app ($($files.Count) files, $size MB)" -ForegroundColor Green

if ($NoZip) { return }

Write-Step 'Zipping'
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $app -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  Agmen-win-x64.zip`n")
Write-Host "$zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)" -ForegroundColor Green
Write-Host "SHA-256 $hash"
