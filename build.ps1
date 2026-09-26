<#
.SYNOPSIS
  Builds and tests LinearTimecode in Debug and/or Release.
.EXAMPLE
  ./build.ps1                         # Debug + Release, library, CLI, tests, MAUI app (Windows)
  ./build.ps1 -Configuration Release  # one configuration
  ./build.ps1 -SkipApp                # skip the MAUI app
#>
param(
    [ValidateSet('Debug', 'Release', 'Both')]
    [string]$Configuration = 'Both',
    [switch]$SkipApp,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$configs = if ($Configuration -eq 'Both') { @('Debug', 'Release') } else { @($Configuration) }

function Run([string]$what, [string[]]$cmd) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & dotnet @cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

foreach ($c in $configs) {
    Run "Build library + CLI ($c)" @('build', 'tools/LinearTimecode.Cli', '-c', $c, '--nologo')
    if (-not $SkipTests) { Run "Test ($c)" @('test', 'tests/LinearTimecode.Tests', '-c', $c, '--nologo') }
    if (-not $SkipApp) {
        if ($IsWindows -or $env:OS -eq 'Windows_NT') {
            Run "Build LtcExplorer ($c)" @('build', 'samples/LtcExplorer', '-c', $c, '-f', 'net10.0-windows10.0.19041.0', '--nologo')
        } else {
            Write-Host "Skipping LtcExplorer (Windows target only in this script)." -ForegroundColor Yellow
        }
    }
}
Write-Host "Done: $($configs -join ', ')" -ForegroundColor Green
