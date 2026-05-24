param(
    [string]$SettingsPath = "timer-settings.json",
    [string]$RunId = "",
    [string]$Server = "",
    [string]$Url = "",
    [string]$RunnerId = "runner-1",
    [switch]$StartRunner
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

if ([string]::IsNullOrWhiteSpace($Server) -and -not [string]::IsNullOrWhiteSpace($Url)) {
    $Server = $Url
}

$settings = Read-TimerSettings $SettingsPath

$RunId = Resolve-Setting $RunId $settings "runId" "local-test-run"
$Server = Resolve-Setting $Server $settings "serverUrl" "http://localhost:5177"

Write-Host "Starting TournamentTimer local dev/package"
Write-Host "RunId:       $RunId"
Write-Host "Server URL:  $Server"
Write-Host "Settings:    $SettingsPath"
Write-Host ""

Write-Host "Opening server window..."
Start-Process powershell -ArgumentList @(
    "-NoExit",
    "-ExecutionPolicy", "Bypass",
    "-File", "$root\scripts\start-server.ps1",
    "-SettingsPath", $SettingsPath,
    "-RunId", $RunId,
    "-Url", $Server
)

Start-Sleep -Seconds 2

Write-Host "Opening admin..."
& powershell -ExecutionPolicy Bypass -File "$root\scripts\open-admin.ps1" `
    -SettingsPath $SettingsPath `
    -RunId $RunId `
    -Server $Server

if ($StartRunner) {
    Write-Host "Opening runner UI..."
    Start-Process powershell -ArgumentList @(
        "-NoExit",
        "-ExecutionPolicy", "Bypass",
        "-File", "$root\scripts\start-runner-ui.ps1",
        "-SettingsPath", $SettingsPath,
        "-RunId", $RunId,
        "-RunnerId", $RunnerId,
        "-Server", $Server
    )
}

Write-Host ""
Write-Host "Manual commands:"
Write-Host "Server:    powershell -ExecutionPolicy Bypass -File .\scripts\start-server.ps1 -SettingsPath `"$SettingsPath`" -RunId `"$RunId`" -Url `"$Server`""
Write-Host "Admin:     powershell -ExecutionPolicy Bypass -File .\scripts\open-admin.ps1 -SettingsPath `"$SettingsPath`" -RunId `"$RunId`" -Server `"$Server`""
Write-Host "Runner UI: powershell -ExecutionPolicy Bypass -File .\scripts\start-runner-ui.ps1 -SettingsPath `"$SettingsPath`" -RunId `"$RunId`" -RunnerId `"$RunnerId`" -Server `"$Server`""
