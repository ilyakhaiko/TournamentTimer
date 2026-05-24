param(
    [string]$RunId = "local-test-run",
    [string]$SourceAssetsDir = "",
    [string]$AslHostRoot = "",
    [string]$DestinationAssetsDir = "",
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-RepoRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Resolve-PathOrFullName([string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $null
    }

    if (Test-Path -LiteralPath $PathValue) {
        return (Resolve-Path -LiteralPath $PathValue).Path
    }

    return [System.IO.Path]::GetFullPath($PathValue)
}

function Assert-AssetFile(
    [string]$AssetsDir,
    [string]$FileName
) {
    $path = Join-Path $AssetsDir $FileName

    if (-not (Test-Path -LiteralPath $path)) {
        throw "$FileName not found in assets dir: $AssetsDir"
    }
}

$repoRoot = Get-RepoRoot
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($SourceAssetsDir)) {
    $SourceAssetsDir = Join-Path $repoRoot "TournamentTimer.Server\server-runs\assets\$RunId"
}

if ([string]::IsNullOrWhiteSpace($AslHostRoot)) {
    $AslHostRoot = Join-Path $repoRoot "artifacts\asl-host"
}

if ([string]::IsNullOrWhiteSpace($DestinationAssetsDir)) {
    $DestinationAssetsDir = Join-Path $AslHostRoot "runs\$RunId"
}

$sourceFullPath = Resolve-PathOrFullName $SourceAssetsDir
$destinationFullPath = Resolve-PathOrFullName $DestinationAssetsDir

if (-not (Test-Path -LiteralPath $sourceFullPath)) {
    throw "Source assets dir not found: $sourceFullPath"
}

Assert-AssetFile -AssetsDir $sourceFullPath -FileName "autosplitter.asl"
Assert-AssetFile -AssetsDir $sourceFullPath -FileName "splits.lss"

if ((Test-Path -LiteralPath $destinationFullPath) -and -not $NoClean) {
    Remove-Item -LiteralPath $destinationFullPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $destinationFullPath | Out-Null

Get-ChildItem -LiteralPath $sourceFullPath -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $destinationFullPath -Recurse -Force
}

$settingsPath = Join-Path $destinationFullPath "asl-settings.json"
$hasSettings = Test-Path -LiteralPath $settingsPath

Write-Host "ASL Host assets synced."
Write-Host "RunId:       $RunId"
Write-Host "Source:      $sourceFullPath"
Write-Host "Destination: $destinationFullPath"
Write-Host "Settings:    $(if ($hasSettings) { 'yes' } else { 'no' })"
Write-Host ""
Write-Host "Run x86:       powershell -ExecutionPolicy Bypass -File .\scripts\start-asl-host-x86.ps1 -RunId $RunId"
Write-Host "Run x64:       powershell -ExecutionPolicy Bypass -File .\scripts\start-asl-host-x64.ps1 -RunId $RunId"
Write-Host "Configure x64: powershell -ExecutionPolicy Bypass -File .\scripts\start-asl-host-x64.ps1 -RunId $RunId -Configure"
