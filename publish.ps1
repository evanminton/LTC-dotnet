<#
.SYNOPSIS
  Publishes LTC Studio as a standalone Windows app (self-contained .NET + Windows App SDK, unpackaged),
  with the ltc CLI alongside, and zips it.

.DESCRIPTION
  Output (artifacts/ is git-ignored):
    artifacts/publish/<Configuration>/<Runtime>/LtcStudio     LtcStudio.exe + ltc.exe; copy anywhere and run
    artifacts/LtcStudio-<version>-<Runtime>[-debug].zip       the same folder, zipped (portable)
    artifacts/LtcStudio-<version>-<Runtime>[-debug]-setup.exe installer (Inno Setup 6; see -InstallInno)
    artifacts/publish.log                                     full log of the last run

  Nothing else needs to be installed on the target PC (Windows 10 1809 or later).

.EXAMPLE
  ./publish.ps1                          # Release, win-x64, zip + installer
  ./publish.ps1 -Configuration Debug
  ./publish.ps1 -Runtime win-arm64
  ./publish.ps1 -NoInstaller             # zip only
  ./publish.ps1 -InstallInno             # install Inno Setup via winget if it is missing
  ./publish.ps1 -Clean                   # wipe artifacts/ and the app's bin/ + obj/ first
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$Clean,
    [switch]$NoInstaller,
    [switch]$InstallInno
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not ($IsWindows -or $env:OS -eq 'Windows_NT')) { throw 'LTC Studio (WinUI) can only be published on Windows.' }

$version  = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1
$outRoot  = Join-Path $PSScriptRoot 'artifacts'
$pubRoot  = Join-Path $outRoot "publish/$Configuration/$Runtime"
$appOut   = Join-Path $pubRoot 'LtcStudio'
$cliOut   = Join-Path $pubRoot 'cli'
$suffix   = if ($Configuration -eq 'Debug') { '-debug' } else { '' }
$zip      = Join-Path $outRoot "LtcStudio-$version-$Runtime$suffix.zip"

function Run([string]$what, [string[]]$cmd) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & dotnet @cmd | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

if ($Clean) {
    Write-Host '==> Clean' -ForegroundColor Cyan
    & dotnet build-server shutdown | Out-Null
    Remove-Item $outRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item apps/LtcStudio/bin, apps/LtcStudio/obj -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force $outRoot | Out-Null
Start-Transcript -Path (Join-Path $outRoot 'publish.log') -Force | Out-Null
try {
    Remove-Item $pubRoot -Recurse -Force -ErrorAction SilentlyContinue

    # Self-contained, unpackaged, Windows App SDK bundled: see apps/LtcStudio/LtcStudio.csproj.
    Run "Publish LTC Studio ($Configuration, $Runtime)" @(
        'publish', 'apps/LtcStudio', '-c', $Configuration, '-r', $Runtime,
        '-p:PublishReadyToRun=false',
        '-o', $appOut, '--nologo')

    Run "Publish ltc CLI ($Configuration, $Runtime)" @(
        'publish', 'tools/LinearTimecode.Cli', '-c', $Configuration, '-r', $Runtime,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=embedded',
        '-o', $cliOut, '--nologo')

    Copy-Item (Join-Path $cliOut 'ltc.exe') $appOut -Force
    Copy-Item README.md, LICENSE $appOut -Force
    if (-not (Test-Path (Join-Path $appOut 'LtcStudio.exe'))) { throw 'LtcStudio.exe missing from publish output.' }

    Remove-Item $zip -ErrorAction SilentlyContinue
    Write-Host "==> Zip $zip" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $appOut '*') -DestinationPath $zip

    $size = (Get-ChildItem $appOut -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Zip:  {0}  ({1:0} MB unpacked)" -f $zip, $size) -ForegroundColor Green
    Write-Host "Run:  $(Join-Path $appOut 'LtcStudio.exe')"

    if ($NoInstaller) { return }

    # Installer (Inno Setup 6)
    function Find-Iscc {
        $c = Get-Command iscc.exe -ErrorAction SilentlyContinue
        if ($c) { return $c.Source }
        foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                         "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                         "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
            if ($p -and (Test-Path $p)) { return $p }
        }
    }
    $iscc = Find-Iscc
    if (-not $iscc -and $InstallInno) {
        Write-Host '==> Installing Inno Setup (winget)' -ForegroundColor Cyan
        winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements --scope user | Out-Host
        $iscc = Find-Iscc
    }
    if (-not $iscc) {
        Write-Host 'Inno Setup 6 not found - installer skipped. Run with -InstallInno, or: winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
        return
    }

    $arch  = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64compatible' }
    $setup = "LtcStudio-$version-$Runtime$suffix-setup"
    Write-Host "==> Installer ($iscc)" -ForegroundColor Cyan
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$appOut" "/DOutputDir=$outRoot" "/DOutputBase=$setup" "/DArch=$arch" 'installer/LtcStudio.iss' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($LASTEXITCODE)" }
    Write-Host "Setup: $(Join-Path $outRoot "$setup.exe")" -ForegroundColor Green
}
finally {
    Stop-Transcript | Out-Null
}
