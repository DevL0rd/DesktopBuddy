param(
    [Alias("r")]
    [switch]$Restart,

    [Alias("d")]
    [switch]$Desktop,

    [switch]$NoDeploy,

    [string]$ProfileName = "Default",

    [string]$ProfilePath,

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$NoDeployResonitePath = "C:\__DesktopBuddyNoDeploy__"
$dotnetBuildArgs = @()
if ($NoDeploy -or $env:GITHUB_ACTIONS -eq "true") {
    $dotnetBuildArgs += "/p:ResonitePath=$NoDeployResonitePath"
}

function Resolve-LocalProfilePath {
    param(
        [string]$RequestedProfilePath,
        [string]$RequestedProfileName,
        [bool]$RequireNamedProfile
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedProfilePath)) {
        $resolved = (Resolve-Path -LiteralPath $RequestedProfilePath -ErrorAction Stop).Path
        return $resolved
    }

    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($RequestedProfileName)) {
        $RequestedProfileName = "Default"
    }

    $galeProfile = Join-Path $env:APPDATA (Join-Path "com.kesomannen.gale\resonite\profiles" $RequestedProfileName)
    if (Test-Path -LiteralPath (Join-Path $galeProfile "BepInEx\plugins")) {
        return $galeProfile
    }

    if ($RequireNamedProfile) {
        throw "Gale Resonite profile '$RequestedProfileName' was not found or does not contain BepInEx\plugins: $galeProfile"
    }

    return $null
}

function Get-DesktopBuddyModOutput {
    param([Parameter(Mandatory)][string]$ConfigurationName)

    $output = Get-ChildItem -LiteralPath (Join-Path $Root "DesktopBuddy\bin\$ConfigurationName") -Directory -Filter "net10.0-windows*" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "DesktopBuddy.dll") } |
        Select-Object -First 1

    if ($null -eq $output) {
        throw "DesktopBuddy.dll not found under DesktopBuddy\bin\$ConfigurationName\net10.0-windows*."
    }

    return $output.FullName
}

function Copy-DeployFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        $sourceItem = Get-Item -LiteralPath $Source
        $destinationItem = Get-Item -LiteralPath $Destination
        if ($sourceItem.Length -eq $destinationItem.Length -and
            $sourceItem.LastWriteTimeUtc -eq $destinationItem.LastWriteTimeUtc) {
            return
        }
    }

    try {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
    catch {
        Write-Warning "Could not update deployed file '$Destination': $($_.Exception.Message)"
    }
}

function Update-SetupPayloadManifest {
    $runtimeSource = Join-Path $Root "DesktopBuddyRuntime"
    $manifest = Join-Path $runtimeSource "DesktopBuddySetupPayloads.md5"
    $payloads = @(
        "softcam64.dll",
        "softcam.dll",
        "VBCABLE_Setup_x64.exe"
    )

    $lines = foreach ($payload in $payloads) {
        $path = Join-Path $runtimeSource $payload
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Setup payload not found: DesktopBuddyRuntime\$payload"
        }

        "$payload=$((Get-FileHash -Algorithm MD5 -LiteralPath $path).Hash.ToLowerInvariant())"
    }

    Set-Content -LiteralPath $manifest -Value $lines
}

