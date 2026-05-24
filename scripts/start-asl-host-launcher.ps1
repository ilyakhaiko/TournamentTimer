param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

function Exit-WithPause([int]$ExitCode) {
    if (-not $NoPause) {
        Write-Host ""
        [void](Read-Host "Press Enter to close")
    }

    exit $ExitCode
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir

$launcherExe = Join-Path $root "asl-host-launcher\TournamentTimer.AslHost.Launcher.exe"
$assetsRoot = Join-Path $root "server-runs\assets"

if (!(Test-Path $launcherExe)) {
    Write-Host "ERROR: ASL Host Launcher executable not found."
    Write-Host "Expected path: $launcherExe"
    Write-Host ""
    Write-Host "Use the runner or full package. ASL Host Launcher is not included in server-admin-only packages."
    Exit-WithPause 1
}

New-Item -ItemType Directory -Force $assetsRoot | Out-Null

Write-Host "Opening ASL Host Launcher"
Write-Host "Assets root: $assetsRoot"

Start-Process -FilePath $launcherExe -WorkingDirectory (Split-Path -Parent $launcherExe)
