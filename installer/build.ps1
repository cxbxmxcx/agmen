<# Build an offline per-user setup from an existing, verified publish directory. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDir,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$AppDir = (Resolve-Path -LiteralPath $AppDir).ProviderPath
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($file in 'Agmen.exe', 'conpty.dll', 'x64\OpenConsole.exe', 'Microsoft.Terminal.Control.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $AppDir $file) -PathType Leaf)) { throw "Missing published file: $file" }
}
if ($OutDir.StartsWith($AppDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $AppDir -eq $OutDir) {
    throw 'The setup output must be outside its input directory.'
}

if (-not $CompilerPath) {
    # The upstream portable distribution installs only in this output's build cache.
    $tools = Join-Path $OutDir '.build\inno-6.7.4'
    $CompilerPath = Join-Path $tools 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        $compilerInstaller = Join-Path $OutDir '.build\innosetup-6.7.4.exe'
        New-Item -ItemType Directory -Force -Path (Split-Path $compilerInstaller) | Out-Null
        $url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe'
        $sha = 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c'
        $ProgressPreference = 'SilentlyContinue'
        if (-not (Test-Path -LiteralPath $compilerInstaller -PathType Leaf)) {
            $download = "$compilerInstaller.download"
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $download
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $sha) { throw 'Inno Setup download checksum mismatch.' }
            Move-Item -LiteralPath $download -Destination $compilerInstaller
        }
        if ((Get-FileHash -LiteralPath $compilerInstaller -Algorithm SHA256).Hash -ne $sha) { throw 'Inno Setup compiler installer checksum mismatch.' }
        $install = Start-Process -FilePath $compilerInstaller -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
            '/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS',
            '/TASKS=""', ('/DIR="' + $tools + '"')
        )
        if ($install.ExitCode -ne 0) { throw "Inno Setup compiler extraction failed ($($install.ExitCode))." }
    }
}
$compiler = (Resolve-Path -LiteralPath $CompilerPath).ProviderPath
$version = (Get-Item -LiteralPath (Join-Path $AppDir 'Agmen.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Unsupported installer version: $version" }
& $compiler '/Qp' "/DAppSource=$AppDir" "/DAppVersion=$version" "/O$OutDir" (Join-Path $PSScriptRoot 'Agmen.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }
$setup = Get-Item -LiteralPath (Join-Path $OutDir 'Agmen-Setup-x64.exe')
$hash = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$($setup.FullName).sha256", "$hash  $($setup.Name)`n")
Write-Host "Agmen $version setup: $($setup.FullName) ($([math]::Round($setup.Length / 1MB)) MB)"
