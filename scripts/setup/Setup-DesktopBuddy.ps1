param(
    [string]$ResonitePath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [switch]$DryRun,
    [switch]$SkipRendererDeps
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$SoftCamClsid = "{AEF3B972-5FA5-4647-9571-358EB472BC9E}"
$SavedPathKey = "HKCU:\Software\DesktopBuddy"
$SavedPathValue = "ManagerPath"
$RenderiteHookReleasesApi = "https://api.github.com/repos/ResoniteModding/RenderiteHook/releases/latest"
$BepInExRendererReleasesApi = "https://api.github.com/repos/ResoniteModding/BepInEx.Renderer/releases/latest"

function Write-Log {
    param([string]$Message)
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message
    Write-Host $line
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-ResoniteRoot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath (Join-Path $Path "Resonite.exe"))) {
        throw "Resonite.exe was not found in '$Path'. Run this script from the extracted DesktopBuddy package, or pass -ResonitePath."
    }
}

function Invoke-SetupProcess {
    param(
        [string]$FileName,
        [string]$Arguments,
        [string]$WorkingDirectory = $ResonitePath,
        [int]$TimeoutMs = 60000,
        [switch]$UseShellExecute
    )

    if ($DryRun) {
        Write-Log "DRY RUN: $FileName $Arguments"
        return 0
    }

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FileName
    $startInfo.Arguments = $Arguments
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = [bool]$UseShellExecute
    $startInfo.CreateNoWindow = -not $UseShellExecute

    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start $FileName"
    }

    if (-not $process.WaitForExit($TimeoutMs)) {
        try { $process.Kill() } catch { }
        throw "$FileName timed out"
    }
    return $process.ExitCode
}

function Test-VBCableInstalled {
    return Test-Path -LiteralPath "HKLM:\Software\VB-Audio\Cable"
}

function Test-SoftCamRegistered {
    param([string]$Path)
    $keyPath = "Registry::HKEY_CLASSES_ROOT\CLSID\$SoftCamClsid\InprocServer32"
    if (-not (Test-Path -LiteralPath $keyPath)) {
        return $false
    }

    $registered = (Get-Item -LiteralPath $keyPath).GetValue("")
    if ([string]::IsNullOrWhiteSpace($registered)) {
        return $true
    }

    $expected = Join-Path $Path "rml_libs\softcam64.dll"
    return [string]::Equals($registered.Trim('"'), $expected, [StringComparison]::OrdinalIgnoreCase)
}