function Copy-DesktopBuddyProfileDeploy {
    param(
        [Parameter(Mandatory)][string]$ResolvedProfilePath,
        [Parameter(Mandatory)][string]$ConfigurationName
    )

    $modOutDir = Get-DesktopBuddyModOutput -ConfigurationName $ConfigurationName
    $bridgeDll = Join-Path $Root "DesktopBuddySharedTextureBridge\bin\$ConfigurationName\net472\DesktopBuddySharedTextureBridge.dll"
    $runtimeSource = Join-Path $Root "DesktopBuddyRuntime"
    $pluginsRoot = Join-Path $ResolvedProfilePath "BepInEx\plugins"
    $gamePluginDir = Join-Path $pluginsRoot "DevL0rd-DesktopBuddy\DesktopBuddy"
    $runtimeTarget = Join-Path $pluginsRoot "DevL0rd-DesktopBuddyRuntime\DesktopBuddy\DesktopBuddyRuntime"
    $bridgeTarget = Join-Path $ResolvedProfilePath "Renderer\BepInEx\plugins\DevL0rd-DesktopBuddy\DesktopBuddySharedTextureBridge"
    $oldBridgeTarget = Join-Path $ResolvedProfilePath "Renderer\BepInEx\plugins\DesktopBuddySharedTextureBridge"
    $oldGamePluginDir = Join-Path $pluginsRoot "DesktopBuddy"
    $gameCache = Join-Path $ResolvedProfilePath "BepInEx\cache\chainloader_typeloader.dat"
    $rendererCache = Join-Path $ResolvedProfilePath "Renderer\BepInEx\cache\chainloader_typeloader.dat"

    foreach ($path in @(
        (Join-Path $modOutDir "DesktopBuddy.dll"),
        (Join-Path $modOutDir "icon_transparent.png"),
        (Join-Path $Root "scripts\CollectDesktopBuddyDiagnostics.ps1"),
        $bridgeDll,
        $runtimeSource
    )) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required deploy input not found: $path"
        }
    }

    foreach ($file in @(
        "FFmpeg.AutoGen.dll",
        "Microsoft.Windows.SDK.NET.dll",
        "WinRT.Runtime.dll"
    )) {
        $path = Join-Path $modOutDir $file
        if (-not (Test-Path -LiteralPath $path)) {
            throw "DesktopBuddy build dependency $file not found under $modOutDir."
        }
    }

    Write-Host "Deploying DesktopBuddy to BepInEx profile: $ResolvedProfilePath"
    if (Test-Path -LiteralPath $oldGamePluginDir) {
        try {
            Remove-Item -LiteralPath $oldGamePluginDir -Recurse -Force
            Write-Host "Removed old DesktopBuddy deploy folder: $oldGamePluginDir"
        }
        catch {
            Write-Warning "Could not remove old DesktopBuddy deploy folder '$oldGamePluginDir': $($_.Exception.Message)"
        }
    }
    if (Test-Path -LiteralPath $oldBridgeTarget) {
        try {
            Remove-Item -LiteralPath $oldBridgeTarget -Recurse -Force
            Write-Host "Removed old DesktopBuddy renderer bridge folder: $oldBridgeTarget"
        }
        catch {
            Write-Warning "Could not remove old DesktopBuddy renderer bridge folder '$oldBridgeTarget': $($_.Exception.Message)"
        }
    }

    New-Item -ItemType Directory -Force -Path $gamePluginDir, $runtimeTarget, $bridgeTarget | Out-Null

    Copy-Item -LiteralPath (Join-Path $modOutDir "DesktopBuddy.dll") -Destination (Join-Path $gamePluginDir "DesktopBuddy.dll") -Force
    Copy-DeployFile -Source (Join-Path $modOutDir "icon_transparent.png") -Destination (Join-Path $gamePluginDir "icon_transparent.png")
    Copy-DeployFile -Source (Join-Path $Root "scripts\CollectDesktopBuddyDiagnostics.ps1") -Destination (Join-Path $gamePluginDir "CollectDesktopBuddyDiagnostics.ps1")
    $modSha = Join-Path $modOutDir "DesktopBuddy.sha"
    if (Test-Path -LiteralPath $modSha) {
        Copy-Item -LiteralPath $modSha -Destination (Join-Path $gamePluginDir "DesktopBuddy.sha") -Force
    }

    foreach ($file in Get-ChildItem -LiteralPath $runtimeSource -File) {
        Copy-DeployFile -Source $file.FullName -Destination (Join-Path $runtimeTarget $file.Name)
    }
    foreach ($file in @(
        "FFmpeg.AutoGen.dll",
        "Microsoft.Windows.SDK.NET.dll",
        "WinRT.Runtime.dll"
    )) {
        Copy-DeployFile -Source (Join-Path $modOutDir $file) -Destination (Join-Path $runtimeTarget $file)
    }

    Copy-DeployFile -Source $bridgeDll -Destination (Join-Path $bridgeTarget "DesktopBuddySharedTextureBridge.dll")

    foreach ($cacheFile in @($gameCache, $rendererCache)) {
        if (Test-Path -LiteralPath $cacheFile) {
            Remove-Item -LiteralPath $cacheFile -Force
        }
    }
}

