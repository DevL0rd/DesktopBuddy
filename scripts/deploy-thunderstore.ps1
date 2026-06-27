param(
    [switch]$Publish,
    [switch]$SkipBuild,
    [string]$ExpectedVersion,
    [string]$ExpectedRuntimeVersion,

    [ValidateSet("Main", "Runtime", "All")]
    [string]$Package = "All"
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

function Get-TomlString {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Name
    )

    $match = [regex]::Match($Content, "(?m)^\s*$([regex]::Escape($Name))\s*=\s*`"([^`"]*)`"\s*$")
    if (-not $match.Success) {
        throw "$Name was not found in thunderstore.toml"
    }
    return $match.Groups[1].Value
}

function Test-ThunderstoreVersionExists {
    param(
        [Parameter(Mandatory)][string]$Namespace,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Version
    )

    $uri = "https://thunderstore.io/api/experimental/package/$Namespace/$Name/$Version/"
    try {
        Invoke-RestMethod -Uri $uri -Headers @{ Accept = "application/json" } | Out-Null
        return $true
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 404) {
            return $false
        }

        throw "Could not check Thunderstore package version $Namespace-$Name-$Version`: $($_.Exception.Message)"
    }
}

function Invoke-TcliPublish {
    param([Parameter(Mandatory)][string]$ZipPath)

    if (-not (Test-Path -LiteralPath $ZipPath)) {
        throw "Package zip not found: $ZipPath"
    }

    Assert-ThunderstorePackageZip -ZipPath $ZipPath

    $token = $env:TCLI_AUTH_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "TCLI_AUTH_TOKEN is empty. Add it as a repository secret, or attach the GitHub environment that contains it to this workflow job."
    }

    $output = @(dotnet tcli publish --file $ZipPath --token $token 2>&1)
    $output | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        $joinedOutput = $output -join "`n"
        if ($joinedOutput -match "Package of the same namespace, name and version already exists") {
            Write-Host "Skipping publish because this exact package version already exists on Thunderstore."
            $global:LASTEXITCODE = 0
            return
        }

        throw "Thunderstore publish failed: $ZipPath"
    }
}

function Assert-ThunderstorePackageZip {
    param([Parameter(Mandatory)][string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        $rootEntries = @($entries | Where-Object { $_ -notmatch '/' } | Sort-Object)

        Write-Host "Thunderstore package root entries in ${ZipPath}:"
        $rootEntries | ForEach-Object { Write-Host "  $_" }

        foreach ($required in @("manifest.json", "README.md", "icon.png")) {
            if ($entries -notcontains $required) {
                throw "Thunderstore package is missing root $required`: $ZipPath"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Build-PackageIfMissing {
    param(
        [Parameter(Mandatory)][string]$PackageKind,
        [Parameter(Mandatory)][string]$Namespace,
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$PackageVersion
    )

    $zipPath = Join-Path $Root "build\$Namespace-$PackageName-$PackageVersion.zip"

    if ($Publish -and (Test-ThunderstoreVersionExists -Namespace $Namespace -Name $PackageName -Version $PackageVersion)) {
        Write-Host "Skipping $Namespace-$PackageName-$PackageVersion; version already exists on Thunderstore."
        return $null
    }

    & (Join-Path $PSScriptRoot "package.ps1") -Configuration Release -ThunderstoreFormat -Package $PackageKind
    if (-not (Test-Path -LiteralPath $zipPath)) {
        throw "Expected package zip was not created: $zipPath"
    }

    return $zipPath
}

Import-DotEnv $envPath

& (Join-Path $PSScriptRoot "sync-version.ps1") -Root $Root

$version = (Get-Content -Raw -LiteralPath (Join-Path $Root "VERSION")).Trim()
$runtimeVersion = (Get-Content -Raw -LiteralPath (Join-Path $Root "RUNTIME_VERSION")).Trim()
$toml = Get-Content -Raw -LiteralPath (Join-Path $Root "thunderstore.toml")
$namespace = Get-TomlString $toml "namespace"
$mainPackageName = Get-TomlString $toml "name"
$runtimePackageName = "DesktopBuddyRuntime"

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $normalizedExpected = $ExpectedVersion.Trim() -replace '^v', ''
    if ($version -ne $normalizedExpected) {
        throw "VERSION ($version) does not match expected release version ($normalizedExpected)"
    }
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedRuntimeVersion)) {
    $normalizedExpectedRuntime = $ExpectedRuntimeVersion.Trim() -replace '^v', ''
    if ($runtimeVersion -ne $normalizedExpectedRuntime) {
        throw "RUNTIME_VERSION ($runtimeVersion) does not match expected runtime release version ($normalizedExpectedRuntime)"
    }
}

Push-Location $Root
try {
    dotnet tool restore
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot "build.ps1") -Configuration Release -NoDeploy
    }

    $packagesToBuild = if ($Package -eq "All") { @("Runtime", "Main") } else { @($Package) }
    foreach ($packageKind in $packagesToBuild) {
        if ($packageKind -eq "Runtime") {
            $zipPath = Build-PackageIfMissing -PackageKind "Runtime" -Namespace $namespace -PackageName $runtimePackageName -PackageVersion $runtimeVersion
        }
        else {
            $zipPath = Build-PackageIfMissing -PackageKind "Main" -Namespace $namespace -PackageName $mainPackageName -PackageVersion $version
        }

        if ($Publish -and -not [string]::IsNullOrWhiteSpace($zipPath)) {
            Invoke-TcliPublish -ZipPath $zipPath
        }
    }
}
finally {
    Pop-Location
}
