param(
    [switch]$Publish,
    [switch]$SkipBuild,
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$envPath = Join-Path $Root ".env"

function Import-DotEnv {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#")) {
            continue
        }

        $parts = $trimmed.Split("=", 2)
        if ($parts.Length -ne 2) {
            continue
        }

        $name = $parts[0].Trim()
        $value = $parts[1].Trim().Trim('"').Trim("'")
        if ($name.Length -gt 0 -and [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name))) {
            [Environment]::SetEnvironmentVariable($name, $value, "Process")
        }
    }
}

Import-DotEnv $envPath

& (Join-Path $PSScriptRoot "sync-version.ps1") -Root $Root

$version = (Get-Content -Raw -LiteralPath (Join-Path $Root "VERSION")).Trim()
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $normalizedExpected = $ExpectedVersion.Trim() -replace '^v', ''
    if ($version -ne $normalizedExpected) {
        throw "VERSION ($version) does not match expected release version ($normalizedExpected)"
    }
}

Push-Location $Root
try {
    dotnet tool restore
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot "build.ps1") -Configuration Release -NoDeploy
    }

    if ($Publish) {
        if ([string]::IsNullOrWhiteSpace($env:TCLI_AUTH_TOKEN)) {
            throw "TCLI_AUTH_TOKEN is not set. Put it in a local .env file or GitHub Actions secret."
        }

        dotnet tcli publish
    }
    else {
        dotnet tcli build
    }
}
finally {
    Pop-Location
}
