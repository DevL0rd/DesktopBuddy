param(
    [string]$OutputDirectory = (Join-Path $env:USERPROFILE "Downloads")
)

$ErrorActionPreference = "Continue"

function New-SafeName {
    param([Parameter(Mandatory)][string]$Name)
    return ($Name -replace '[^\w\-. ]', '_')
}

function Add-TextFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()][string]$Content
    )

    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    if ($null -eq $Content) {
        $Content = ""
    }
    Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8
}

function Add-TextLine {
    param(
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()][string]$Content
    )

    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    if ($null -eq $Content) {
        $Content = ""
    }
    Add-Content -LiteralPath $Path -Value $Content -Encoding UTF8
}

function Copy-IfExists {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return $false
    }

    $dir = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    try {
        $inputStream = [System.IO.File]::Open(
            $Source,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        try {
            $outputStream = [System.IO.File]::Open(
                $Destination,
                [System.IO.FileMode]::Create,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            try {
                $inputStream.CopyTo($outputStream)
            }
            finally {
                $outputStream.Dispose()
            }
        }
        finally {
            $inputStream.Dispose()
        }
        return $true
    }
    catch {
        return $false
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return 0
    }

    $count = 0
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force -ErrorAction SilentlyContinue) {
        try {
            $relative = $file.FullName.Substring($Source.TrimEnd('\').Length).TrimStart('\')
            $target = Join-Path $Destination $relative
            if (Copy-IfExists -Source $file.FullName -Destination $target) {
                $count++
            }
        }
        catch {
        }
    }

    return $count
}

function Copy-WerPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$DestinationRoot
    )

    $clean = $Path.Trim()
    if ($clean.StartsWith("\\?\")) {
        $clean = $clean.Substring(4)
    }

    if (-not (Test-Path -LiteralPath $clean -ErrorAction SilentlyContinue)) {
        if ($clean -match '\.(wer|xml|csv|txt)$|\\Microsoft\\Windows\\WER\\|\\LiveKernelReports\\') {
            Add-TextLine -Path (Join-Path $DestinationRoot "inaccessible-or-missing.txt") -Content "Missing or inaccessible: $clean"
        }
        return 0
    }

    $name = New-SafeName -Name (Split-Path -Leaf $clean)
    if ([string]::IsNullOrWhiteSpace($name)) {
        $name = "WER"
    }

    $target = Join-Path $DestinationRoot $name
    $item = Get-Item -LiteralPath $clean -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return 0
    }

    if ($item.PSIsContainer) {
        return Copy-DirectoryContents -Source $item.FullName -Destination $target
    }

    if (Copy-IfExists -Source $item.FullName -Destination (Join-Path $target $item.Name)) {
        return 1
    }

    return 0
}

function Add-WerReports {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [object[]]$Events = @()
    )

    $count = 0
    $matchedNames = @(
        "AppCrash_Renderite*",
        "AppHang_Renderite*",
        "AppCrash_Resonite*",
        "AppHang_Resonite*",
        "AppCrash_DesktopBuddy*",
        "AppHang_DesktopBuddy*"
    )

    $werRoots = @(
        (Join-Path $env:ProgramData "Microsoft\Windows\WER\ReportArchive"),
        (Join-Path $env:ProgramData "Microsoft\Windows\WER\ReportQueue"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\Windows\WER\ReportArchive"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\Windows\WER\ReportQueue")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique

    foreach ($root in $werRoots) {
        foreach ($pattern in $matchedNames) {
            foreach ($dir in Get-ChildItem -LiteralPath $root -Directory -Filter $pattern -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 20) {
                $rootName = New-SafeName -Name (Split-Path -Leaf $root)
                $dest = Join-Path $Destination "$rootName\$($dir.Name)"
                $count += Copy-DirectoryContents -Source $dir.FullName -Destination $dest
            }
        }
    }

    foreach ($event in $Events) {
        if ($null -eq $event -or [string]::IsNullOrWhiteSpace($event.Message)) {
            continue
        }

        foreach ($match in [regex]::Matches($event.Message, '(?m)(\\\\\?\\[^\r\n]+|[A-Z]:\\[^\r\n]+)')) {
            $path = $match.Value.Trim()
            if ($path -match '\\Microsoft\\Windows\\WER\\' -or $path -match '\.(wer|xml|csv|txt)$') {
                $count += Copy-WerPath -Path $path -DestinationRoot (Join-Path $Destination "referenced")
            }
        }
    }

    return $count
}

