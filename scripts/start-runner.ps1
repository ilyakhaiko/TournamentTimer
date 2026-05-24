param(
    [string]$SettingsPath = "timer-settings.json",
    [string]$RunId = "",
    [string]$RunnerId = "runner-1",
    [string]$Server = "",
    [string]$RunKey = "",
    [string]$LogPath = ""
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
Set-Or-Clear-Env "TOURNAMENT_TIMER_RUN_KEY" $RunKey

Write-Host "Starting TournamentTimer.Runner"
Write-Host "RunId:    $RunId"
Write-Host "Runner:   $RunnerId"
Write-Host "Server:   $Server"
Write-Host "Settings: $SettingsPath"
Write-Host "RunKey:   $(-not [string]::IsNullOrWhiteSpace($env:TOURNAMENT_TIMER_RUN_KEY))"

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    Write-Host "Log:      auto (current server attempt)"
} else {
    Write-Host "Log:      $LogPath (manual override)"
}

Write-Host ""

$packagedExe = Join-Path $root "runner\TournamentTimer.Runner.exe"
$projectPath = Join-Path $root "TournamentTimer.Runner\TournamentTimer.Runner.csproj"

$argsList = @("--runId=$RunId", "--runnerId=$RunnerId")

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $argsList += "--logPath=$LogPath"
}

if (Test-Path $packagedExe) {
    & $packagedExe @argsList
    exit $LASTEXITCODE
}

if (Test-Path $projectPath) {
    dotnet run --project $projectPath -- @argsList
    exit $LASTEXITCODE
}

Write-Host "ERROR: console runner is not included in this package."
Write-Host "Use scripts\start-runner-ui.ps1 for the normal runner UI."
Write-Host "Expected source project: $projectPath"
exit 1
