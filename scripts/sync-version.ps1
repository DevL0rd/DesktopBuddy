param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

$versionPath = Join-Path $Root "VERSION"
if (-not (Test-Path -LiteralPath $versionPath)) {
    throw "VERSION file not found at $versionPath"
}

$version = (Get-Content -Raw -LiteralPath $versionPath).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION must be semantic Major.Minor.Patch without suffix for Thunderstore. Current value: '$version'"
}

$runtimeVersionPath = Join-Path $Root "RUNTIME_VERSION"
if (-not (Test-Path -LiteralPath $runtimeVersionPath)) {
    throw "RUNTIME_VERSION file not found at $runtimeVersionPath"
}

$runtimeVersion = (Get-Content -Raw -LiteralPath $runtimeVersionPath).Trim()
if ($runtimeVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "RUNTIME_VERSION must be semantic Major.Minor.Patch without suffix for Thunderstore. Current value: '$runtimeVersion'"
}

$tomlPath = Join-Path $Root "thunderstore.toml"
$toml = Get-Content -Raw -LiteralPath $tomlPath
$match = [regex]::Match($toml, 'versionNumber\s*=\s*"([^"]+)"')
if (-not $match.Success) {
    throw "versionNumber was not found in $tomlPath"
}

$updatedToml = $toml
$changed = $false

if ($match.Groups[1].Value -ne $version) {
    $updatedToml = [regex]::Replace($updatedToml, 'versionNumber\s*=\s*"[^"]+"', "versionNumber = `"$version`"", 1)
    $changed = $true
}

$runtimeDependencyPattern = '(?m)^\s*DevL0rd-DesktopBuddyRuntime\s*=\s*"([^"]+)"\s*$'
$runtimeDependencyMatch = [regex]::Match($updatedToml, $runtimeDependencyPattern)
if (-not $runtimeDependencyMatch.Success) {
    throw "DevL0rd-DesktopBuddyRuntime dependency was not found in $tomlPath"
}

if ($runtimeDependencyMatch.Groups[1].Value -ne $runtimeVersion) {
    $updatedToml = [regex]::Replace($updatedToml, $runtimeDependencyPattern, "DevL0rd-DesktopBuddyRuntime = `"$runtimeVersion`"", 1)
    $changed = $true
}

if (-not $changed) {
    Write-Host "DesktopBuddy version already synced to $version; runtime dependency already synced to $runtimeVersion"
    return
}

Set-Content -NoNewline -LiteralPath $tomlPath -Value $updatedToml

Write-Host "DesktopBuddy version synced to $version; runtime dependency synced to $runtimeVersion"