function Add-RecentFiles {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string[]]$Patterns,
        [Parameter(Mandatory)][string]$Destination,
        [int]$Limit = 10
    )

    if (-not (Test-Path -LiteralPath $Root)) {
        return 0
    }

    $files = foreach ($pattern in $Patterns) {
        Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue
    }

    $count = 0
    foreach ($file in ($files | Sort-Object LastWriteTime -Descending | Select-Object -First $Limit)) {
        $relative = $file.FullName.Substring($Root.TrimEnd('\').Length).TrimStart('\')
        $target = Join-Path $Destination $relative
        if (Copy-IfExists -Source $file.FullName -Destination $target) {
            $count++
        }
    }

    return $count
}

function Find-ProfileRoots {
    param([System.Diagnostics.Process[]]$Processes = @())

    $roots = New-Object System.Collections.Generic.List[string]

    $candidates = @(
        (Join-Path $env:APPDATA "com.kesomannen.gale\resonite\profiles"),
        (Join-Path $env:APPDATA "Thunderstore Mod Manager\DataFolder\Resonite\profiles"),
        (Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\Resonite")
    )

    foreach ($proc in $Processes) {
        try {
            $path = $proc.Path
            if ([string]::IsNullOrWhiteSpace($path)) {
                continue
            }

            $dir = Split-Path -Parent $path
            if ((Split-Path -Leaf $dir) -eq "Renderer") {
                $dir = Split-Path -Parent $dir
            }

            $candidates += $dir
        }
        catch {
        }
    }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        if (Test-Path -LiteralPath (Join-Path $candidate "BepInEx")) {
            $roots.Add((Resolve-Path -LiteralPath $candidate).Path)
            continue
        }

        foreach ($dir in Get-ChildItem -LiteralPath $candidate -Directory -ErrorAction SilentlyContinue) {
            if (Test-Path -LiteralPath (Join-Path $dir.FullName "BepInEx")) {
                $roots.Add($dir.FullName)
            }
        }
    }

    return $roots | Sort-Object -Unique
}

function Get-FileHashLine {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return "MISSING $Path"
    }

    try {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $Path
        return "$($hash.Hash)  $Path"
    }
    catch {
        return "HASH_FAILED $Path : $($_.Exception.Message)"
    }
}

