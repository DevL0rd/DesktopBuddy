param(
    [string]$Configuration = "Release",
    [string]$ZipName = $env:ZIP_NAME,

    [ValidateSet("Manual", "Main", "Runtime", "All")]
    [string]$Package,

    [Alias("Thunderstore")]
    [switch]$ThunderstoreFormat
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
& (Join-Path $PSScriptRoot "sync-version.ps1") -Root $Root

if ([string]::IsNullOrWhiteSpace($Package)) {
    $Package = if ($ThunderstoreFormat) { "All" } else { "Manual" }
}

$version = (Get-Content -Raw -LiteralPath (Join-Path $Root "VERSION")).Trim()
$runtimeVersion = (Get-Content -Raw -LiteralPath (Join-Path $Root "RUNTIME_VERSION")).Trim()
$tomlPath = Join-Path $Root "thunderstore.toml"
$toml = Get-Content -Raw -LiteralPath $tomlPath
$runtimeDirName = "DesktopBuddyRuntime"
$runtimePackageName = "DesktopBuddyRuntime"

function Assert-SemVer {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "$Name must be semantic Major.Minor.Patch without suffix. Current value: '$Value'"
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

function Get-TomlDependencies {
    param([Parameter(Mandatory)][string]$Content)

    $match = [regex]::Match($Content, '(?ms)^\[package\.dependencies\]\s*(.*?)(?=^\[|\z)')
    if (-not $match.Success) {
        throw "[package.dependencies] was not found in thunderstore.toml"
    }

    $dependencies = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($match.Groups[1].Value -split "`r?`n")) {
        $dependency = [regex]::Match($line, '^\s*([A-Za-z0-9_.-]+)\s*=\s*"([^"]+)"\s*$')
        if ($dependency.Success) {
            $dependencies.Add("$($dependency.Groups[1].Value)-$($dependency.Groups[2].Value)")
        }
    }

    return $dependencies.ToArray()
}

function Get-DesktopBuddyModOutput {
    param([Parameter(Mandatory)][string]$ConfigurationName)

    $output = Get-ChildItem -LiteralPath (Join-Path $Root "DesktopBuddy\bin\$ConfigurationName") -Directory -Filter "net10.0-windows*" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "DesktopBuddy.dll") } |
        Select-Object -First 1

    if ($null -eq $output) {
        throw "DesktopBuddy.dll not found under DesktopBuddy\bin\$ConfigurationName\net10.0-windows*. Run scripts\build.ps1 first."
    }

    return $output.FullName
}

function Update-SetupPayloadManifest {
    param([Parameter(Mandatory)][string]$RuntimeSource)

    $manifest = Join-Path $RuntimeSource "DesktopBuddySetupPayloads.md5"
    $payloads = @(
        "softcam64.dll",
        "softcam.dll",
        "VBCABLE_Setup_x64.exe"
    )

    $lines = foreach ($payload in $payloads) {
        $path = Join-Path $RuntimeSource $payload
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Setup payload not found: $runtimeDirName\$payload"
        }

        "$payload=$((Get-FileHash -Algorithm MD5 -LiteralPath $path).Hash.ToLowerInvariant())"
    }

    Set-Content -LiteralPath $manifest -Value $lines
}

function New-GrayscalePng {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    Add-Type -AssemblyName System.Drawing
    $sourceBitmap = [System.Drawing.Bitmap]::new($Source)
    $targetBitmap = [System.Drawing.Bitmap]::new(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($targetBitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($sourceBitmap, 0, 0, 256, 256)
        }
        finally {
            $graphics.Dispose()
        }

        for ($x = 0; $x -lt $targetBitmap.Width; $x++) {
            for ($y = 0; $y -lt $targetBitmap.Height; $y++) {
                $pixel = $targetBitmap.GetPixel($x, $y)
                $gray = [int]([Math]::Round(($pixel.R * 0.299) + ($pixel.G * 0.587) + ($pixel.B * 0.114)))
                $targetBitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($pixel.A, $gray, $gray, $gray))
            }
        }

        $targetBitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $targetBitmap.Dispose()
        $sourceBitmap.Dispose()
    }
}

