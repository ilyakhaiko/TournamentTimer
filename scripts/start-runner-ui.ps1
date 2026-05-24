param(
    [string]$SettingsPath = "timer-settings.json",
    [string]$RunId = "",
    [string]$RunnerId = "runner-1",
    [string]$Server = "",
    [string]$RunKey = "",
    [switch]$Build
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
Set-Location $root

function Resolve-Path-FromRoot([string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) { return $null }
    if ([System.IO.Path]::IsPathRooted($PathValue)) { return $PathValue }
    return (Join-Path $root $PathValue)
}

function Read-TimerSettings([string]$PathValue) {
    $resolvedPath = Resolve-Path-FromRoot $PathValue

    if ([string]::IsNullOrWhiteSpace($resolvedPath) -or -not (Test-Path $resolvedPath)) {
        return $null
    }

    try {
        return Get-Content -Raw -Path $resolvedPath | ConvertFrom-Json
    }
    catch {
        Write-Host "WARNING: failed to read settings file: $resolvedPath"
        Write-Host $_.Exception.Message
        return $null
    }
}

function Resolve-Setting([string]$CliValue, $Settings, [string]$Name, [string]$Fallback) {
    if (-not [string]::IsNullOrWhiteSpace($CliValue)) { return $CliValue }

    if ($null -ne $Settings -and $Settings.PSObject.Properties.Name -contains $Name) {
        $value = [string]$Settings.$Name
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    }

    return $Fallback
}

function Set-Or-Clear-Env([string]$Name, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        Remove-Item "Env:$Name" -ErrorAction SilentlyContinue
        return
    }

    Set-Item "Env:$Name" $Value
}

$settings = Read-TimerSettings $SettingsPath

$RunId = Resolve-Setting $RunId $settings "runId" "local-test-run"
$Server = Resolve-Setting $Server $settings "serverUrl" "http://localhost:5177"
$RunKey = Resolve-Setting $RunKey $settings "runKey" ""

Set-Or-Clear-Env "TOURNAMENT_TIMER_SERVER" $Server
Set-Or-Clear-Env "TOURNAMENT_TIMER_RUN_ID" $RunId
Set-Or-Clear-Env "TOURNAMENT_TIMER_RUN_KEY" $RunKey

$packagedExe = Join-Path $root "runner-ui\TournamentTimer.Runner.Wpf.exe"
$projectPath = Join-Path $root "TournamentTimer.Runner.Wpf\TournamentTimer.Runner.Wpf.csproj"
$debugExe = Join-Path $root "TournamentTimer.Runner.Wpf\bin\Debug\net10.0-windows\TournamentTimer.Runner.Wpf.exe"
$releaseExe = Join-Path $root "TournamentTimer.Runner.Wpf\bin\Release\net10.0-windows\TournamentTimer.Runner.Wpf.exe"

Write-Host "Starting TournamentTimer.Runner.Wpf"
Write-Host "RunId:    $RunId"
Write-Host "Runner:   $RunnerId"
Write-Host "Server:   $Server"
Write-Host "Settings: $SettingsPath"
Write-Host "RunKey:   $(-not [string]::IsNullOrWhiteSpace($env:TOURNAMENT_TIMER_RUN_KEY))"
Write-Host ""

$argsList = @(
    "--server=$Server",
    "--runId=$RunId",
    "--runnerId=$RunnerId"
)

if (-not [string]::IsNullOrWhiteSpace($RunKey)) {
    $argsList += "--runKey=$RunKey"
}

if (Test-Path $packagedExe) {
    & $packagedExe @argsList
    exit $LASTEXITCODE
}

if ($Build) {
    $runningRunnerUi = Get-Process TournamentTimer.Runner.Wpf -ErrorAction SilentlyContinue

    if ($runningRunnerUi) {
        Write-Host "ERROR: cannot build while TournamentTimer.Runner.Wpf is running."
        Write-Host "Close runner UI windows, or run:"
        Write-Host "Get-Process TournamentTimer.Runner.Wpf -ErrorAction SilentlyContinue | Stop-Process"
        exit 1
    }
}

if ($Build -or -not (Test-Path $debugExe)) {
    if (!(Test-Path $projectPath)) {
        Write-Host "ERROR: cannot find runner UI executable or project."
        Write-Host "Expected packaged exe: $packagedExe"
        Write-Host "Expected source project: $projectPath"
        exit 1
    }

    Write-Host "Building TournamentTimer.Runner.Wpf..."
    dotnet build $projectPath

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host ""
}

$exePath = if (Test-Path $debugExe) { $debugExe } elseif (Test-Path $releaseExe) { $releaseExe } else { $null }

if ($null -eq $exePath) {
    Write-Host "ERROR: runner UI executable not found after build."
    exit 1
}

& $exePath @argsList
exit $LASTEXITCODE