function Add-ProcessDetails {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [System.Diagnostics.Process[]]$Processes = @()
    )

    if ($null -eq $Processes -or $Processes.Count -eq 0) {
        Add-TextFile -Path (Join-Path $Destination "live-processes.txt") -Content "No target processes were running when diagnostics were collected."
        return
    }

    foreach ($proc in $Processes) {
        $safe = "{0}_{1}" -f (New-SafeName -Name $proc.ProcessName), $proc.Id
        $procDir = Join-Path $Destination $safe
        try {
            $proc.Refresh()
            $details = [ordered]@{
                ProcessName = $proc.ProcessName
                Id = $proc.Id
                StartTime = Get-SafeValue { $proc.StartTime }
                Path = Get-SafeValue { $proc.Path }
                Responding = Get-SafeValue { $proc.Responding }
                MainWindowTitle = Get-SafeValue { $proc.MainWindowTitle }
                MainWindowHandle = Get-SafeValue { "0x{0:X}" -f $proc.MainWindowHandle.ToInt64() }
                Threads = Get-SafeValue { $proc.Threads.Count }
                Handles = Get-SafeValue { $proc.HandleCount }
                WorkingSet64 = Get-SafeValue { $proc.WorkingSet64 }
                PrivateMemorySize64 = Get-SafeValue { $proc.PrivateMemorySize64 }
                PagedMemorySize64 = Get-SafeValue { $proc.PagedMemorySize64 }
                VirtualMemorySize64 = Get-SafeValue { $proc.VirtualMemorySize64 }
                TotalProcessorTime = Get-SafeValue { $proc.TotalProcessorTime }
            }
            Add-TextFile -Path (Join-Path $procDir "process.txt") -Content (($details.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" }) -join "`r`n")
        }
        catch {
            Add-TextFile -Path (Join-Path $procDir "process.txt") -Content "Process details failed: $($_.Exception.Message)"
        }

        try {
            $threads = foreach ($thread in $proc.Threads) {
                [pscustomobject]@{
                    Id = $thread.Id
                    ThreadState = $thread.ThreadState
                    WaitReason = if ($thread.ThreadState -eq "Wait") { $thread.WaitReason } else { "" }
                    StartAddress = ("0x{0:X}" -f $thread.StartAddress.ToInt64())
                    CurrentPriority = $thread.CurrentPriority
                    TotalProcessorTime = $thread.TotalProcessorTime
                    UserProcessorTime = $thread.UserProcessorTime
                    PrivilegedProcessorTime = $thread.PrivilegedProcessorTime
                }
            }
            Add-TextFile -Path (Join-Path $procDir "threads.txt") -Content ($threads | Sort-Object Id | Format-Table -AutoSize | Out-String -Width 240)
            Add-TextFile -Path (Join-Path $procDir "threads-by-cpu.txt") -Content ($threads | Sort-Object TotalProcessorTime -Descending | Format-Table -AutoSize | Out-String -Width 240)
        }
        catch {
            Add-TextFile -Path (Join-Path $procDir "threads.txt") -Content "Thread snapshot failed: $($_.Exception.Message)"
        }

        try {
            $modules = foreach ($module in $proc.Modules) {
                [pscustomobject]@{
                    ModuleName = $module.ModuleName
                    FileName = $module.FileName
                    BaseAddress = ("0x{0:X}" -f $module.BaseAddress.ToInt64())
                    ModuleMemorySize = $module.ModuleMemorySize
                    FileVersion = $module.FileVersionInfo.FileVersion
                    ProductVersion = $module.FileVersionInfo.ProductVersion
                }
            }
            Add-TextFile -Path (Join-Path $procDir "modules.txt") -Content ($modules | Sort-Object ModuleName | Format-Table -AutoSize | Out-String -Width 360)
        }
        catch {
            Add-TextFile -Path (Join-Path $procDir "modules.txt") -Content "Module snapshot failed: $($_.Exception.Message)"
        }
    }
}

function Export-EventLogSnapshot {
    param(
        [Parameter(Mandatory)][string]$LogName,
        [Parameter(Mandatory)][string]$Destination,
        [int]$Days = 3,
        [string]$TextPattern = ""
    )

    $safeLog = New-SafeName -Name ($LogName -replace '/', '_')
    $txtPath = Join-Path $Destination "$safeLog-recent.txt"
    $evtxPath = Join-Path $Destination "$safeLog.evtx"
    $events = @()

    try {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName = $LogName
            StartTime = (Get-Date).AddDays(-$Days)
        } -ErrorAction Stop |
            Where-Object {
                [string]::IsNullOrWhiteSpace($TextPattern) -or $_.Message -match $TextPattern -or $_.ProviderName -match $TextPattern
            } |
            Select-Object -First 200 TimeCreated, Id, ProviderName, LevelDisplayName, Message)

        Add-TextFile -Path $txtPath -Content ($events | Format-List | Out-String -Width 240)
    }
    catch {
        Add-TextFile -Path $txtPath -Content "Event log query failed for ${LogName}: $($_.Exception.Message)"
    }

    try {
        $dir = Split-Path -Parent $evtxPath
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
        $ms = [int64]([TimeSpan]::FromDays($Days).TotalMilliseconds)
        $query = "*[System[TimeCreated[timediff(@SystemTime) <= $ms]]]"
        & wevtutil epl "$LogName" "$evtxPath" /q:"$query" 2>$null
    }
    catch {
        Add-TextFile -Path (Join-Path $Destination "$safeLog-evtx-export-error.txt") -Content "EVTX export failed for ${LogName}: $($_.Exception.Message)"
    }

    return $events
}

function Add-ReliabilityRecords {
    param([Parameter(Mandatory)][string]$Destination)

    try {
        $records = Get-CimInstance -Namespace root\cimv2 -ClassName Win32_ReliabilityRecords -ErrorAction Stop |
            Where-Object {
                $_.TimeGenerated -gt (Get-Date).AddDays(-7) -and
                ($_.ProductName -match 'DesktopBuddy|Resonite|Renderite|Unity|NVIDIA|Windows' -or $_.Message -match 'DesktopBuddy|Resonite|Renderite|Unity|NVIDIA|Display|Video|Hang|Stopped responding')
            } |
            Sort-Object TimeGenerated -Descending |
            Select-Object -First 200 TimeGenerated, SourceName, ProductName, EventIdentifier, Message

        Add-TextFile -Path (Join-Path $Destination "reliability-records.txt") -Content ($records | Format-List | Out-String -Width 240)
    }
    catch {
        Add-TextFile -Path (Join-Path $Destination "reliability-records.txt") -Content "Reliability query failed: $($_.Exception.Message)"
    }
}