function Register-SoftCam {
    param([string]$Path)
    if (Test-SoftCamRegistered $Path) {
        Write-Log "SoftCam: already registered at the expected path"
        return
    }

    Write-Log "Registering SoftCam DirectShow filter..."
    $found = $false
    foreach ($dll in @("softcam64.dll", "softcam.dll")) {
        $dllPath = Join-Path $Path "rml_libs\$dll"
        Write-Log "  checking: $dllPath exists=$(Test-Path -LiteralPath $dllPath)"
        if (-not (Test-Path -LiteralPath $dllPath)) {
            continue
        }

        $found = $true
        $exit = Invoke-SetupProcess -FileName "regsvr32.exe" -Arguments "/s `"$dllPath`"" -TimeoutMs 10000
        Write-Log "  regsvr32 $dll exit: $exit"
    }

    if (-not $found) {
        Write-Log "  SoftCam DLL not found in rml_libs"
    }
}

function Install-VBCable {
    param([string]$Path)
    if (Test-VBCableInstalled) {
        Write-Log "VB-Cable: already installed"
        return
    }

    $installer = Join-Path $Path "vbcable\VBCABLE_Setup_x64.exe"
    if (-not (Test-Path -LiteralPath $installer)) {
        Write-Log "VB-Cable installer not found: $installer"
        return
    }

    Write-Log "Installing VB-Cable..."
    $exit = Invoke-SetupProcess -FileName $installer -Arguments "-i -h" -WorkingDirectory (Split-Path -Parent $installer) -TimeoutMs 60000 -UseShellExecute
    Write-Log "VB-Cable installer exit: $exit"
    Write-Log ($(if (Test-VBCableInstalled) { "VB-Cable detected" } else { "VB-Cable not detected yet; reboot may be required" }))
}

function Restart-ServiceQuiet {
    param([string]$Name)
    Write-Log "Restarting $Name..."
    [void](Invoke-SetupProcess -FileName "net.exe" -Arguments "stop `"$Name`" /yes" -TimeoutMs 15000)
    [void](Invoke-SetupProcess -FileName "net.exe" -Arguments "start `"$Name`"" -TimeoutMs 15000)
}

function Configure-VBCableLoopback {
    $keyPath = "HKLM:\Software\VB-Audio\Cable"
    if (-not (Test-Path -LiteralPath $keyPath)) {
        Write-Log "VB-Cable registry key not present yet"
        return
    }

    $current = (Get-ItemProperty -LiteralPath $keyPath -Name "VBAudioCableWDM_LoopBack" -ErrorAction SilentlyContinue).VBAudioCableWDM_LoopBack
    if ($current -eq 0) {
        Write-Log "VB-Cable loopback already disabled"
        return
    }

    if ($DryRun) {
        Write-Log "DRY RUN: set VB-Cable loopback registry value to 0 and restart audio services"
        return
    }

    New-ItemProperty -LiteralPath $keyPath -Name "VBAudioCableWDM_LoopBack" -PropertyType DWord -Value 0 -Force | Out-Null
    Write-Log "VB-Cable loopback disabled"
    Restart-ServiceQuiet "AudioEndpointBuilder"
    Restart-ServiceQuiet "AudioSrv"
}

function Configure-UrlAcl {
    $url = "http://+:48080/"
    $sddl = "D:(A;;GX;;;S-1-1-0)"
    Write-Log "Configuring HTTP URL ACL for stream server..."

    $existing = ""
    try {
        $existing = & netsh http show urlacl url=$url 2>$null | Out-String
    } catch {
        $existing = ""
    }

    if ($existing -match "48080") {
        Write-Log "  HTTP URL ACL already configured"
        return
    }

    $exit = Invoke-SetupProcess -FileName "netsh" -Arguments "http add urlacl url=$url sddl=$sddl" -TimeoutMs 10000
    if ($exit -eq 0) {
        Write-Log "  HTTP URL ACL added"
    } else {
        Write-Log "  netsh urlacl exit: $exit"
    }
}

function Get-LatestReleaseZipUrl {
    param([string]$ApiUrl)
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $release = Invoke-RestMethod -Uri $ApiUrl -Headers @{ "User-Agent" = "DesktopBuddySetup" }
    $asset = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
    if ($null -eq $asset) {
        return $null
    }
    return $asset.browser_download_url
}

function Download-File {
    param([string]$Url, [string]$Destination)
    Write-Log "  downloading: $Url"
    if ($DryRun) {
        Write-Log "DRY RUN: would download to $Destination"
        return
    }
    Invoke-WebRequest -Uri $Url -OutFile $Destination -Headers @{ "User-Agent" = "DesktopBuddySetup" }
}

function Install-RenderiteHook {
    param([string]$Path)
    $renderiteHookPath = Join-Path $Path "rml_mods\RenderiteHook.dll"
    if (Test-Path -LiteralPath $renderiteHookPath) {
        Write-Log "RenderiteHook: already installed ($renderiteHookPath)"
        return
    }

    Write-Log "RenderiteHook: not found; downloading latest release..."
    $zipUrl = Get-LatestReleaseZipUrl $RenderiteHookReleasesApi
    if ([string]::IsNullOrWhiteSpace($zipUrl)) {
        Write-Log "RenderiteHook: no release zip asset found"
        return
    }

    $tempFile = [IO.Path]::GetTempFileName()
    try {
        Download-File -Url $zipUrl -Destination $tempFile
        if ($DryRun) {
            Write-Log "DRY RUN: would extract RenderiteHook into rml_mods and Renderer"
            return
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($tempFile)
        try {
            foreach ($entry in $archive.Entries) {
                if ([string]::IsNullOrEmpty($entry.Name)) {
                    continue
                }

                $parts = $entry.FullName.Replace("\", "/").Split("/")
                if ($parts.Length -lt 3 -or $parts[0] -ne "plugins") {
                    continue
                }

                $destPath = $null
                if ($parts.Length -ge 4 -and [string]::Equals($parts[2], "Doorstop", [StringComparison]::OrdinalIgnoreCase)) {
                    $destPath = Join-Path (Join-Path $Path "Renderer") $entry.Name
                } elseif ($parts.Length -eq 3) {
                    $destPath = Join-Path (Join-Path $Path "rml_mods") $entry.Name
                }

                if ($null -eq $destPath) {
                    continue
                }

                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destPath) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destPath, $true)
                Write-Log "  $($entry.FullName) -> $destPath"
            }
        } finally {
            $archive.Dispose()
        }
        Write-Log "RenderiteHook: installed"
    } finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

function Install-BepInExRenderer {
    param([string]$Path)
    $bepInExCorePath = Join-Path $Path "Renderer\BepInEx\core"
    if (Test-Path -LiteralPath $bepInExCorePath) {
        Write-Log "BepInEx.Renderer: already installed ($bepInExCorePath)"
        return
    }

    Write-Log "BepInEx.Renderer: not found; downloading latest release..."
    $zipUrl = Get-LatestReleaseZipUrl $BepInExRendererReleasesApi
    if ([string]::IsNullOrWhiteSpace($zipUrl)) {
        Write-Log "BepInEx.Renderer: no release zip asset found"
        return
    }

    $tempFile = [IO.Path]::GetTempFileName()
    try {
        Download-File -Url $zipUrl -Destination $tempFile
        if ($DryRun) {
            Write-Log "DRY RUN: would extract BepInEx.Renderer into Renderer"
            return
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($tempFile)
        try {
            $zipPrefix = "BepInExPack/Renderer/"
            foreach ($entry in $archive.Entries) {
                if ([string]::IsNullOrEmpty($entry.Name)) {
                    continue
                }

                $normalizedPath = $entry.FullName.Replace("\", "/")
                if (-not $normalizedPath.StartsWith($zipPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $relativePath = $normalizedPath.Substring($zipPrefix.Length)
                $destPath = Join-Path (Join-Path $Path "Renderer") ($relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destPath) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destPath, $true)
                Write-Log "  $relativePath"
            }
        } finally {
            $archive.Dispose()
        }

        New-Item -ItemType Directory -Force -Path (Join-Path $Path "Renderer\BepInEx\plugins") | Out-Null
        Write-Log "BepInEx.Renderer: installed"
    } finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

function Install-RendererDependencies {
    param([string]$Path)
    if ($SkipRendererDeps) {
        Write-Log "Renderer dependency install skipped"
        return
    }

    Install-RenderiteHook $Path
    Install-BepInExRenderer $Path

    $pluginPath = Join-Path $Path "Renderer\BepInEx\plugins\DesktopBuddyRenderer.dll"
    if (Test-Path -LiteralPath $pluginPath) {
        Write-Log "DesktopBuddyRenderer: present at $pluginPath"
    } else {
        Write-Log "DesktopBuddyRenderer: missing at $pluginPath"
    }
}

function Save-ResonitePath {
    param([string]$Path)
    if ($DryRun) {
        Write-Log "DRY RUN: would save Resonite path to HKCU:\Software\DesktopBuddy"
        return
    }
    New-Item -Path $SavedPathKey -Force | Out-Null
    Set-ItemProperty -Path $SavedPathKey -Name $SavedPathValue -Value $Path
}

$ResonitePath = (Resolve-Path -LiteralPath $ResonitePath).Path

Write-Log "================================"
Write-Log "DesktopBuddy setup"
Write-Log "Target: $ResonitePath"
Write-Log "DryRun: $DryRun"
Write-Log "================================"

Assert-ResoniteRoot $ResonitePath

if (-not (Test-Admin)) {
    Write-Log "WARNING: not running as administrator. Driver, registry, and URL ACL setup may fail."
}

Write-Log "rml_mods exists : $(Test-Path -LiteralPath (Join-Path $ResonitePath "rml_mods"))"
Write-Log "rml_libs exists : $(Test-Path -LiteralPath (Join-Path $ResonitePath "rml_libs"))"
Write-Log "Renderer exists : $(Test-Path -LiteralPath (Join-Path $ResonitePath "Renderer"))"
Write-Log "vbcable exists  : $(Test-Path -LiteralPath (Join-Path $ResonitePath "vbcable"))"
Write-Log "DesktopBuddy.dll: $(Test-Path -LiteralPath (Join-Path $ResonitePath "rml_mods\DesktopBuddy.dll"))"

Register-SoftCam $ResonitePath
Install-VBCable $ResonitePath
Configure-VBCableLoopback
Configure-UrlAcl
Install-RendererDependencies $ResonitePath
Save-ResonitePath $ResonitePath

Write-Log "================================"
Write-Log "Setup complete. A restart may be required for virtual devices."
Write-Log "================================"
