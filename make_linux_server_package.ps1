param(
    [string]$RunId = "local-test-run",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = $scriptDir

if (!(Test-Path (Join-Path $root "TournamentTimer.Server\TournamentTimer.Server.csproj"))) {
    $root = (Get-Location).Path
}

$serverProject = Join-Path $root "TournamentTimer.Server\TournamentTimer.Server.csproj"

if (!(Test-Path $serverProject)) {
    throw "Server project not found: $serverProject"
}

$artifactsRoot = Join-Path $root "artifacts"
$publishDir = Join-Path $artifactsRoot "publish\server\linux-x64"
$packageDir = Join-Path $artifactsRoot "packages\server-admin-linux-x64"
$zipPath = Join-Path $artifactsRoot "TournamentTimer-server-admin-linux-x64.zip"

Write-Host "Publishing TournamentTimer.Server for linux-x64..."
dotnet publish $serverProject -c $Configuration -r linux-x64 --self-contained true -o $publishDir

Write-Host "Preparing linux server/admin package..."
Remove-Item $packageDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force (Join-Path $packageDir "server") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $packageDir "configs") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $packageDir "scripts") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $packageDir "server-runs\assets") | Out-Null

Copy-Item (Join-Path $publishDir "*") (Join-Path $packageDir "server") -Recurse -Force
Remove-Item (Join-Path $packageDir "server\server-runs") -Recurse -Force -ErrorAction SilentlyContinue

function Copy-DirectoryContentsIfExists {
    param(
        [Parameter(Mandatory=$true)][string]$SourceDir,
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    if (!(Test-Path $SourceDir)) {
        return
    }

    New-Item -ItemType Directory -Force $DestinationDir | Out-Null

    $items = Get-ChildItem -LiteralPath $SourceDir -Force -ErrorAction SilentlyContinue

    foreach ($item in $items) {
        Copy-Item $item.FullName $DestinationDir -Recurse -Force
    }
}

function Copy-FileIfExists {
    param(
        [Parameter(Mandatory=$true)][string]$Source,
        [Parameter(Mandatory=$true)][string]$Destination
    )

    if (Test-Path $Source) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $Destination) | Out-Null
        Copy-Item $Source $Destination -Force
    }
    else {
        Write-Warning "File not found: $Source"
    }
}

function Copy-CommonLegalDocuments {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    $names = @(
        "README.md",
        "LICENSE.txt",
        "NOTICE.txt",
        "README_USAGE_TERMS_RU.md",
        "AUTHORS.md"
    )

    foreach ($name in $names) {
        Copy-FileIfExists `
            -Source (Join-Path $root $name) `
            -Destination (Join-Path $DestinationDir $name)
    }
}

Copy-DirectoryContentsIfExists `
    -SourceDir (Join-Path $root "configs") `
    -DestinationDir (Join-Path $packageDir "configs")

Copy-FileIfExists `
    -Source (Join-Path $root "timer-settings.example.json") `
    -Destination (Join-Path $packageDir "timer-settings.example.json")

