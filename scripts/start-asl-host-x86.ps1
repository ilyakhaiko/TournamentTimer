param(
    [string]$RunId = "local-test-run",
    [string]$AssetsDir = "",
    [string]$AslHostRoot = "",
    [string]$BridgeUrl = "http://127.0.0.1:52991/api/local/livesplit/events",
    [switch]$Configure,
    [switch]$Catalog,
    [switch]$Trace,
    [switch]$ManualKeys,
    [switch]$DebugMode,
    [int]$TickMs = 300,
    [int]$StatusEvery = 10
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path

if ([string]::IsNullOrWhiteSpace($AslHostRoot)) {
    $AslHostRoot = Join-Path $repoRoot "artifacts\asl-host"
}

$hostDir = Join-Path $AslHostRoot "x86"
$exePath = Join-Path $hostDir "TournamentTimer.AslHost.PoC.exe"

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Host "ASL Host x86 runtime was not found: $exePath" -ForegroundColor Yellow
    Write-Host "Build it first:" -ForegroundColor Yellow
    Write-Host "powershell -ExecutionPolicy Bypass -File .\scripts\build-asl-host.ps1" -ForegroundColor Yellow
    exit 1
}

if ($Catalog) {
    $runsRoot = [System.IO.Path]::GetFullPath((Join-Path $AslHostRoot "runs"))

    $hostArgs = @(
        "--catalog",
        "--runsRoot=$runsRoot",
        "--bridgeUrl=$BridgeUrl"
    )

    Write-Host "Starting ASL Host x86 catalog GUI"
    Write-Host "Host:     $exePath"
    Write-Host "RunsRoot: $runsRoot"
    Write-Host ""

    Push-Location $hostDir
    try {
        & $exePath @hostArgs
    }
    finally {
        Pop-Location
    }

    return
}

if ([string]::IsNullOrWhiteSpace($AssetsDir)) {
    $runtimeAssetsDir = Join-Path $AslHostRoot "runs\$RunId"
    $serverAssetsDir = Join-Path $repoRoot "TournamentTimer.Server\server-runs\assets\$RunId"

    if ((Test-Path -LiteralPath (Join-Path $runtimeAssetsDir "autosplitter.asl")) -and
        (Test-Path -LiteralPath (Join-Path $runtimeAssetsDir "splits.lss"))) {
        $AssetsDir = $runtimeAssetsDir
    }
    else {
        $AssetsDir = $serverAssetsDir
    }
}

$AssetsDir = [System.IO.Path]::GetFullPath($AssetsDir)

if (-not (Test-Path -LiteralPath (Join-Path $AssetsDir "autosplitter.asl"))) {
    throw "autosplitter.asl not found in assets dir: $AssetsDir"
}

if (-not (Test-Path -LiteralPath (Join-Path $AssetsDir "splits.lss"))) {
    throw "splits.lss not found in assets dir: $AssetsDir"
}

$hostArgs = @(
    "--assetsDir=$AssetsDir",
    "--bridgeUrl=$BridgeUrl",
    "--tickMs=$TickMs",
    "--statusEvery=$StatusEvery"
)

if ($Configure) { $hostArgs += "--configure" }
if ($Trace) { $hostArgs += "--trace" }
if ($ManualKeys) { $hostArgs += "--manualKeys" }
if ($DebugMode) { $hostArgs += "--debug" }

Write-Host "Starting ASL Host x86"
Write-Host "Host:   $exePath"
Write-Host "Assets: $AssetsDir"
Write-Host "Bridge: $BridgeUrl"
Write-Host ""

Push-Location $hostDir
try {
    & $exePath @hostArgs
}
finally {
    Pop-Location
}