function New-ZipFromStage {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$OutZip
    )

    if (Test-Path -LiteralPath $OutZip) {
        Remove-Item -LiteralPath $OutZip -Force
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($OutZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $Stage -Recurse -File) {
            $entryName = [IO.Path]::GetRelativePath($Stage, $file.FullName).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-PackageStage {
    param([Parameter(Mandatory)][string]$Name)

    $stage = Join-Path $env:TEMP "DesktopBuddyPackage\$Name"
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    return $stage
}

function Add-PackageMetadata {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Dependencies,
        [Parameter(Mandatory)][string]$Icon
    )

    Copy-Item -LiteralPath (Join-Path $Root "README_THUNDERSTORE.md") -Destination (Join-Path $Stage "README.md")
    Copy-Item -LiteralPath $Icon -Destination (Join-Path $Stage "icon.png")
    Copy-Item -LiteralPath (Join-Path $Root "CHANGELOG.md") -Destination (Join-Path $Stage "CHANGELOG.md")

    $manifest = [ordered]@{
        name = $PackageName
        version_number = $PackageVersion
        website_url = Get-TomlString $toml "websiteUrl"
        description = $Description
        dependencies = $Dependencies
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -NoNewline -LiteralPath (Join-Path $Stage "manifest.json")
}

function Copy-ManagedRuntimeDependencies {
    param(
        [Parameter(Mandatory)][string]$ModOutDir,
        [Parameter(Mandatory)][string]$RuntimeTarget
    )

    foreach ($file in @(
        "FFmpeg.AutoGen.dll",
        "Microsoft.Windows.SDK.NET.dll",
        "WinRT.Runtime.dll"
    )) {
        $path = Join-Path $ModOutDir $file
        if (-not (Test-Path -LiteralPath $path)) {
            throw "DesktopBuddy build dependency $file not found under $ModOutDir. Run scripts\build.ps1 first."
        }
        Copy-Item -LiteralPath $path -Destination (Join-Path $RuntimeTarget $file)
    }
}

function Build-ManualPackage {
    $modOutDir = Get-DesktopBuddyModOutput -ConfigurationName $Configuration
    $bridgeDll = Join-Path $Root "DesktopBuddySharedTextureBridge\bin\$Configuration\net472\DesktopBuddySharedTextureBridge.dll"
    $runtimeSource = Join-Path $Root $runtimeDirName
    $namespace = Get-TomlString $toml "namespace"
    $mainName = Get-TomlString $toml "name"
    $description = Get-TomlString $toml "description"
    $runtimeDependency = "$namespace-$runtimePackageName-$runtimeVersion"
    $dependencies = @(Get-TomlDependencies $toml | Where-Object { $_ -notmatch "^[^-]+-$runtimePackageName-" })
    $dependencies += $runtimeDependency
    $runtimeDescription = "Runtime payloads for DesktopBuddy, including FFmpeg, tunnel, virtual camera, and virtual audio setup files."
    $runtimeIcon = Join-Path $Root "icon_runtime.png"

    foreach ($path in @((Join-Path $modOutDir "DesktopBuddy.dll"), $bridgeDll, $runtimeSource, (Join-Path $Root "icon_transparent.png"))) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required package input not found: $path"
        }
    }

    New-GrayscalePng -Source (Join-Path $Root "icon.png") -Destination $runtimeIcon

    $name = if ([string]::IsNullOrWhiteSpace($ZipName)) { "DesktopBuddy-$version" } else { $ZipName }
    $stage = New-PackageStage -Name $name
    $outZip = Join-Path $Root "$name.zip"
    $mainPackageRoot = Join-Path $stage "BepInEx\plugins\$namespace-$mainName"
    $runtimePackageRoot = Join-Path $stage "BepInEx\plugins\$namespace-$runtimePackageName"
    $gamePluginDir = Join-Path $mainPackageRoot "DesktopBuddy"
    $runtimeTarget = Join-Path $runtimePackageRoot "DesktopBuddy\$runtimeDirName"
    $bridgeTarget = Join-Path $stage "Renderer\BepInEx\plugins\$namespace-$mainName\DesktopBuddySharedTextureBridge"

    New-Item -ItemType Directory -Force -Path $mainPackageRoot, $runtimePackageRoot, $gamePluginDir, $runtimeTarget, $bridgeTarget | Out-Null
    Add-PackageMetadata -Stage $mainPackageRoot -PackageName $mainName -PackageVersion $version -Description $description -Dependencies $dependencies -Icon (Join-Path $Root "icon.png")
    Add-PackageMetadata -Stage $runtimePackageRoot -PackageName $runtimePackageName -PackageVersion $runtimeVersion -Description $runtimeDescription -Dependencies @() -Icon $runtimeIcon
    Copy-Item -LiteralPath (Join-Path $modOutDir "DesktopBuddy.dll") -Destination (Join-Path $gamePluginDir "DesktopBuddy.dll")
    Copy-Item -LiteralPath (Join-Path $Root "icon_transparent.png") -Destination (Join-Path $gamePluginDir "icon_transparent.png")
    Copy-Item -LiteralPath (Join-Path $Root "scripts\CollectDesktopBuddyDiagnostics.ps1") -Destination (Join-Path $gamePluginDir "CollectDesktopBuddyDiagnostics.ps1")
    $modSha = Join-Path $modOutDir "DesktopBuddy.sha"
    if (Test-Path -LiteralPath $modSha) {
        Copy-Item -LiteralPath $modSha -Destination (Join-Path $gamePluginDir "DesktopBuddy.sha")
    }
    Copy-Item -Path (Join-Path $runtimeSource "*") -Destination $runtimeTarget -Recurse -Force
    Copy-ManagedRuntimeDependencies -ModOutDir $modOutDir -RuntimeTarget $runtimeTarget
    Copy-Item -LiteralPath $bridgeDll -Destination (Join-Path $bridgeTarget "DesktopBuddySharedTextureBridge.dll")

    New-ZipFromStage -Stage $stage -OutZip $outZip
    Remove-Item -LiteralPath $stage -Recurse -Force
    Write-Host "Done: $outZip (manual profile-root package layout)"
}

