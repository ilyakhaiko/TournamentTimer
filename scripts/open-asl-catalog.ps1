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

$catalogExe = Join-Path $root "asl-catalog\TournamentTimer.AslCatalog.exe"
$runsRoot = Join-Path $root "server-runs\assets"

if (!(Test-Path $catalogExe)) {
    Write-Host "ERROR: ASL Catalog executable not found."
    Write-Host "Expected path: $catalogExe"
    Write-Host ""
    Write-Host "Use the server-admin or full package. ASL Catalog is not included in runner-only packages."
    Exit-WithPause 1
}

New-Item -ItemType Directory -Force $runsRoot | Out-Null

Write-Host "Opening ASL Catalog"
Write-Host "Runs root: $runsRoot"

$argument = '--runsRoot="' + $runsRoot + '"'
Start-Process -FilePath $catalogExe -ArgumentList $argument -WorkingDirectory (Split-Path -Parent $catalogExe)
