param(
    [string]$SettingsPath = "timer-settings.json",
    [string]$RunId = "",
    [string]$RunConfigPath = "",
    [string]$ServerRunsRoot = "",
    [string]$RunAssetsRoot = "",
    [string]$Url = "",
    [string]$AdminKey = "",
    [string]$RunKey = "",
    [string]$ViewKey = "",
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
Set-Location $root

$runIdFromCli = $PSBoundParameters.ContainsKey("RunId") -and -not [string]::IsNullOrWhiteSpace($RunId)
$runConfigPathFromCli = $PSBoundParameters.ContainsKey("RunConfigPath") -and -not [string]::IsNullOrWhiteSpace($RunConfigPath)

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

function Exit-WithPause([int]$ExitCode) {
    if (-not $NoPause) {
        Write-Host ""
        [void](Read-Host "Press Enter to close")
    }

    exit $ExitCode
}

function Exit-WithError() {
    Exit-WithPause 1
}

function Exit-AfterProcess([int]$ExitCode, [string]$Label) {
    if ($ExitCode -ne 0) {
        Write-Host ""
        Write-Host "$Label exited with code $ExitCode."
        Exit-WithPause $ExitCode
    }

    exit $ExitCode
}

$settings = Read-TimerSettings $SettingsPath
$resolvedSettingsPath = Resolve-Path-FromRoot $SettingsPath

$RunId = Resolve-Setting $RunId $settings "runId" "local-test-run"
$Url = Resolve-Setting $Url $settings "serverUrl" "http://localhost:5177"

if ($runConfigPathFromCli) {
    $RunConfigPath = $RunConfigPath
}
elseif ($runIdFromCli) {
    # CLI -RunId means "use configs/<RunId>.json" unless -RunConfigPath is also explicit.
    # Do not reuse timer-settings runConfigPath here, because that commonly points to another run.
    $RunConfigPath = ""
}
else {
    $RunConfigPath = Resolve-Setting $RunConfigPath $settings "runConfigPath" ""
}

$ServerRunsRoot = Resolve-Setting $ServerRunsRoot $settings "serverRunsRoot" "server-runs"
$RunAssetsRoot = Resolve-Setting $RunAssetsRoot $settings "runAssetsRoot" ""
$AdminKey = Resolve-Setting $AdminKey $settings "adminKey" ""
$RunKey = Resolve-Setting $RunKey $settings "runKey" ""
$ViewKey = Resolve-Setting $ViewKey $settings "viewKey" ""

if ([string]::IsNullOrWhiteSpace($RunConfigPath)) {
    $RunConfigPath = "configs\$RunId.json"
}

$resolvedRunConfigPath = Resolve-Path-FromRoot $RunConfigPath
$resolvedServerRunsRoot = Resolve-Path-FromRoot $ServerRunsRoot

if ([string]::IsNullOrWhiteSpace($RunAssetsRoot)) {
    $resolvedRunAssetsRoot = Join-Path $resolvedServerRunsRoot "assets"
}
else {
    $resolvedRunAssetsRoot = Resolve-Path-FromRoot $RunAssetsRoot
}

if (-not (Test-Path $resolvedRunConfigPath)) {
    Write-Host "ERROR: run config file not found."
    Write-Host "Selected RunId:                $RunId"
    Write-Host "Config path from settings/args: $RunConfigPath"
    Write-Host "Resolved config path:          $resolvedRunConfigPath"
    Write-Host "Settings path:                 $resolvedSettingsPath"
    Write-Host ""
    Write-Host "Fix timer-settings.json runId/runConfigPath, pass -RunConfigPath explicitly,"
    Write-Host "or create configs\$RunId.json."
    Exit-WithError
}

try {
    $configJson = Get-Content -Raw -Path $resolvedRunConfigPath | ConvertFrom-Json
    $configRunId = [string]$configJson.runId

    if ([string]::IsNullOrWhiteSpace($configRunId)) {
        Write-Host "ERROR: run config has no runId."
        Write-Host "Selected RunId:       $RunId"
        Write-Host "Config path:          $resolvedRunConfigPath"
        Write-Host "Settings path:        $resolvedSettingsPath"
        Write-Host ""
        Write-Host "Add runId to the config file, or select another config with -RunConfigPath."
        Exit-WithError
    }

    if ($configRunId -ne $RunId) {
        Write-Host "ERROR: runId mismatch."
        Write-Host "timer-settings / selected RunId: $RunId"
        Write-Host "Config file runId:              $configRunId"
        Write-Host "Config path:                    $resolvedRunConfigPath"
        Write-Host "Settings path:                  $resolvedSettingsPath"
        Write-Host ""
        Write-Host "These values must match."
        Write-Host "Fix runId in timer-settings.json, fix runId inside the config file,"
        Write-Host "or pass the correct -RunConfigPath."
        Exit-WithError
    }
}
catch {
    Write-Host "ERROR: failed to read run config."
    Write-Host "Selected RunId: $RunId"
    Write-Host "Config path:    $resolvedRunConfigPath"
    Write-Host "Settings path:  $resolvedSettingsPath"
    Write-Host $_.Exception.Message
    Exit-WithError
}

Set-Or-Clear-Env "TOURNAMENT_TIMER_RUN_ID" $RunId
Set-Or-Clear-Env "TOURNAMENT_TIMER_ADMIN_KEY" $AdminKey
Set-Or-Clear-Env "TOURNAMENT_TIMER_RUN_KEY" $RunKey
Set-Or-Clear-Env "TOURNAMENT_TIMER_VIEW_KEY" $ViewKey
Set-Or-Clear-Env "TOURNAMENT_TIMER_RUN_ASSETS_ROOT" $resolvedRunAssetsRoot
Set-Or-Clear-Env "TOURNAMENT_TIMER_SERVER_RUNS_ROOT" $resolvedServerRunsRoot
Set-Or-Clear-Env "TOURNAMENT_TIMER_SETTINGS_PATH" $resolvedSettingsPath

Write-Host "Starting TournamentTimer.Server"
Write-Host "RunId:    $RunId"
Write-Host "Config:   $RunConfigPath"
Write-Host "Runs:     $ServerRunsRoot"

if ([string]::IsNullOrWhiteSpace($RunAssetsRoot)) {
    Write-Host "Assets:   $ServerRunsRoot\assets"
}
else {
    Write-Host "Assets:   $RunAssetsRoot"
}
Write-Host "URL:      $Url"
Write-Host "Settings: $SettingsPath"
Write-Host "Keys:     admin=$(-not [string]::IsNullOrWhiteSpace($env:TOURNAMENT_TIMER_ADMIN_KEY)) run=$(-not [string]::IsNullOrWhiteSpace($env:TOURNAMENT_TIMER_RUN_KEY)) view=$(-not [string]::IsNullOrWhiteSpace($env:TOURNAMENT_TIMER_VIEW_KEY))"
Write-Host ""

$serverExe = Join-Path $root "server\TournamentTimer.Server.exe"
$serverProject = Join-Path $root "TournamentTimer.Server\TournamentTimer.Server.csproj"

$serverArgs = @(
    "--urls", $Url,
    "--RunConfigPath=$RunConfigPath",
    "--SettingsPath=$resolvedSettingsPath",
    "--ServerRunsRoot=$resolvedServerRunsRoot",
    "--RunAssetsRoot=$resolvedRunAssetsRoot"
)

if (Test-Path $serverExe) {
    $serverDir = Split-Path -Parent $serverExe
    Push-Location $serverDir
    try {
        & $serverExe @serverArgs
        Exit-AfterProcess $LASTEXITCODE "TournamentTimer.Server.exe"
    }
    finally {
        Pop-Location
    }
}

if (Test-Path $serverProject) {
    $dotnetArgs = @("run", "--project", $serverProject) + $serverArgs
    dotnet @dotnetArgs
    Exit-AfterProcess $LASTEXITCODE "dotnet run"
}

Write-Host "ERROR: cannot find TournamentTimer server executable or project."
Write-Host "Expected packaged exe: $serverExe"
Write-Host "Expected source project: $serverProject"
Exit-WithError
