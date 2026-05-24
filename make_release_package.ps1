param(
    [string]$RunId = "local-test-run",
    [string]$Configuration = "Release",
    [string]$LiveSplitDir = "",
    [switch]$BuildLiveSplitPlugin
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = $scriptDir

if (!(Test-Path (Join-Path $root "TournamentTimer.Server\TournamentTimer.Server.csproj"))) {
    $root = Get-Location
}

$serverProject = Join-Path $root "TournamentTimer.Server\TournamentTimer.Server.csproj"
$runnerProject = Join-Path $root "TournamentTimer.Runner.Wpf\TournamentTimer.Runner.Wpf.csproj"
$aslLauncherProject = Join-Path $root "tools\TournamentTimer.AslHost.Launcher\TournamentTimer.AslHost.Launcher.csproj"
$aslCatalogProject = Join-Path $root "tools\TournamentTimer.AslCatalog\TournamentTimer.AslCatalog.csproj"
$buildAslHostScript = Join-Path $root "scripts\build-asl-host.ps1"
$bridgeProject = Join-Path $root "LiveSplit.TournamentTimerBridge\LiveSplit.TournamentTimerBridge.csproj"

if (!(Test-Path $serverProject)) {
    throw "Server project not found: $serverProject"
}

if (!(Test-Path $runnerProject)) {
    throw "Runner UI project not found: $runnerProject"
}

if (!(Test-Path $aslLauncherProject)) {
    throw "ASL Host Launcher project not found: $aslLauncherProject"
}

if (!(Test-Path $aslCatalogProject)) {
    throw "ASL Catalog project not found: $aslCatalogProject"
}

if (!(Test-Path $buildAslHostScript)) {
    throw "ASL Host build script not found: $buildAslHostScript"
}

if (!(Test-Path $bridgeProject)) {
    throw "LiveSplit bridge project not found: $bridgeProject"
}

$artifactsRoot = Join-Path $root "artifacts"
$publishRoot = Join-Path $artifactsRoot "publish"
$packagesRoot = Join-Path $artifactsRoot "packages"

$serverPublish = Join-Path $publishRoot "server\win-x64"
$runnerPublish = Join-Path $publishRoot "runner-ui\win-x64"
$aslLauncherPublish = Join-Path $publishRoot "asl-host-launcher\win-x64"
$aslCatalogBuildOutput = Join-Path $root "tools\TournamentTimer.AslCatalog\bin\x64\$Configuration\net481"
$aslHostRuntimeRoot = Join-Path $artifactsRoot "asl-host"

$runnerPackage = Join-Path $packagesRoot "runner-win-x64"
$serverPackage = Join-Path $packagesRoot "server-admin-win-x64"
$fullPackage = Join-Path $packagesRoot "full-win-x64"

Write-Host "Publishing TournamentTimer.Server for win-x64..."
dotnet publish $serverProject -c $Configuration -r win-x64 --self-contained true -o $serverPublish

Write-Host "Publishing TournamentTimer.Runner.Wpf for win-x64..."
dotnet publish $runnerProject -c $Configuration -r win-x64 --self-contained true -o $runnerPublish

Write-Host "Publishing TournamentTimer.AslHost.Launcher for win-x64..."
dotnet publish $aslLauncherProject -c $Configuration -r win-x64 --self-contained true -o $aslLauncherPublish

Write-Host "Building ASL Host runtime x86/x64..."
powershell -ExecutionPolicy Bypass -File $buildAslHostScript -Configuration $Configuration

Write-Host "Building TournamentTimer.AslCatalog for win-x64..."
dotnet build $aslCatalogProject -c $Configuration -p:Platform=x64 -p:PlatformTarget=x64

$aslCatalogExe = Join-Path $aslCatalogBuildOutput "TournamentTimer.AslCatalog.exe"

if (!(Test-Path $aslCatalogExe)) {
    throw "ASL Catalog exe was not built: $aslCatalogExe"
}

$pluginDllPath = Join-Path $root "LiveSplit.TournamentTimerBridge\bin\$Configuration\LiveSplit.TournamentTimerBridge.dll"
$shouldBuildLiveSplitPlugin = $BuildLiveSplitPlugin -or (-not [string]::IsNullOrWhiteSpace($LiveSplitDir))

if ($shouldBuildLiveSplitPlugin) {
    Write-Host "Building LiveSplit bridge plugin..."
    $bridgeBuildArgs = @("build", $bridgeProject, "-c", $Configuration)

    if (-not [string]::IsNullOrWhiteSpace($LiveSplitDir)) {
        $bridgeBuildArgs += "/p:LiveSplitDir=$LiveSplitDir"
    }

    dotnet @bridgeBuildArgs

    if ($LASTEXITCODE -ne 0) {
        throw "LiveSplit bridge plugin build failed. Pass -LiveSplitDir with the folder that contains LiveSplit.exe, LiveSplit.Core.dll and UpdateManager.dll, or omit -BuildLiveSplitPlugin and use an already built DLL."
    }
}
else {
    Write-Host "Skipping LiveSplit bridge build. Using existing plugin DLL if found."
}

if (!(Test-Path $pluginDllPath)) {
    $pluginDllFallback = Get-ChildItem (Join-Path $root "LiveSplit.TournamentTimerBridge\bin") -Recurse -Filter "LiveSplit.TournamentTimerBridge.dll" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($pluginDllFallback -ne $null) {
        $pluginDllPath = $pluginDllFallback.FullName
    }
}

if (!(Test-Path $pluginDllPath)) {
    throw "LiveSplit.TournamentTimerBridge.dll not found. Build it first or run make_release_package.ps1 with -BuildLiveSplitPlugin -LiveSplitDir <LiveSplit folder>."
}

$pluginDll = Get-Item $pluginDllPath
Write-Host "LiveSplit plugin: $($pluginDll.FullName)"

Write-Host "Preparing package folders..."
Remove-Item $packagesRoot -Recurse -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force $runnerPackage | Out-Null
New-Item -ItemType Directory -Force $serverPackage | Out-Null
New-Item -ItemType Directory -Force $fullPackage | Out-Null

function Copy-IfExists {
    param(
        [Parameter(Mandatory=$true)][string]$Source,
        [Parameter(Mandatory=$true)][string]$Destination
    )

    if (Test-Path $Source) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $Destination) | Out-Null
        Copy-Item $Source $Destination -Recurse -Force
    }
}