function Build-MainThunderstorePackage {
    $modOutDir = Get-DesktopBuddyModOutput -ConfigurationName $Configuration
    $bridgeDll = Join-Path $Root "DesktopBuddySharedTextureBridge\bin\$Configuration\net472\DesktopBuddySharedTextureBridge.dll"
    $mainName = Get-TomlString $toml "name"
    $namespace = Get-TomlString $toml "namespace"
    $description = Get-TomlString $toml "description"
    $runtimeDependency = "$namespace-$runtimePackageName-$runtimeVersion"
    $dependencies = @(Get-TomlDependencies $toml | Where-Object { $_ -notmatch "^[^-]+-$runtimePackageName-" })
    $dependencies += $runtimeDependency

    foreach ($path in @((Join-Path $modOutDir "DesktopBuddy.dll"), $bridgeDll, (Join-Path $Root "icon.png"), (Join-Path $Root "README_THUNDERSTORE.md"))) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required package input not found: $path"
        }
    }

    $name = if ([string]::IsNullOrWhiteSpace($ZipName)) { "$namespace-$mainName-$version" } else { $ZipName }
    $stage = New-PackageStage -Name $name
    $outDir = Join-Path $Root "build"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $outZip = Join-Path $outDir "$name.zip"
    $gamePluginDir = Join-Path $stage "plugins\DesktopBuddy"
    $bridgeTarget = Join-Path $stage "Renderer\BepInEx\plugins\DesktopBuddySharedTextureBridge"

    Add-PackageMetadata -Stage $stage -PackageName $mainName -PackageVersion $version -Description $description -Dependencies $dependencies -Icon (Join-Path $Root "icon.png")
    New-Item -ItemType Directory -Force -Path $gamePluginDir, $bridgeTarget | Out-Null
    Copy-Item -LiteralPath (Join-Path $modOutDir "DesktopBuddy.dll") -Destination (Join-Path $gamePluginDir "DesktopBuddy.dll")
    Copy-Item -LiteralPath (Join-Path $Root "icon_transparent.png") -Destination (Join-Path $gamePluginDir "icon_transparent.png")
    Copy-Item -LiteralPath (Join-Path $Root "scripts\CollectDesktopBuddyDiagnostics.ps1") -Destination (Join-Path $gamePluginDir "CollectDesktopBuddyDiagnostics.ps1")
    $modSha = Join-Path $modOutDir "DesktopBuddy.sha"
    if (Test-Path -LiteralPath $modSha) {
        Copy-Item -LiteralPath $modSha -Destination (Join-Path $gamePluginDir "DesktopBuddy.sha")
    }
    Copy-Item -LiteralPath $bridgeDll -Destination (Join-Path $bridgeTarget "DesktopBuddySharedTextureBridge.dll")

    New-ZipFromStage -Stage $stage -OutZip $outZip
    Remove-Item -LiteralPath $stage -Recurse -Force
    Write-Host "Done: $outZip (Thunderstore main package)"
}

