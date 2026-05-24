param(
    [string]$SettingsPath = "timer-settings.json",
    [string]$RunId = "",
    [string]$Server = "",
    [string]$AdminKey = "",
    [string]$RunKey = "",
    [string]$ViewKey = ""
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

function Add-QueryParam([string]$Url, [string]$Name, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $Url }
    $separator = if ($Url.Contains("?")) { "&" } else { "?" }
    return "$Url$separator$Name=$([System.Uri]::EscapeDataString($Value))"
}

function Get-BrowserUrl([string]$Url) {
    try {
        $uri = [System.Uri]$Url

        if ($uri.Host -eq "0.0.0.0" -or $uri.Host -eq "::") {
            $builder = [System.UriBuilder]$uri
            $builder.Host = "localhost"
            return $builder.Uri.AbsoluteUri.TrimEnd("/")
        }
    }
    catch {
        return $Url
    }

    return $Url.TrimEnd("/")
}

$settings = Read-TimerSettings $SettingsPath

$RunId = Resolve-Setting $RunId $settings "runId" "local-test-run"
$Server = Resolve-Setting $Server $settings "serverUrl" "http://localhost:5177"
$AdminKey = Resolve-Setting $AdminKey $settings "adminKey" ""
$RunKey = Resolve-Setting $RunKey $settings "runKey" ""
$ViewKey = Resolve-Setting $ViewKey $settings "viewKey" ""

$BrowserServer = Get-BrowserUrl $Server

$adminUrl = "$BrowserServer/admin.html?runId=$([System.Uri]::EscapeDataString($RunId))"
$adminUrl = Add-QueryParam $adminUrl "adminKey" $AdminKey
$adminUrl = Add-QueryParam $adminUrl "runKey" $RunKey
$adminUrl = Add-QueryParam $adminUrl "viewKey" $ViewKey

$overlayUrl = "$BrowserServer/overlay.html?runId=$([System.Uri]::EscapeDataString($RunId))"
$overlayUrl = Add-QueryParam $overlayUrl "viewKey" $ViewKey

Write-Host "Opening admin:"
Write-Host $adminUrl
Write-Host ""
Write-Host "Overlay:"
Write-Host $overlayUrl
Write-Host ""
Write-Host "Settings: $SettingsPath"

Start-Process $adminUrl