$localProfilePath = $null
if (-not $NoDeploy -and $env:GITHUB_ACTIONS -ne "true") {
    $localProfilePath = Resolve-LocalProfilePath `
        -RequestedProfilePath $ProfilePath `
        -RequestedProfileName $ProfileName `
        -RequireNamedProfile $PSBoundParameters.ContainsKey("ProfileName")
    if (-not [string]::IsNullOrWhiteSpace($localProfilePath)) {
        $dotnetBuildArgs += "/p:DesktopBuddySkipDeploy=true"
    }
}

function Stop-ProcessTreeByName {
    param([Parameter(Mandatory)][string]$Name)

    $running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Name)) -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    Write-Host "Stopping $Name..."
    foreach ($process in $running) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}

function Get-DesktopBuddyCloudflared {
    @(Get-CimInstance Win32_Process -Filter "name='cloudflared.exe'" -ErrorAction SilentlyContinue | Where-Object {
        ($_.ExecutablePath -like "*\plugins\DevL0rd-DesktopBuddyRuntime\DesktopBuddy\DesktopBuddyRuntime\cloudflared.exe") -or
        ($_.CommandLine -like "*--url http://localhost:48080*")
    })
}

function Stop-DesktopBuddyCloudflared {
    foreach ($process in Get-DesktopBuddyCloudflared) {
        Write-Host "Stopping DesktopBuddy cloudflared.exe PID $($process.ProcessId)"
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Wait-ProcessesStopped {
    param([Parameter(Mandatory)][string[]]$Names)

    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $running = @()
        foreach ($name in $Names) {
            $processName = [IO.Path]::GetFileNameWithoutExtension($name)
            $running += @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
        }

        if ($running.Count -eq 0) {
            return
        }

        foreach ($name in $Names) {
            Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($name)) -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    }

    Write-Warning "Some Resonite processes are still running after forced shutdown."
    foreach ($name in $Names) {
        Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($name)) -ErrorAction SilentlyContinue |
            Select-Object Name, Id, Path |
            Format-List
    }
    throw "FAILED TO STOP RESONITE - not building or restarting"
}

function Wait-DesktopBuddyCloudflaredStopped {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $processes = @(Get-DesktopBuddyCloudflared)
        if ($processes.Count -eq 0) {
            return
        }

        foreach ($process in $processes) {
            Write-Host "Waiting: stopping DesktopBuddy cloudflared.exe PID $($process.ProcessId)"
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    $processes = @(Get-DesktopBuddyCloudflared)
    if ($processes.Count -eq 0) {
        return
    }

    Write-Warning "DesktopBuddy cloudflared.exe is still running after forced shutdown:"
    $processes | Select-Object Name, ProcessId, ParentProcessId, ExecutablePath, CommandLine | Format-List
    throw "FAILED TO STOP DESKTOPBUDDY CLOUDFLARED"
}

function Start-Resonite {
    param(
        [switch]$DesktopMode,
        [string]$ResolvedProfilePath
    )

    $resoniteDir = "C:\Program Files (x86)\Steam\steamapps\common\Resonite"
    $resoniteExe = Join-Path $resoniteDir "Resonite.exe"
    if (-not (Test-Path -LiteralPath $resoniteExe)) {
        throw "Resonite.exe was not found at $resoniteExe"
    }

    $launchArgs = @("--hookfxr-enable")
    if (-not [string]::IsNullOrWhiteSpace($ResolvedProfilePath)) {
        $launchArgs += @(
            "--bepinex-target",
            (Join-Path $ResolvedProfilePath "BepInEx"),
            "--doorstop-enabled",
            "true",
            "--doorstop-target-assembly",
            (Join-Path $ResolvedProfilePath "Renderer\BepInEx\core\BepInEx.Preloader.dll")
        )
    }
    if ($DesktopMode) {
        $launchArgs += "-Screen"
    }

    Start-Process -FilePath $resoniteExe -WorkingDirectory $resoniteDir -ArgumentList $launchArgs
}

if ($Restart) {
    Stop-ProcessTreeByName "Resonite.exe"
    Stop-ProcessTreeByName "Renderite.Host.exe"
    Stop-ProcessTreeByName "Renderite.Renderer.exe"
    Stop-DesktopBuddyCloudflared
    Wait-ProcessesStopped @("Resonite.exe", "Renderite.Host.exe", "Renderite.Renderer.exe")
    Stop-DesktopBuddyCloudflared
    Wait-DesktopBuddyCloudflaredStopped
    Start-Sleep -Seconds 2
}

Push-Location $Root
try {
    Update-SetupPayloadManifest

    dotnet build (Join-Path $Root "DesktopBuddy\DesktopBuddy.csproj") -c $Configuration @dotnetBuildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "MOD BUILD FAILED - not launching Resonite"
    }

    dotnet build (Join-Path $Root "DesktopBuddySharedTextureBridge\DesktopBuddySharedTextureBridge.csproj") -c $Configuration @dotnetBuildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "SHARED TEXTURE BRIDGE BUILD FAILED - not launching Resonite"
    }

    if (-not [string]::IsNullOrWhiteSpace($localProfilePath)) {
        Copy-DesktopBuddyProfileDeploy -ResolvedProfilePath $localProfilePath -ConfigurationName $Configuration
    }
}
finally {
    Pop-Location
}

if ($Restart) {
    if ($Desktop) {
        Write-Host "Starting Resonite in desktop mode..."
        Start-Resonite -DesktopMode -ResolvedProfilePath $localProfilePath
    }
    else {
        Start-Resonite -ResolvedProfilePath $localProfilePath
    }
}