function Copy-Readme {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    $source = Join-Path $root $Name

    if (Test-Path $source) {
        Copy-Item $source (Join-Path $DestinationDir $Name) -Force
    }
    else {
        Write-Warning "README not found: $Name"
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
        $source = Join-Path $root $name

        if (Test-Path $source) {
            Copy-Item $source (Join-Path $DestinationDir $name) -Force
        }
        else {
            Write-Warning "Document not found: $name"
        }
    }
}

function Copy-FullDocumentation {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    $names = @(
        "docs\TournamentTimer_Overview.docx",
        "docs\contacts_qr.png"
    )

    foreach ($name in $names) {
        $source = Join-Path $root $name
        $destination = Join-Path $DestinationDir $name

        if (Test-Path $source) {
            New-Item -ItemType Directory -Force (Split-Path -Parent $destination) | Out-Null
            Copy-Item $source $destination -Force
        }
        else {
            Write-Warning "Document not found: $name"
        }
    }
}


function Copy-CommonScripts {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir,
        [Parameter(Mandatory=$true)][string[]]$Names
    )

    $scriptsDir = Join-Path $DestinationDir "scripts"
    New-Item -ItemType Directory -Force $scriptsDir | Out-Null

    foreach ($name in $Names) {
        $source = Join-Path $root "scripts\$name"

        if (Test-Path $source) {
            Copy-Item $source (Join-Path $scriptsDir $name) -Force
        }
        else {
            Write-Warning "Script not found: scripts\$name"
        }
    }
}

function Copy-ConfigsAndExampleSettings {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    New-Item -ItemType Directory -Force (Join-Path $DestinationDir "configs") | Out-Null

    if (Test-Path (Join-Path $root "configs")) {
        Copy-Item (Join-Path $root "configs\*") (Join-Path $DestinationDir "configs") -Recurse -Force
    }

    Copy-IfExists (Join-Path $root "timer-settings.example.json") (Join-Path $DestinationDir "timer-settings.example.json")
    Copy-IfExists (Join-Path $root "timer-settings.turn.example.json") (Join-Path $DestinationDir "timer-settings.turn.example.json")
}