function Add-DxDiag {
    param([Parameter(Mandatory)][string]$Destination)

    try {
        $path = Join-Path $Destination "dxdiag.txt"
        $dir = Split-Path -Parent $path
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }

        $dxdiag = Join-Path $env:WINDIR "System32\dxdiag.exe"
        if (Test-Path -LiteralPath $dxdiag) {
            $args = "/whql:off /t `"$path`""
            $p = Start-Process -FilePath $dxdiag -ArgumentList $args -WindowStyle Hidden -PassThru
            if (-not $p.WaitForExit(30000)) {
                try { $p.Kill() } catch { }
                Add-TextFile -Path (Join-Path $Destination "dxdiag-error.txt") -Content "dxdiag timed out after 30 seconds"
            }
        }
        else {
            Add-TextFile -Path (Join-Path $Destination "dxdiag-error.txt") -Content "dxdiag.exe not found"
        }
    }
    catch {
        Add-TextFile -Path (Join-Path $Destination "dxdiag-error.txt") -Content "dxdiag failed: $($_.Exception.Message)"
    }
}

function Get-SafeValue {
    param([Parameter(Mandatory)][scriptblock]$Expression)

    try {
        return & $Expression
    }
    catch {
        return $null
    }
}

function Add-UnityAndCrashLogs {
    param([Parameter(Mandatory)][string]$Destination)

    $roots = @(
        (Join-Path $env:LOCALAPPDATA "Temp"),
        (Join-Path $env:USERPROFILE "AppData\LocalLow")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique

    foreach ($root in $roots) {
        $files = Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                $_.FullName -notmatch '\\DesktopBuddyDiagnostics\\' -and
                $_.LastWriteTime -gt (Get-Date).AddDays(-7) -and
                $_.Length -lt 200MB -and
                (
                    $_.Name -match 'Player.*\.log|output_log\.txt|error\.log|crash.*\.(log|txt|json|xml)' -or
                    $_.DirectoryName -match 'Crashes|CrashReports|Renderite\.Renderer'
                ) -and
                $_.FullName -notmatch '\\Assets\\|\\Cache\\'
            } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 50

        foreach ($file in $files) {
            $rootName = New-SafeName -Name (Split-Path -Leaf $root)
            $relative = $file.FullName.Substring($root.TrimEnd('\').Length).TrimStart('\')
            Copy-IfExists -Source $file.FullName -Destination (Join-Path $Destination "$rootName\$relative") | Out-Null
        }
    }
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}

$stamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$safeMachine = New-SafeName -Name $env:COMPUTERNAME
$stage = Join-Path $env:TEMP "DesktopBuddyDiagnostics\$safeMachine-$stamp"
$zip = Join-Path $OutputDirectory "DesktopBuddy_Diagnostics_${safeMachine}_${stamp}.zip"
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("DesktopBuddy diagnostics")
$summary.Add("Machine: $env:COMPUTERNAME")
$summary.Add("Time: $(Get-Date -Format o)")
$summary.Add("User: $env:USERNAME")
$summary.Add("")

try {
    $os = Get-CimInstance Win32_OperatingSystem
    $summary.Add("OS: $($os.Caption) $($os.Version) build $($os.BuildNumber)")
}
catch {
    $summary.Add("OS: failed: $($_.Exception.Message)")
}

try {
    $gpus = Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, DriverDate, PNPDeviceID
    Add-TextFile -Path (Join-Path $stage "system\gpu.txt") -Content ($gpus | Format-List | Out-String)
}
catch {
    Add-TextFile -Path (Join-Path $stage "system\gpu.txt") -Content "GPU query failed: $($_.Exception.Message)"
}

try {
    $targetProcesses = @(Get-Process Resonite, Renderite.Host, Renderite.Renderer -ErrorAction SilentlyContinue)
    $processes = $targetProcesses | Select-Object ProcessName, Id, StartTime, Path
    Add-TextFile -Path (Join-Path $stage "system\processes.txt") -Content ($processes | Format-List | Out-String)
    Add-ProcessDetails -Destination (Join-Path $stage "processes") -Processes $targetProcesses
    $summary.Add("Live target processes found: $($targetProcesses.Count)")
}
catch {
    Add-TextFile -Path (Join-Path $stage "system\processes.txt") -Content "Process query failed: $($_.Exception.Message)"
    $targetProcesses = @()
}

try {
    Add-DxDiag -Destination (Join-Path $stage "system")
}
catch {
    Add-TextFile -Path (Join-Path $stage "system\dxdiag-error.txt") -Content "dxdiag wrapper failed: $($_.Exception.Message)"
}

try {
    Add-ReliabilityRecords -Destination (Join-Path $stage "events")
}
catch {
    Add-TextFile -Path (Join-Path $stage "events\reliability-records.txt") -Content "Reliability wrapper failed: $($_.Exception.Message)"
}

try {
    Add-UnityAndCrashLogs -Destination (Join-Path $stage "unity-and-crash-logs")
}
catch {
    Add-TextFile -Path (Join-Path $stage "unity-and-crash-logs\collection-error.txt") -Content "Unity/crash log collection failed: $($_.Exception.Message)"
}

$profiles = @(Find-ProfileRoots -Processes $targetProcesses)
$summary.Add("Profiles found: $($profiles.Count)")
foreach ($profile in $profiles) {
    $profileName = New-SafeName -Name (Split-Path -Leaf $profile)
    if ([string]::IsNullOrWhiteSpace($profileName)) {
        $profileName = "SteamInstall"
    }

    $profileDest = Join-Path $stage "profiles\$profileName"
    $summary.Add("Profile: $profile")

    Add-RecentFiles -Root $profile -Patterns @("LogOutput.log") -Destination $profileDest -Limit 20 | Out-Null
    Add-RecentFiles -Root $profile -Patterns @("DesktopBuddy_*.log", "*.log") -Destination (Join-Path $profileDest "recent-logs") -Limit 20 | Out-Null
    Add-RecentFiles -Root $profile -Patterns @("manifest.json", "*.sha") -Destination (Join-Path $profileDest "metadata") -Limit 50 | Out-Null

    $dlls = @(
        "BepInEx\plugins\DevL0rd-DesktopBuddy\DesktopBuddy\DesktopBuddy.dll",
        "BepInEx\plugins\DesktopBuddy\DesktopBuddy.dll",
        "Renderer\BepInEx\plugins\DevL0rd-DesktopBuddy\DesktopBuddySharedTextureBridge\DesktopBuddySharedTextureBridge.dll",
        "Renderer\BepInEx\plugins\DesktopBuddySharedTextureBridge\DesktopBuddySharedTextureBridge.dll"
    )

    $hashLines = foreach ($dll in $dlls) {
        Get-FileHashLine -Path (Join-Path $profile $dll)
    }
    Add-TextFile -Path (Join-Path $profileDest "desktopbuddy-dll-hashes.txt") -Content ($hashLines -join "`r`n")

    $pluginFiles = Get-ChildItem -LiteralPath $profile -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -match 'DesktopBuddy|SharedTextureBridge|InterprocessLib|RenderiteHook|BepInExRenderer|BepisLoader'
        } |
        Select-Object FullName, Length, LastWriteTime
    Add-TextFile -Path (Join-Path $profileDest "desktopbuddy-related-files.txt") -Content ($pluginFiles | Format-Table -AutoSize | Out-String -Width 240)
}

try {
    $eventPattern = 'DesktopBuddy|Resonite|Renderite|UnityPlayer|nvwgf2umx|nvlddmkm|NVIDIA|Display|d3d11|dxgi|DWM|Application Hang|Application Error|LiveKernelEvent|WHEA|TDR|stopped responding'
    $events = @(Export-EventLogSnapshot -LogName "Application" -Destination (Join-Path $stage "events") -Days 3 -TextPattern $eventPattern)
    Export-EventLogSnapshot -LogName "System" -Destination (Join-Path $stage "events") -Days 3 -TextPattern $eventPattern | Out-Null

    $optionalLogs = @(
        "Microsoft-Windows-DxgKrnl/Operational",
        "Microsoft-Windows-Dwm-Core/Operational",
        "Microsoft-Windows-WER-Diag/Operational",
        "Microsoft-Windows-WER-SystemErrorReporting/Operational",
        "Microsoft-Windows-Diagnostics-Performance/Operational"
    )

    $availableLogs = @(wevtutil el 2>$null)
    foreach ($logName in $optionalLogs) {
        if ($availableLogs -contains $logName) {
            Export-EventLogSnapshot -LogName $logName -Destination (Join-Path $stage "events") -Days 3 -TextPattern $eventPattern | Out-Null
        }
        else {
            Add-TextFile -Path (Join-Path $stage "events\$((New-SafeName -Name ($logName -replace '/', '_')))-missing.txt") -Content "Event log channel not available: $logName"
        }
    }
}
catch {
    Add-TextFile -Path (Join-Path $stage "events\application-recent.txt") -Content "Event log query failed: $($_.Exception.Message)"
    $events = @()
}

try {
    $werCount = Add-WerReports -Destination (Join-Path $stage "wer") -Events $events
    $summary.Add("WER reports copied: $werCount")
}
catch {
    $summary.Add("WER collection failed: $($_.Exception.Message)")
}

Add-TextFile -Path (Join-Path $stage "summary.txt") -Content ($summary -join "`r`n")

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $stageRoot = $stage.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $entryName = $file.FullName.Substring($stageRoot.Length).Replace('\', '/')
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

Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host "DesktopBuddy diagnostics written to:"
Write-Host $zip