if (Test-Path (Join-Path $root "timer-settings.turn.example.json")) {
    Copy-Item `
        (Join-Path $root "timer-settings.turn.example.json") `
        (Join-Path $packageDir "timer-settings.turn.example.json") `
        -Force
}

$assetTarget = Join-Path $packageDir "server-runs\assets"
$assetSources = @(
    (Join-Path $root "server-runs\assets"),
    (Join-Path $root "TournamentTimer.Server\server-runs\assets")
)

foreach ($source in $assetSources) {
    Copy-DirectoryContentsIfExists -SourceDir $source -DestinationDir $assetTarget
}

$expectedConfig = Join-Path $packageDir "configs\$RunId.json"
if (!(Test-Path $expectedConfig)) {
    Write-Warning "Config for RunId '$RunId' was not found in package: configs\$RunId.json"
}

$expectedAssets = Join-Path $packageDir "server-runs\assets\$RunId"
if (!(Test-Path $expectedAssets)) {
    Write-Warning "Assets for RunId '$RunId' were not found in package: server-runs\assets\$RunId"
}

$startScript = @'
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVER_DIR="$ROOT_DIR/server"
SERVER_BIN="$SERVER_DIR/TournamentTimer.Server"

normalize_path_value() {
  local value="$1"
  value="${value//\\//}"
  echo "$value"
}

make_absolute_from_root() {
  local value
  value="$(normalize_path_value "$1")"

  if [ -z "$value" ]; then
    echo ""
  elif [[ "$value" = /* ]]; then
    echo "$value"
  else
    echo "$ROOT_DIR/$value"
  fi
}

SETTINGS_PATH_RAW="${1:-$ROOT_DIR/timer-settings.json}"
SETTINGS_PATH="$(make_absolute_from_root "$SETTINGS_PATH_RAW")"
URL_ARG="${2:-}"

if [ ! -f "$SERVER_BIN" ]; then
  echo "ERROR: server binary not found: $SERVER_BIN"
  exit 1
fi

if [ ! -f "$SETTINGS_PATH" ]; then
  echo "ERROR: settings file not found: $SETTINGS_PATH"
  echo "Copy timer-settings.example.json to timer-settings.json and edit it."
  exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "ERROR: python3 is required to read timer-settings.json."
  echo "Install it, for example: sudo apt install python3"
  exit 1
fi

chmod +x "$SERVER_BIN" 2>/dev/null || true

read_json_value() {
  local path="$1"
  local key="$2"

  python3 - "$path" "$key" <<'PY'
import json
import sys

path = sys.argv[1]
key = sys.argv[2]

try:
    with open(path, 'r', encoding='utf-8-sig') as f:
        data = json.load(f)
except FileNotFoundError:
    print('')
    raise SystemExit(0)

value = data.get(key)

if value is None:
    print('')
else:
    print(str(value))
PY
}

read_settings_value() {
  read_json_value "$SETTINGS_PATH" "$1"
}

RUN_ID="$(read_settings_value runId)"
RUN_CONFIG_PATH="$(read_settings_value runConfigPath)"
SERVER_RUNS_ROOT="$(read_settings_value serverRunsRoot)"
RUN_ASSETS_ROOT="$(read_settings_value runAssetsRoot)"
SETTINGS_SERVER_URL="$(read_settings_value serverUrl)"

if [ -z "$RUN_ID" ]; then
  RUN_ID="local-test-run"
fi

if [ -z "$RUN_CONFIG_PATH" ]; then
  RUN_CONFIG_PATH="configs/$RUN_ID.json"
fi

if [ -z "$SERVER_RUNS_ROOT" ]; then
  SERVER_RUNS_ROOT="server-runs"
fi

if [ -n "$URL_ARG" ]; then
  URL="$URL_ARG"
elif [ -n "$SETTINGS_SERVER_URL" ]; then
  URL="$SETTINGS_SERVER_URL"
else
  URL="http://0.0.0.0:5177"
fi

RUN_CONFIG_PATH_RAW="$RUN_CONFIG_PATH"
RUN_CONFIG_PATH="$(make_absolute_from_root "$RUN_CONFIG_PATH")"
SERVER_RUNS_ROOT="$(make_absolute_from_root "$SERVER_RUNS_ROOT")"

if [ -z "$RUN_ASSETS_ROOT" ]; then
  RUN_ASSETS_ROOT="$SERVER_RUNS_ROOT/assets"
else
  RUN_ASSETS_ROOT="$(make_absolute_from_root "$RUN_ASSETS_ROOT")"
fi

if [ ! -f "$RUN_CONFIG_PATH" ]; then
  echo "ERROR: run config file not found."
  echo "Selected RunId:                $RUN_ID"
  echo "Config path from settings/args: $RUN_CONFIG_PATH_RAW"
  echo "Resolved config path:          $RUN_CONFIG_PATH"
  echo "Settings path:                 $SETTINGS_PATH"
  echo ""
  echo "On Linux use forward slashes in runConfigPath, for example: configs/$RUN_ID.json"
  exit 1
fi

CONFIG_RUN_ID="$(read_json_value "$RUN_CONFIG_PATH" runId)"

if [ -z "$CONFIG_RUN_ID" ]; then
  echo "ERROR: run config has no runId."
  echo "Selected RunId: $RUN_ID"
  echo "Config path:    $RUN_CONFIG_PATH"
  echo "Settings path:  $SETTINGS_PATH"
  exit 1
fi

if [ "$CONFIG_RUN_ID" != "$RUN_ID" ]; then
  echo "ERROR: runId mismatch."
  echo "timer-settings / selected RunId: $RUN_ID"
  echo "Config file runId:              $CONFIG_RUN_ID"
  echo "Config path:                    $RUN_CONFIG_PATH"
  echo "Settings path:                  $SETTINGS_PATH"
  echo ""
  echo "These values must match."
  exit 1
fi

echo "Starting TournamentTimer.Server"
echo "Root:     $ROOT_DIR"
echo "Settings: $SETTINGS_PATH"
echo "RunId:    $RUN_ID"
echo "Config:   $RUN_CONFIG_PATH"
echo "Runs:     $SERVER_RUNS_ROOT"
echo "Assets:   $RUN_ASSETS_ROOT"
echo "URL:      $URL"
echo ""

cd "$SERVER_DIR"
exec "$SERVER_BIN" \
  --urls "$URL" \
  --SettingsPath="$SETTINGS_PATH" \
  --RunConfigPath="$RUN_CONFIG_PATH" \
  --ServerRunsRoot="$SERVER_RUNS_ROOT" \
  --RunAssetsRoot="$RUN_ASSETS_ROOT"
'@
Set-Content -LiteralPath (Join-Path $packageDir "scripts\start-server-linux.sh") -Value $startScript -Encoding UTF8

$service = @'
[Unit]
Description=TournamentTimer Server
After=network.target

[Service]
WorkingDirectory=/opt/tournamenttimer
ExecStart=/opt/tournamenttimer/scripts/start-server-linux.sh /opt/tournamenttimer/timer-settings.json
Restart=always
RestartSec=3
User=tournamenttimer

[Install]
WantedBy=multi-user.target
'@

Set-Content -LiteralPath (Join-Path $packageDir "scripts\tournamenttimer.service.example") -Value $service -Encoding UTF8

$readmes = @(
    "README_ADMIN.md",
    "README_CAMERA_TURN.md",
    "README_ASL_HOST.md",
    "README_RUNNER.md",
    "README_LIVESPLIT_BRIDGE.md"
)

foreach ($readme in $readmes) {
    $source = Join-Path $root $readme

    if (Test-Path $source) {
        Copy-Item $source (Join-Path $packageDir $readme) -Force
    }
    else {
        Write-Warning "README not found: $readme"
    }
}

Copy-CommonLegalDocuments $packageDir

if (!(Test-Path (Join-Path $packageDir "server-runs\assets"))) {
    throw "Package validation failed. Missing path: server-runs\assets"
}

if (Test-Path (Join-Path $packageDir "server\server-runs")) {
    throw "Package validation failed. Unexpected path: server\server-runs"
}

$requiredPackageFiles = @(
    "README.md",
    "README_ADMIN.md",
    "README_CAMERA_TURN.md",
    "README_ASL_HOST.md",
    "LICENSE.txt",
    "NOTICE.txt",
    "README_USAGE_TERMS_RU.md",
    "AUTHORS.md"
)

foreach ($relativePath in $requiredPackageFiles) {
    if (!(Test-Path (Join-Path $packageDir $relativePath))) {
        throw "Package validation failed. Missing file: $relativePath"
    }
}

$screenshotsSource = Join-Path $PSScriptRoot "docs\screenshots"
$screenshotsTarget = Join-Path $PSScriptRoot "artifacts\packages\server-admin-linux-x64\docs\screenshots"

if (Test-Path $screenshotsSource) {
    New-Item -ItemType Directory -Force $screenshotsTarget | Out-Null
    Copy-Item (Join-Path $screenshotsSource "*") $screenshotsTarget -Recurse -Force
}
Write-Host "Creating zip..."
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Linux server/admin package ready:"
Write-Host "  $zipPath"
Write-Host ""
Write-Host "Package contains server/admin/overlay/configs/assets only."
Write-Host "Runner UI, ASL Host and ASL Host Launcher are Windows-side packages."
Write-Host ""
Write-Host "Upload this zip to Linux server, then:"
Write-Host "  sudo mkdir -p /opt/tournamenttimer"
Write-Host "  sudo unzip TournamentTimer-server-admin-linux-x64.zip -d /opt/tournamenttimer"
Write-Host "  cd /opt/tournamenttimer"
Write-Host "  cp timer-settings.example.json timer-settings.json"
Write-Host "  nano timer-settings.json"
Write-Host "  chmod +x scripts/start-server-linux.sh"
Write-Host "  ./scripts/start-server-linux.sh"
