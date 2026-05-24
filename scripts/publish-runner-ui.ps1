param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$projectPath = Join-Path $root "TournamentTimer.Runner.Wpf\TournamentTimer.Runner.Wpf.csproj"
$outputPath = Join-Path $root "artifacts\runner-ui\$Runtime"

Write-Host "Publishing TournamentTimer.Runner.Wpf"
Write-Host "Configuration:  $Configuration"
Write-Host "Runtime:        $Runtime"
Write-Host "Self-contained: $SelfContained"
Write-Host "Output:         $outputPath"
Write-Host ""

$runningRunnerUi = Get-Process TournamentTimer.Runner.Wpf -ErrorAction SilentlyContinue

if ($runningRunnerUi) {
    Write-Host "ERROR: cannot publish while TournamentTimer.Runner.Wpf is running."
    Write-Host "Close runner UI windows, or run:"
    Write-Host "Get-Process TournamentTimer.Runner.Wpf -ErrorAction SilentlyContinue | Stop-Process"
    exit 1
}

if ($Clean -and (Test-Path $outputPath)) {
    Write-Host "Cleaning output directory..."
    Remove-Item $outputPath -Recurse -Force
    Write-Host ""
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedValue `
    -o $outputPath `
    /p:PublishSingleFile=false `
    /p:PublishReadyToRun=false

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exePath = Join-Path $outputPath "TournamentTimer.Runner.Wpf.exe"

Write-Host ""
Write-Host "Publish complete."
Write-Host "EXE: $exePath"

if (Test-Path $exePath) {
    Write-Host ""
    Write-Host "Run example:"
    Write-Host "`"$exePath`" --server=http://localhost:5177 --runId=local-test-run --runnerId=runner-1 --runKey=RUN_KEY"
}
