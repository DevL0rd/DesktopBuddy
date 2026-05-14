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

$tomlPath = Join-Path $Root "thunderstore.toml"
$toml = Get-Content -Raw -LiteralPath $tomlPath
$match = [regex]::Match($toml, 'versionNumber\s*=\s*"([^"]+)"')
if (-not $match.Success) {
    throw "versionNumber was not found in $tomlPath"
}

if ($match.Groups[1].Value -eq $version) {
    Write-Host "DesktopBuddy version already synced to $version"
    return
}

$updatedToml = [regex]::Replace($toml, 'versionNumber\s*=\s*"[^"]+"', "versionNumber = `"$version`"", 1)
Set-Content -NoNewline -LiteralPath $tomlPath -Value $updatedToml

Write-Host "DesktopBuddy version synced to $version"