function Build-RuntimeThunderstorePackage {
    $modOutDir = Get-DesktopBuddyModOutput -ConfigurationName $Configuration
    $runtimeSource = Join-Path $Root $runtimeDirName
    $namespace = Get-TomlString $toml "namespace"
    $description = "Runtime payloads for DesktopBuddy, including FFmpeg, tunnel, virtual camera, and virtual audio setup files."
    $runtimeIcon = Join-Path $Root "icon_runtime.png"

    foreach ($path in @($runtimeSource, (Join-Path $Root "icon.png"), (Join-Path $Root "README_THUNDERSTORE.md"))) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required runtime package input not found: $path"
        }
    }

    New-GrayscalePng -Source (Join-Path $Root "icon.png") -Destination $runtimeIcon

    $name = if ([string]::IsNullOrWhiteSpace($ZipName)) { "$namespace-$runtimePackageName-$runtimeVersion" } else { $ZipName }
    $stage = New-PackageStage -Name $name
    $outDir = Join-Path $Root "build"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $outZip = Join-Path $outDir "$name.zip"
    $runtimeTarget = Join-Path $stage "plugins\DesktopBuddy\$runtimeDirName"

    Add-PackageMetadata -Stage $stage -PackageName $runtimePackageName -PackageVersion $runtimeVersion -Description $description -Dependencies @() -Icon $runtimeIcon
    New-Item -ItemType Directory -Force -Path $runtimeTarget | Out-Null
    Copy-Item -Path (Join-Path $runtimeSource "*") -Destination $runtimeTarget -Recurse -Force
    Copy-ManagedRuntimeDependencies -ModOutDir $modOutDir -RuntimeTarget $runtimeTarget

    New-ZipFromStage -Stage $stage -OutZip $outZip
    Remove-Item -LiteralPath $stage -Recurse -Force
    Write-Host "Done: $outZip (Thunderstore runtime package)"
}

Assert-SemVer -Name "VERSION" -Value $version
Assert-SemVer -Name "RUNTIME_VERSION" -Value $runtimeVersion

$tomlVersion = Get-TomlString $toml "versionNumber"
if ($tomlVersion -ne $version) {
    throw "VERSION ($version) does not match thunderstore.toml versionNumber ($tomlVersion). Run scripts\sync-version.ps1."
}

Update-SetupPayloadManifest -RuntimeSource (Join-Path $Root $runtimeDirName)

switch ($Package) {
    "Manual" { Build-ManualPackage }
    "Main" { Build-MainThunderstorePackage }
    "Runtime" { Build-RuntimeThunderstorePackage }
    "All" {
        Build-RuntimeThunderstorePackage
        Build-MainThunderstorePackage
    }
}