function Copy-RunAssets {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    $target = Join-Path $DestinationDir "server-runs\assets"
    New-Item -ItemType Directory -Force $target | Out-Null

    $assetSources = @(
        (Join-Path $root "server-runs\assets"),
        (Join-Path $root "TournamentTimer.Server\server-runs\assets")
    )

    foreach ($source in $assetSources) {
        if (Test-Path $source) {
            Copy-Item (Join-Path $source "*") $target -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-RunAssetsSource {
    $candidates = @(
        (Join-Path $root "server-runs\assets\$RunId"),
        (Join-Path $root "TournamentTimer.Server\server-runs\assets\$RunId")
    )

    foreach ($candidate in $candidates) {
        if ((Test-Path (Join-Path $candidate "autosplitter.asl")) -and
            (Test-Path (Join-Path $candidate "splits.lss"))) {
            return $candidate
        }
    }

    return $null
}

function Warn-IfRunAssetsMissing {
    $runAssetsSource = Get-RunAssetsSource

    if ($runAssetsSource -ne $null) {
        return
    }

    Write-Warning "ASL run assets not found for RunId=$RunId. Expected autosplitter.asl and splits.lss."
    Write-Warning "Checked:"
    Write-Warning ("  " + (Join-Path $root "server-runs\assets\$RunId"))
    Write-Warning ("  " + (Join-Path $root "TournamentTimer.Server\server-runs\assets\$RunId"))
}

function Assert-AslHostRuntimeReady {
    $x86Exe = Join-Path $aslHostRuntimeRoot "x86\TournamentTimer.AslHost.PoC.exe"
    $x64Exe = Join-Path $aslHostRuntimeRoot "x64\TournamentTimer.AslHost.PoC.exe"

    if (!(Test-Path $x86Exe)) {
        throw "ASL Host x86 runtime missing: $x86Exe"
    }

    if (!(Test-Path $x64Exe)) {
        throw "ASL Host x64 runtime missing: $x64Exe"
    }
}

function Copy-AslHostLauncherAndRuntime {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    Assert-AslHostRuntimeReady

    $launcherDir = Join-Path $DestinationDir "asl-host-launcher"
    New-Item -ItemType Directory -Force $launcherDir | Out-Null
    Copy-Item (Join-Path $aslLauncherPublish "*") $launcherDir -Recurse -Force

    $targetRuntimeRoot = Join-Path $DestinationDir "artifacts\asl-host"
    New-Item -ItemType Directory -Force $targetRuntimeRoot | Out-Null

    foreach ($arch in @("x86", "x64")) {
        $sourceArchDir = Join-Path $aslHostRuntimeRoot $arch
        $targetArchDir = Join-Path $targetRuntimeRoot $arch

        if (Test-Path $sourceArchDir) {
            New-Item -ItemType Directory -Force $targetArchDir | Out-Null
            Copy-Item (Join-Path $sourceArchDir "*") $targetArchDir -Recurse -Force
        }
    }

    Warn-IfRunAssetsMissing

    # Compatibility marker for older launcher root detection.
    New-Item -ItemType Directory -Force (Join-Path $DestinationDir "tools") | Out-Null
}

function Copy-AslCatalog {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    $catalogExe = Join-Path $aslCatalogBuildOutput "TournamentTimer.AslCatalog.exe"

    if (!(Test-Path $catalogExe)) {
        throw "ASL Catalog exe not found: $catalogExe"
    }

    $catalogDir = Join-Path $DestinationDir "asl-catalog"
    New-Item -ItemType Directory -Force $catalogDir | Out-Null
    Copy-Item (Join-Path $aslCatalogBuildOutput "*") $catalogDir -Recurse -Force

    Warn-IfRunAssetsMissing
}


function Copy-LiveSplitPlugin {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    if ($pluginDll -eq $null) {
        throw "LiveSplit plugin DLL is not available."
    }

    $pluginDir = Join-Path $DestinationDir "livesplit-plugin"
    New-Item -ItemType Directory -Force $pluginDir | Out-Null
    Copy-Item $pluginDll.FullName (Join-Path $pluginDir "LiveSplit.TournamentTimerBridge.dll") -Force

    $pluginReadme = Join-Path $root "README_LIVESPLIT_BRIDGE.md"

    if (!(Test-Path $pluginReadme)) {
        throw "LiveSplit bridge README not found: $pluginReadme"
    }

    Copy-Item $pluginReadme (Join-Path $pluginDir "README_LIVESPLIT_BRIDGE.md") -Force
}


function Remove-InternalServerRuns {
    param(
        [Parameter(Mandatory=$true)][string]$DestinationDir
    )

    $internalServerRuns = Join-Path $DestinationDir "server\server-runs"

    if (Test-Path $internalServerRuns) {
        Remove-Item $internalServerRuns -Recurse -Force
    }
}

function Assert-PackageFile {
    param(
        [Parameter(Mandatory=$true)][string]$PackageDir,
        [Parameter(Mandatory=$true)][string]$RelativePath
    )

    $path = Join-Path $PackageDir $RelativePath

    if (!(Test-Path $path)) {
        throw "Package validation failed. Missing file: $path"
    }
}

function Assert-PackageDirectory {
    param(
        [Parameter(Mandatory=$true)][string]$PackageDir,
        [Parameter(Mandatory=$true)][string]$RelativePath
    )

    $path = Join-Path $PackageDir $RelativePath

    if (!(Test-Path $path)) {
        throw "Package validation failed. Missing directory: $path"
    }
}

function Assert-PackageMissing {
    param(
        [Parameter(Mandatory=$true)][string]$PackageDir,
        [Parameter(Mandatory=$true)][string]$RelativePath
    )

    $path = Join-Path $PackageDir $RelativePath

    if (Test-Path $path) {
        throw "Package validation failed. Unexpected path: $path"
    }
}

# Runner package.
New-Item -ItemType Directory -Force (Join-Path $runnerPackage "runner-ui") | Out-Null
Copy-Item (Join-Path $runnerPublish "*") (Join-Path $runnerPackage "runner-ui") -Recurse -Force
Copy-CommonScripts $runnerPackage @("start-runner-ui.ps1", "start-asl-host-launcher.ps1")
Copy-RunAssets $runnerPackage
Copy-AslHostLauncherAndRuntime $runnerPackage
Copy-Readme "README_RUNNER.md" $runnerPackage
Copy-Readme "README_LIVESPLIT_BRIDGE.md" $runnerPackage
Copy-Readme "README_ASL_HOST.md" $runnerPackage
Copy-CommonLegalDocuments $runnerPackage
Copy-LiveSplitPlugin $runnerPackage

# Server/admin Windows package.
New-Item -ItemType Directory -Force (Join-Path $serverPackage "server") | Out-Null
Copy-Item (Join-Path $serverPublish "*") (Join-Path $serverPackage "server") -Recurse -Force
Remove-InternalServerRuns $serverPackage
Copy-ConfigsAndExampleSettings $serverPackage
Copy-RunAssets $serverPackage
Copy-AslCatalog $serverPackage
Copy-CommonScripts $serverPackage @("start-server.ps1", "open-admin.ps1", "open-asl-catalog.ps1")
Copy-Readme "README_ADMIN.md" $serverPackage
Copy-Readme "README_CAMERA_TURN.md" $serverPackage
Copy-Readme "README_ASL_HOST.md" $serverPackage
Copy-Readme "README_RUNNER.md" $serverPackage
Copy-Readme "README_LIVESPLIT_BRIDGE.md" $serverPackage
Copy-CommonLegalDocuments $serverPackage

# Full Windows package.
New-Item -ItemType Directory -Force (Join-Path $fullPackage "server") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $fullPackage "runner-ui") | Out-Null
Copy-Item (Join-Path $serverPublish "*") (Join-Path $fullPackage "server") -Recurse -Force
Remove-InternalServerRuns $fullPackage
Copy-Item (Join-Path $runnerPublish "*") (Join-Path $fullPackage "runner-ui") -Recurse -Force
Copy-ConfigsAndExampleSettings $fullPackage
Copy-RunAssets $fullPackage
Copy-AslHostLauncherAndRuntime $fullPackage
Copy-AslCatalog $fullPackage
Copy-CommonScripts $fullPackage @("start-server.ps1", "open-admin.ps1", "start-runner-ui.ps1", "start-runner.ps1", "start-local.ps1", "open-asl-catalog.ps1", "start-asl-host-launcher.ps1")
Copy-Readme "README_ADMIN.md" $fullPackage
Copy-Readme "README_CAMERA_TURN.md" $fullPackage
Copy-Readme "README_ASL_HOST.md" $fullPackage
Copy-Readme "README_RUNNER.md" $fullPackage
Copy-Readme "README_LIVESPLIT_BRIDGE.md" $fullPackage
Copy-CommonLegalDocuments $fullPackage
Copy-FullDocumentation $fullPackage
Copy-LiveSplitPlugin $fullPackage



$screenshotsSource = Join-Path $PSScriptRoot "docs\screenshots"
$screenshotsTargets = @(
    (Join-Path $PSScriptRoot "artifacts\packages\runner-win-x64\docs\screenshots"),
    (Join-Path $PSScriptRoot "artifacts\packages\server-admin-win-x64\docs\screenshots"),
    (Join-Path $PSScriptRoot "artifacts\packages\full-win-x64\docs\screenshots")
)

if (Test-Path $screenshotsSource) {
    foreach ($screenshotsTarget in $screenshotsTargets) {
        New-Item -ItemType Directory -Force $screenshotsTarget | Out-Null
        Copy-Item (Join-Path $screenshotsSource "*") $screenshotsTarget -Recurse -Force
    }
}
Write-Host "Validating package contents..."
Assert-PackageFile $runnerPackage "runner-ui\TournamentTimer.Runner.Wpf.exe"
Assert-PackageFile $runnerPackage "asl-host-launcher\TournamentTimer.AslHost.Launcher.exe"
Assert-PackageFile $runnerPackage "scripts\start-asl-host-launcher.ps1"
Assert-PackageFile $runnerPackage "livesplit-plugin\LiveSplit.TournamentTimerBridge.dll"
Assert-PackageFile $runnerPackage "livesplit-plugin\README_LIVESPLIT_BRIDGE.md"
Assert-PackageFile $runnerPackage "README_LIVESPLIT_BRIDGE.md"
Assert-PackageFile $runnerPackage "artifacts\asl-host\x86\TournamentTimer.AslHost.PoC.exe"
Assert-PackageFile $runnerPackage "artifacts\asl-host\x64\TournamentTimer.AslHost.PoC.exe"
Assert-PackageDirectory $runnerPackage "server-runs\assets"
Assert-PackageMissing $runnerPackage "artifacts\asl-host\runs"
Assert-PackageMissing $runnerPackage "asl-catalog\TournamentTimer.AslCatalog.exe"
Assert-PackageMissing $runnerPackage "scripts\open-asl-catalog.ps1"
Assert-PackageFile $runnerPackage "README.md"
Assert-PackageFile $runnerPackage "README_RUNNER.md"
Assert-PackageFile $runnerPackage "README_ASL_HOST.md"
Assert-PackageFile $runnerPackage "LICENSE.txt"
Assert-PackageFile $runnerPackage "NOTICE.txt"
Assert-PackageFile $runnerPackage "README_USAGE_TERMS_RU.md"
Assert-PackageFile $runnerPackage "AUTHORS.md"

Assert-PackageFile $serverPackage "server\TournamentTimer.Server.exe"
Assert-PackageFile $serverPackage "asl-catalog\TournamentTimer.AslCatalog.exe"
Assert-PackageFile $serverPackage "scripts\open-asl-catalog.ps1"
Assert-PackageDirectory $serverPackage "server-runs\assets"
Assert-PackageMissing $serverPackage "server\server-runs"
Assert-PackageMissing $serverPackage "artifacts\asl-host\runs"
Assert-PackageMissing $serverPackage "asl-host-launcher\TournamentTimer.AslHost.Launcher.exe"
Assert-PackageMissing $serverPackage "scripts\start-asl-host-launcher.ps1"
Assert-PackageMissing $serverPackage "livesplit-plugin\LiveSplit.TournamentTimerBridge.dll"
Assert-PackageFile $serverPackage "README.md"
Assert-PackageFile $serverPackage "README_ADMIN.md"
Assert-PackageFile $serverPackage "README_CAMERA_TURN.md"
Assert-PackageFile $serverPackage "README_ASL_HOST.md"
Assert-PackageFile $serverPackage "LICENSE.txt"
Assert-PackageFile $serverPackage "NOTICE.txt"
Assert-PackageFile $serverPackage "README_USAGE_TERMS_RU.md"
Assert-PackageFile $serverPackage "AUTHORS.md"

Assert-PackageFile $fullPackage "runner-ui\TournamentTimer.Runner.Wpf.exe"
Assert-PackageFile $fullPackage "server\TournamentTimer.Server.exe"
Assert-PackageFile $fullPackage "asl-host-launcher\TournamentTimer.AslHost.Launcher.exe"
Assert-PackageFile $fullPackage "asl-catalog\TournamentTimer.AslCatalog.exe"
Assert-PackageFile $fullPackage "scripts\start-asl-host-launcher.ps1"
Assert-PackageFile $fullPackage "scripts\open-asl-catalog.ps1"
Assert-PackageFile $fullPackage "livesplit-plugin\LiveSplit.TournamentTimerBridge.dll"
Assert-PackageFile $fullPackage "livesplit-plugin\README_LIVESPLIT_BRIDGE.md"
Assert-PackageFile $fullPackage "README.md"
Assert-PackageFile $fullPackage "README_ADMIN.md"
Assert-PackageFile $fullPackage "README_RUNNER.md"
Assert-PackageFile $fullPackage "README_ASL_HOST.md"
Assert-PackageFile $fullPackage "README_CAMERA_TURN.md"
Assert-PackageFile $fullPackage "README_LIVESPLIT_BRIDGE.md"
Assert-PackageFile $fullPackage "LICENSE.txt"
Assert-PackageFile $fullPackage "NOTICE.txt"
Assert-PackageFile $fullPackage "README_USAGE_TERMS_RU.md"
Assert-PackageFile $fullPackage "AUTHORS.md"
Assert-PackageFile $fullPackage "artifacts\asl-host\x86\TournamentTimer.AslHost.PoC.exe"
Assert-PackageFile $fullPackage "artifacts\asl-host\x64\TournamentTimer.AslHost.PoC.exe"
Assert-PackageDirectory $fullPackage "server-runs\assets"
Assert-PackageMissing $fullPackage "server\server-runs"
Assert-PackageMissing $fullPackage "artifacts\asl-host\runs"
Assert-PackageFile $fullPackage "docs\TournamentTimer_Overview.docx"
Assert-PackageFile $fullPackage "docs\contacts_qr.png"

Write-Host "Creating zip packages..."
$runnerZip = Join-Path $artifactsRoot "TournamentTimer-runner-win-x64.zip"
$serverZip = Join-Path $artifactsRoot "TournamentTimer-server-admin-win-x64.zip"
$fullZip = Join-Path $artifactsRoot "TournamentTimer-full-win-x64.zip"

Compress-Archive -Path (Join-Path $runnerPackage "*") -DestinationPath $runnerZip -Force
Compress-Archive -Path (Join-Path $serverPackage "*") -DestinationPath $serverZip -Force
Compress-Archive -Path (Join-Path $fullPackage "*") -DestinationPath $fullZip -Force

Write-Host ""
Write-Host "Windows packages ready:"
Write-Host "  $runnerZip"
Write-Host "  $serverZip"
Write-Host "  $fullZip"
Write-Host ""
Write-Host "Unpacked packages:"
Write-Host "  $packagesRoot"
Write-Host ""
Write-Host "Next local test:"
Write-Host "  cd $fullPackage"
Write-Host "  powershell -ExecutionPolicy Bypass -File .\scripts\start-server.ps1 -RunId $RunId"

