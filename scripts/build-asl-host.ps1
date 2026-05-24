param(
    [string]$Configuration = "Debug",
    [string]$ProjectPath = ".\tools\TournamentTimer.AslHost.PoC\TournamentTimer.AslHost.PoC.csproj",
    [string]$OutputRoot = "",
    [string]$AslHelpPath = ""
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

function Find-AslHelpPath(
    [string]$RepoRoot,
    [string]$ProjectDir,
    [string]$Configuration,
    [string]$ExplicitPath
) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $fullExplicitPath = Resolve-PathOrFullName $ExplicitPath

        if (Test-Path -LiteralPath $fullExplicitPath) {
            return $fullExplicitPath
        }

        throw "asl-help path was specified but not found: $fullExplicitPath"
    }

    $candidates = @(
        (Join-Path $RepoRoot "Components\asl-help"),
        (Join-Path $RepoRoot "external\Components\asl-help"),
        (Join-Path $RepoRoot "external\LiveSplit\Components\asl-help"),
        (Join-Path $RepoRoot "external\LiveSplit\components\asl-help"),
        (Join-Path $RepoRoot "external\LiveSplit.ScriptableAutoSplit\Components\asl-help"),
        (Join-Path $ProjectDir "bin\x86\$Configuration\net481\Components\asl-help"),
        (Join-Path $ProjectDir "bin\x64\$Configuration\net481\Components\asl-help"),
        (Join-Path $RepoRoot "TournamentTimer.Server\server-runs\assets\local-test-run\Components\asl-help")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Copy-AslHelp(
    [string]$SourcePath,
    [string]$DestinationHostDir
) {
    if ([string]::IsNullOrWhiteSpace($SourcePath)) {
        Write-Warning "Components/asl-help was not found. Helper-dependent ASLs may fail until it is copied next to the host exe."
        return
    }

    $componentsDir = Join-Path $DestinationHostDir "Components"
    New-Item -ItemType Directory -Force -Path $componentsDir | Out-Null

    $destinationPath = Join-Path $componentsDir "asl-help"

    if (Test-Path -LiteralPath $destinationPath) {
        Remove-Item -LiteralPath $destinationPath -Recurse -Force
    }

    if ((Get-Item -LiteralPath $SourcePath).PSIsContainer) {
        Copy-Item -LiteralPath $SourcePath -Destination $componentsDir -Recurse -Force
    }
    else {
        Copy-Item -LiteralPath $SourcePath -Destination $destinationPath -Force
    }

    Write-Host "Copied Components/asl-help -> $destinationPath"
}

function Build-AslHostPlatform(
    [string]$RepoRoot,
    [string]$ProjectFullPath,
    [string]$Configuration,
    [string]$Platform,
    [string]$OutputRootFullPath,
    [string]$AslHelpSourcePath
) {
    Write-Host ""
    Write-Host "Building ASL Host $Platform / $Configuration..."

    dotnet build $ProjectFullPath -c $Configuration -p:Platform=$Platform -p:PlatformTarget=$Platform

    $projectDir = Split-Path -Parent $ProjectFullPath
    $buildOutputDir = Join-Path $projectDir "bin\$Platform\$Configuration\net481"

    if (-not (Test-Path -LiteralPath $buildOutputDir)) {
        throw "Build output directory not found: $buildOutputDir"
    }

    $destinationDir = Join-Path $OutputRootFullPath $Platform

    if (Test-Path -LiteralPath $destinationDir) {
        Remove-Item -LiteralPath $destinationDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
    Copy-Item -Path (Join-Path $buildOutputDir "*") -Destination $destinationDir -Recurse -Force

    Copy-AslHelp -SourcePath $AslHelpSourcePath -DestinationHostDir $destinationDir

    Write-Host "ASL Host $Platform prepared: $destinationDir"
}

$repoRoot = Get-RepoRoot
Set-Location $repoRoot

$projectFullPath = Resolve-PathOrFullName $ProjectPath
if (-not (Test-Path -LiteralPath $projectFullPath)) {
    throw "ASL Host project not found: $projectFullPath"
}

$projectDir = Split-Path -Parent $projectFullPath

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\asl-host"
}

$outputRootFullPath = Resolve-PathOrFullName $OutputRoot
New-Item -ItemType Directory -Force -Path $outputRootFullPath | Out-Null

$aslHelpSourcePath = Find-AslHelpPath `
    -RepoRoot $repoRoot `
    -ProjectDir $projectDir `
    -Configuration $Configuration `
    -ExplicitPath $AslHelpPath

if ([string]::IsNullOrWhiteSpace($aslHelpSourcePath)) {
    Write-Warning "asl-help source was not found before build. If you already copied it only into bin after build, rerun this script with -AslHelpPath."
}
else {
    Write-Host "asl-help source: $aslHelpSourcePath"
}

Build-AslHostPlatform `
    -RepoRoot $repoRoot `
    -ProjectFullPath $projectFullPath `
    -Configuration $Configuration `
    -Platform "x86" `
    -OutputRootFullPath $outputRootFullPath `
    -AslHelpSourcePath $aslHelpSourcePath

Build-AslHostPlatform `
    -RepoRoot $repoRoot `
    -ProjectFullPath $projectFullPath `
    -Configuration $Configuration `
    -Platform "x64" `
    -OutputRootFullPath $outputRootFullPath `
    -AslHelpSourcePath $aslHelpSourcePath

$runsDir = Join-Path $outputRootFullPath "runs"
New-Item -ItemType Directory -Force -Path $runsDir | Out-Null

Write-Host ""
Write-Host "Done. Runtime root: $outputRootFullPath"
Write-Host "Run x86: powershell -ExecutionPolicy Bypass -File .\scripts\start-asl-host-x86.ps1 -RunId local-test-run"
Write-Host "Run x64: powershell -ExecutionPolicy Bypass -File .\scripts\start-asl-host-x64.ps1 -RunId local-test-run"
