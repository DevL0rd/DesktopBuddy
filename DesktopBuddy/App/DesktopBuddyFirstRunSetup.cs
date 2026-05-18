using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DesktopBuddy;

internal static class DesktopBuddyFirstRunSetup
{
    private const string SoftCamClsid = "{AEF3B972-5FA5-4647-9571-358EB472BC9E}";
    private const string VideoInputCategoryClsid = "{860BB310-5D01-11d0-BD3B-00A0C911CE86}";
    private const string SetupHashFile = "DesktopBuddySetupHashes.txt";
    private const string PackagedSetupHashFile = "DesktopBuddySetupPayloads.md5";

    internal enum SetupAction
    {
        SoftCamRegistration,
        VBCableInstall,
        VBCableLoopback,
        UrlAcl,
    }

    internal sealed class SetupState
    {
        internal IReadOnlyList<SetupItem> Items { get; init; } = Array.Empty<SetupItem>();
        internal IReadOnlyList<SetupAction> RequiredActions { get; init; } = Array.Empty<SetupAction>();
        internal bool HasIssues => Items.Any(item => !item.IsOk);
        internal bool HasRequiredActions => RequiredActions.Count > 0;
    }

    internal sealed class SetupItem
    {
        internal string Name { get; init; }
        internal string Status { get; init; }
        internal string Detail { get; init; }
        internal bool IsOk { get; init; }
        internal bool RequiresAdminAction { get; init; }
        internal SetupAction? Action { get; init; }
    }

    internal static SetupState Check()
    {
        try
        {
            string nativeDir = GetNativeDir();

            Log.Msg("[Setup] Checking DesktopBuddy local setup");
            Log.Msg($"[Setup] Native path: {nativeDir}");

            var items = GetSetupItems(nativeDir);
            return new SetupState
            {
                Items = items,
                RequiredActions = GetRequiredActions(items),
            };
        }
        catch (Exception ex)
        {
            Log.Msg($"[Setup] First-run setup check failed: {ex}");
            return new SetupState
            {
                Items = new[]
                {
                    new SetupItem
                    {
                        Name = "Setup check",
                        Status = "Error",
                        Detail = ex.Message,
                        IsOk = false
                    }
                }
            };
        }
    }

    internal static Process StartElevatedSetup(IReadOnlyCollection<SetupAction> actions = null)
    {
        string nativeDir = GetNativeDir();
        var required = (actions == null || actions.Count == 0)
            ? GetRequiredAdminActions(nativeDir)
            : NormalizeSetupActions(actions);

        if (required.Count == 0)
        {
            Log.Msg("[Setup] No elevated setup actions are required");
            return null;
        }

        Log.Msg("[Setup] User requested admin setup: " + string.Join(", ", required.Select(GetActionLabel)));
        if (IsAdministrator())
        {
            RunAdminSetup(required, nativeDir);
            return null;
        }

        return StartElevatedSetupHelper(required, nativeDir);
    }

    private static List<SetupAction> GetRequiredAdminActions(string nativeDir)
    {
        return GetRequiredActions(GetSetupItems(nativeDir));
    }

    private static List<SetupAction> GetRequiredActions(IReadOnlyList<SetupItem> items)
    {
        return NormalizeSetupActions(items
            .Where(item => item.RequiresAdminAction && item.Action.HasValue)
            .Select(item => item.Action.Value));
    }

    private static List<SetupAction> NormalizeSetupActions(IEnumerable<SetupAction> actions)
    {
        var ordered = actions.Distinct().ToList();
        if (ordered.Contains(SetupAction.VBCableInstall) && !ordered.Contains(SetupAction.VBCableLoopback))
        {
            int installIndex = ordered.IndexOf(SetupAction.VBCableInstall);
            ordered.Insert(installIndex + 1, SetupAction.VBCableLoopback);
        }

        return ordered;
    }

    private static string GetActionLabel(SetupAction action)
    {
        return action switch
        {
            SetupAction.SoftCamRegistration => "SoftCam registration",
            SetupAction.VBCableInstall => "VB-Cable install",
            SetupAction.VBCableLoopback => "VB-Cable loopback disable",
            SetupAction.UrlAcl => "streaming access",
            _ => action.ToString(),
        };
    }

    private static IReadOnlyList<SetupItem> GetSetupItems(string nativeDir)
    {
        var items = new List<SetupItem>
        {
            GetSoftCamSetupItem(nativeDir),
            GetVBCableInstallSetupItem(nativeDir),
            GetVBCableLoopbackSetupItem(),
            GetUrlAclSetupItem(),
        };

        return items;
    }

    private static SetupItem GetSoftCamSetupItem(string nativeDir)
    {
        string expectedName = File.Exists(Path.Combine(nativeDir, "softcam64.dll")) || !File.Exists(Path.Combine(nativeDir, "softcam.dll"))
            ? "softcam64.dll"
            : "softcam.dll";
        string expected = Path.Combine(nativeDir, expectedName);

        string packagedHash = ReadPackagedSetupHash(nativeDir, expectedName);
        string registered = GetSoftCamRegisteredDlls().FirstOrDefault();
        string markerHash = ReadSetupHash(nativeDir, expectedName);
        var runningApps = GetRunningRestartSensitiveProcesses();

        bool dllMissing = !File.Exists(expected);
        bool notRegistered = string.IsNullOrWhiteSpace(registered);
        bool wrongPath = !notRegistered && !PathsEqual(registered, expected);
        bool markerOutdated = !dllMissing &&
                              !string.IsNullOrWhiteSpace(packagedHash) &&
                              !string.Equals(markerHash, packagedHash, StringComparison.OrdinalIgnoreCase);
        bool required = dllMissing || notRegistered || wrongPath || markerOutdated;

        return new SetupItem
        {
            Name = "SoftCam",
            Status = required ? "Not installed" : "Installed",
            Detail = required ? GetSoftCamSetupDetail(runningApps) : "Virtual camera support.",
            IsOk = !required,
            RequiresAdminAction = required && !dllMissing,
            Action = SetupAction.SoftCamRegistration,
        };
    }

    private static string GetSoftCamSetupDetail(IReadOnlyList<string> runningApps)
    {
        if (runningApps == null || runningApps.Count == 0)
            return "Virtual camera support.";

        var shown = runningApps.Take(3).ToArray();
        string suffix = runningApps.Count > shown.Length
            ? $" +{runningApps.Count - shown.Length}"
            : "";
        return "Restart: " + string.Join(", ", shown) + suffix;
    }

    private static SetupItem GetVBCableInstallSetupItem(string nativeDir)
    {
        string installer = Path.Combine(nativeDir, "VBCABLE_Setup_x64.exe");
        string installerHash = ReadPackagedSetupHash(nativeDir, "VBCABLE_Setup_x64.exe");
        string markerHash = ReadSetupHash(nativeDir, "VBCABLE_Setup_x64.exe");
        bool installed = IsVBCableInstalled();
        bool installerMissing = !File.Exists(installer);
        bool markerOutdated = installed &&
                              !string.IsNullOrWhiteSpace(installerHash) &&
                              !string.Equals(markerHash, installerHash, StringComparison.OrdinalIgnoreCase);
        bool required = !installed || markerOutdated;

        string status = required ? "Not installed" : "Installed";
        string detail = "Virtual microphone audio route.";

        return new SetupItem
        {
            Name = "VB-Cable",
            Status = status,
            Detail = detail,
            IsOk = !required,
            RequiresAdminAction = required && !installerMissing,
            Action = SetupAction.VBCableInstall,
        };
    }

    private static SetupItem GetVBCableLoopbackSetupItem()
    {
        bool installed = IsVBCableInstalled();
        bool disabled = IsVBCableLoopbackDisabled();
        return new SetupItem
        {
            Name = "VB-Cable loopback",
            Status = (!installed || disabled) ? "Installed" : "Not installed",
            Detail = "Prevents microphone echo.",
            IsOk = !installed || disabled,
            RequiresAdminAction = installed && !disabled,
            Action = SetupAction.VBCableLoopback,
        };
    }

    private static SetupItem GetUrlAclSetupItem()
    {
        bool configured = IsUrlAclConfigured();
        return new SetupItem
        {
            Name = "Streaming access",
            Status = configured ? "Installed" : "Not installed",
            Detail = "Allows local stream hosting.",
            IsOk = configured,
            RequiresAdminAction = !configured,
            Action = SetupAction.UrlAcl,
        };
    }

    private static void RunAdminSetup(IReadOnlyCollection<SetupAction> actions, string nativeDir)
    {
        Log.Msg("[Setup] Running admin setup inside DesktopBuddy");
        foreach (var action in actions)
        {
            switch (action)
            {
                case SetupAction.SoftCamRegistration:
                    RegisterSoftCam(nativeDir);
                    break;
                case SetupAction.VBCableInstall:
                    InstallVBCable(nativeDir);
                    break;
                case SetupAction.VBCableLoopback:
                    ConfigureVBCableLoopback();
                    break;
                case SetupAction.UrlAcl:
                    ConfigureUrlAcl();
                    break;
            }
        }
        WriteSetupHashes(nativeDir);
        Log.Msg("[Setup] Admin setup complete");
    }

    private static Process StartElevatedSetupHelper(IReadOnlyCollection<SetupAction> actions, string nativeDir)
    {
        string logPath = Path.Combine(nativeDir, "DesktopBuddySetup.log");
        string script = BuildElevatedSetupScript(actions, nativeDir, logPath);
        string scriptPath = Path.Combine(nativeDir, "DesktopBuddyElevatedSetup.ps1");
        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        string args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Log.Msg("[Setup] Requesting administrator permission for user-approved setup");
            var process = Process.Start(startInfo);
            if (process == null)
            {
                Log.Msg("[Setup] Elevated setup helper did not start");
                return null;
            }

            return process;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Msg("[Setup] Administrator setup was cancelled by the user");
        }
        catch (Exception ex)
        {
            Log.Msg($"[Setup] Failed to start elevated setup helper: {ex.Message}");
        }

        return null;
    }

    private static string BuildElevatedSetupScript(IReadOnlyCollection<SetupAction> actions, string nativeDir, string logPath)
    {
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Continue'");
        script.AppendLine($"$native = {PsSingleQuote(nativeDir)}");
        script.AppendLine($"$log = {PsSingleQuote(logPath)}");
        script.AppendLine("New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null");
        script.AppendLine("\"[{0:yyyy-MM-dd HH:mm:ss}] DesktopBuddy elevated setup started\" -f (Get-Date) | Set-Content -LiteralPath $log");
        script.AppendLine("function Write-SetupLog([string]$Message) { Add-Content -LiteralPath $log -Value (\"[{0:HH:mm:ss}] {1}\" -f (Get-Date), $Message) }");
        script.AppendLine("function Run-SetupProcess([string]$File, [string]$Arguments, [string]$WorkingDirectory = $native, [int]$TimeoutMs = 60000) {");
        script.AppendLine("  Write-SetupLog ($File + ' ' + $Arguments)");
        script.AppendLine("  try {");
        script.AppendLine("    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru");
        script.AppendLine("    if (-not $process.WaitForExit($TimeoutMs)) { try { $process.Kill() } catch {}; Write-SetupLog ($File + ' timed out'); return }");
        script.AppendLine("    Write-SetupLog ($File + ' exit=' + $process.ExitCode)");
        script.AppendLine("  } catch { Write-SetupLog ($File + ' failed: ' + $_.Exception.Message) }");
        script.AppendLine("}");
        script.AppendLine($"$softCamClsid = {PsSingleQuote(SoftCamClsid)}");
        script.AppendLine($"$videoInputCategoryClsid = {PsSingleQuote(VideoInputCategoryClsid)}");

        foreach (var action in actions)
        {
            switch (action)
            {
                case SetupAction.SoftCamRegistration:
                    AppendSoftCamRegistrationScript(script);
                    break;
                case SetupAction.VBCableInstall:
                    AppendVBCableInstallScript(script);
                    break;
                case SetupAction.VBCableLoopback:
                    AppendVBCableLoopbackScript(script);
                    break;
                case SetupAction.UrlAcl:
                    AppendUrlAclScript(script);
                    break;
            }
        }

        AppendSetupHashScript(script);
        script.AppendLine("Write-SetupLog 'DesktopBuddy elevated setup finished'");
        return script.ToString();
    }

    private static void AppendSoftCamRegistrationScript(StringBuilder script)
    {
        script.AppendLine("Write-SetupLog 'Registering SoftCam'");
        script.AppendLine("$softCamCandidates = @((Join-Path $native 'softcam64.dll'), (Join-Path $native 'softcam.dll'))");
        script.AppendLine("foreach ($candidate in $softCamCandidates) { if (Test-Path -LiteralPath $candidate) { Run-SetupProcess 'regsvr32.exe' ('/s /u \"' + $candidate + '\"') $native 10000 } }");
        script.AppendLine("$softCamKeys = @(");
        script.AppendLine("  'HKCU:\\Software\\Classes\\CLSID\\' + $softCamClsid,");
        script.AppendLine("  'HKCU:\\Software\\Classes\\WOW6432Node\\CLSID\\' + $softCamClsid,");
        script.AppendLine("  'HKLM:\\Software\\Classes\\CLSID\\' + $softCamClsid,");
        script.AppendLine("  'HKLM:\\Software\\Classes\\WOW6432Node\\CLSID\\' + $softCamClsid,");
        script.AppendLine("  'HKCU:\\Software\\Classes\\CLSID\\' + $videoInputCategoryClsid + '\\Instance\\DesktopBuddy - Camera',");
        script.AppendLine("  'HKCU:\\Software\\Classes\\CLSID\\' + $videoInputCategoryClsid + '\\Instance\\DirectShow Softcam',");
        script.AppendLine("  'HKCU:\\Software\\Classes\\WOW6432Node\\CLSID\\' + $videoInputCategoryClsid + '\\Instance\\DesktopBuddy - Camera',");
        script.AppendLine("  'HKCU:\\Software\\Classes\\WOW6432Node\\CLSID\\' + $videoInputCategoryClsid + '\\Instance\\DirectShow Softcam',");
        script.AppendLine("  'HKLM:\\Software\\Classes\\CLSID\\' + $videoInputCategoryClsid + '\\Instance\\DesktopBuddy - Camera',");
        script.AppendLine("  'HKLM:\\Software\\Classes\\CLSID\\' + $videoInputCategoryClsid + '\\Instance\\DirectShow Softcam',");
        script.AppendLine("  'HKLM:\\Software\\Classes\\WOW6432Node\\CLSID\\' + $videoInputCategoryClsid + '\\Instance\\DesktopBuddy - Camera',");
        script.AppendLine("  'HKLM:\\Software\\Classes\\WOW6432Node\\CLSID\\' + $videoInputCategoryClsid + '\\Instance\\DirectShow Softcam'");
        script.AppendLine(")");
        script.AppendLine("foreach ($key in $softCamKeys) { if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue; Write-SetupLog ('Removed registry key ' + $key) } }");
        script.AppendLine("$softCamDll = Join-Path $native 'softcam64.dll'");
        script.AppendLine("if (-not (Test-Path -LiteralPath $softCamDll)) { $softCamDll = Join-Path $native 'softcam.dll' }");
        script.AppendLine("if (Test-Path -LiteralPath $softCamDll) { Run-SetupProcess 'regsvr32.exe' ('/s \"' + $softCamDll + '\"') $native 10000 } else { Write-SetupLog 'SoftCam DLL missing' }");
    }

    private static void AppendVBCableInstallScript(StringBuilder script)
    {
        script.AppendLine("Write-SetupLog 'Installing VB-Cable'");
        script.AppendLine("$vbCableInstaller = Join-Path $native 'VBCABLE_Setup_x64.exe'");
        script.AppendLine("if (Test-Path -LiteralPath $vbCableInstaller) { Run-SetupProcess $vbCableInstaller '-i -h' $native 60000 } else { Write-SetupLog 'VB-Cable installer missing' }");
    }

    private static void AppendVBCableLoopbackScript(StringBuilder script)
    {
        script.AppendLine("Write-SetupLog 'Disabling VB-Cable loopback'");
        script.AppendLine("$vbCableKey = 'HKLM:\\Software\\VB-Audio\\Cable'");
        script.AppendLine("if (Test-Path -LiteralPath $vbCableKey) {");
        script.AppendLine("  Set-ItemProperty -LiteralPath $vbCableKey -Name 'VBAudioCableWDM_LoopBack' -Type DWord -Value 0");
        script.AppendLine("  Run-SetupProcess 'net.exe' 'stop \"AudioEndpointBuilder\" /yes' $native 15000");
        script.AppendLine("  Run-SetupProcess 'net.exe' 'start \"AudioEndpointBuilder\"' $native 15000");
        script.AppendLine("  Run-SetupProcess 'net.exe' 'stop \"AudioSrv\" /yes' $native 15000");
        script.AppendLine("  Run-SetupProcess 'net.exe' 'start \"AudioSrv\"' $native 15000");
        script.AppendLine("} else { Write-SetupLog 'VB-Cable registry key not present yet' }");
    }

    private static void AppendUrlAclScript(StringBuilder script)
    {
        script.AppendLine("Write-SetupLog 'Configuring HTTP URL ACL'");
        script.AppendLine("Run-SetupProcess 'netsh' 'http add urlacl url=http://+:48080/ sddl=D:(A;;GX;;;S-1-1-0)' $native 10000");
    }

    private static void AppendSetupHashScript(StringBuilder script)
    {
        script.AppendLine("$packagedHashFile = Join-Path $native 'DesktopBuddySetupPayloads.md5'");
        script.AppendLine("$hashFile = Join-Path $native 'DesktopBuddySetupHashes.txt'");
        script.AppendLine("if (Test-Path -LiteralPath $packagedHashFile) { Copy-Item -LiteralPath $packagedHashFile -Destination $hashFile -Force; Write-SetupLog ('Wrote setup hash marker ' + $hashFile) }");
        script.AppendLine("else { Write-SetupLog 'Packaged setup hash manifest missing' }");
    }

    private static string PsSingleQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static void RegisterSoftCam(string nativeDir)
    {
        Log.Msg("[Setup] Registering SoftCam");
        foreach (string dll in GetSoftCamUnregisterCandidates(nativeDir))
        {
            if (File.Exists(dll))
                RunProcess("regsvr32.exe", $"/s /u \"{dll}\"", timeoutMs: 10000);
        }

        foreach (var key in GetSoftCamRegistryTrees())
            DeleteRegistryTree(key.Root, key.SubKey);

        string softcam = Path.Combine(nativeDir, "softcam64.dll");
        if (!File.Exists(softcam))
            softcam = Path.Combine(nativeDir, "softcam.dll");
        if (!File.Exists(softcam))
        {
            Log.Msg($"[Setup] SoftCam DLL missing in {nativeDir}");
            return;
        }

        RunProcess("regsvr32.exe", $"/s \"{softcam}\"", timeoutMs: 10000);
        Log.Msg(IsSoftCamRegistered(nativeDir)
            ? "[Setup] SoftCam registered"
            : "[Setup] WARNING: SoftCam registration did not resolve to expected path");
    }

    private static IEnumerable<string> GetSoftCamUnregisterCandidates(string nativeDir)
    {
        yield return Path.Combine(nativeDir, "softcam64.dll");
        yield return Path.Combine(nativeDir, "softcam.dll");
        foreach (string registered in GetSoftCamRegisteredDlls())
            yield return registered;
    }

    private static IEnumerable<string> GetSoftCamRegisteredDlls()
    {
        foreach (var key in GetSoftCamInprocKeys())
        {
            using var opened = key.Root.OpenSubKey(key.SubKey);
            string value = opened?.GetValue("") as string;
            if (!string.IsNullOrWhiteSpace(value))
                yield return value.Trim('"');
        }
    }

    private static IEnumerable<(RegistryKey Root, string SubKey)> GetSoftCamInprocKeys()
    {
        yield return (Registry.ClassesRoot, $@"CLSID\{SoftCamClsid}\InprocServer32");
        yield return (Registry.CurrentUser, $@"Software\Classes\CLSID\{SoftCamClsid}\InprocServer32");
        yield return (Registry.CurrentUser, $@"Software\Classes\WOW6432Node\CLSID\{SoftCamClsid}\InprocServer32");
        yield return (Registry.LocalMachine, $@"Software\Classes\CLSID\{SoftCamClsid}\InprocServer32");
        yield return (Registry.LocalMachine, $@"Software\Classes\WOW6432Node\CLSID\{SoftCamClsid}\InprocServer32");
    }

    private static IEnumerable<(RegistryKey Root, string SubKey)> GetSoftCamRegistryTrees()
    {
        yield return (Registry.CurrentUser, $@"Software\Classes\CLSID\{SoftCamClsid}");
        yield return (Registry.CurrentUser, $@"Software\Classes\WOW6432Node\CLSID\{SoftCamClsid}");
        yield return (Registry.LocalMachine, $@"Software\Classes\CLSID\{SoftCamClsid}");
        yield return (Registry.LocalMachine, $@"Software\Classes\WOW6432Node\CLSID\{SoftCamClsid}");
        yield return (Registry.CurrentUser, $@"Software\Classes\CLSID\{VideoInputCategoryClsid}\Instance\DesktopBuddy - Camera");
        yield return (Registry.CurrentUser, $@"Software\Classes\CLSID\{VideoInputCategoryClsid}\Instance\DirectShow Softcam");
        yield return (Registry.CurrentUser, $@"Software\Classes\WOW6432Node\CLSID\{VideoInputCategoryClsid}\Instance\DesktopBuddy - Camera");
        yield return (Registry.CurrentUser, $@"Software\Classes\WOW6432Node\CLSID\{VideoInputCategoryClsid}\Instance\DirectShow Softcam");
        yield return (Registry.LocalMachine, $@"Software\Classes\CLSID\{VideoInputCategoryClsid}\Instance\DesktopBuddy - Camera");
        yield return (Registry.LocalMachine, $@"Software\Classes\CLSID\{VideoInputCategoryClsid}\Instance\DirectShow Softcam");
        yield return (Registry.LocalMachine, $@"Software\Classes\WOW6432Node\CLSID\{VideoInputCategoryClsid}\Instance\DesktopBuddy - Camera");
        yield return (Registry.LocalMachine, $@"Software\Classes\WOW6432Node\CLSID\{VideoInputCategoryClsid}\Instance\DirectShow Softcam");
    }

    private static void DeleteRegistryTree(RegistryKey root, string subKey)
    {
        try
        {
            root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            Log.Msg($"[Setup] Removed registry key {root.Name}\\{subKey}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[Setup] Unable to remove registry key {root.Name}\\{subKey}: {ex.Message}");
        }
    }

    private static bool IsSoftCamRegistered(string nativeDir)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{SoftCamClsid}\InprocServer32");
        string registered = key?.GetValue("") as string;
        if (string.IsNullOrWhiteSpace(registered))
            return false;

        string expected = Path.Combine(nativeDir, "softcam64.dll");
        return string.Equals(registered.Trim('"'), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSetupHashes(string nativeDir)
    {
        try
        {
            Directory.CreateDirectory(nativeDir);
            string packagedHashPath = Path.Combine(nativeDir, PackagedSetupHashFile);
            if (File.Exists(packagedHashPath))
                File.Copy(packagedHashPath, Path.Combine(nativeDir, SetupHashFile), overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Msg($"[Setup] Failed to write setup hash marker: {ex.Message}");
        }
    }

    private static string ReadSetupHash(string nativeDir, string fileName)
    {
        return ReadHashFile(Path.Combine(nativeDir, SetupHashFile), fileName);
    }

    private static string ReadPackagedSetupHash(string nativeDir, string fileName)
    {
        return ReadHashFile(Path.Combine(nativeDir, PackagedSetupHashFile), fileName);
    }

    private static string ReadHashFile(string path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fileName))
            return null;

        try
        {
            if (!File.Exists(path))
                return null;

            foreach (string line in File.ReadLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string name = line[..separator].Trim();
                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return line[(separator + 1)..].Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Msg($"[Setup] Failed to read setup hash marker: {ex.Message}");
        }

        return null;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            left = Path.GetFullPath(left.Trim('"'));
            right = Path.GetFullPath(right.Trim('"'));
        }
        catch
        {
            left = left.Trim('"');
            right = right.Trim('"');
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetRunningRestartSensitiveProcesses()
    {
        string[] names =
        {
            "Discord", "DiscordCanary", "DiscordPTB",
            "obs64", "obs32", "zoom", "Teams", "ms-teams",
            "chrome", "msedge", "firefox", "brave", "slack",
            "Skype", "Webex", "ManyCam"
        };
        var interesting = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var running = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string name = process.ProcessName;
                    if (interesting.Contains(name))
                        running.Add(name);
                }
            }
        }
        catch
        {
            // Process enumeration is best-effort only; setup should never fail because of it.
        }

        return running.ToArray();
    }

    private static void InstallVBCable(string nativeDir)
    {
        string installer = Path.Combine(nativeDir, "VBCABLE_Setup_x64.exe");
        if (!File.Exists(installer))
        {
            Log.Msg($"[Setup] VB-Cable installer missing at {installer}");
            return;
        }

        RunProcess(installer, "-i -h", workingDirectory: nativeDir, timeoutMs: 60000);
        Log.Msg(IsVBCableInstalled()
            ? "[Setup] VB-Cable detected"
            : "[Setup] VB-Cable not detected yet; reboot may be required");
    }

    private static bool IsVBCableInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"Software\VB-Audio\Cable");
        return key != null;
    }

    private static void ConfigureVBCableLoopback()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"Software\VB-Audio\Cable", writable: true);
        if (key == null)
        {
            Log.Msg("[Setup] VB-Cable registry key not present yet");
            return;
        }

        if ((key.GetValue("VBAudioCableWDM_LoopBack") as int?) == 0)
        {
            Log.Msg("[Setup] VB-Cable loopback already disabled");
            return;
        }

        key.SetValue("VBAudioCableWDM_LoopBack", 0, RegistryValueKind.DWord);
        Log.Msg("[Setup] VB-Cable loopback disabled");
        RunProcess("net.exe", "stop \"AudioEndpointBuilder\" /yes", timeoutMs: 15000);
        RunProcess("net.exe", "start \"AudioEndpointBuilder\"", timeoutMs: 15000);
        RunProcess("net.exe", "stop \"AudioSrv\" /yes", timeoutMs: 15000);
        RunProcess("net.exe", "start \"AudioSrv\"", timeoutMs: 15000);
    }

    private static bool IsVBCableLoopbackDisabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"Software\VB-Audio\Cable");
        return (key?.GetValue("VBAudioCableWDM_LoopBack") as int?) == 0;
    }

    private static void ConfigureUrlAcl()
    {
        if (IsUrlAclConfigured())
        {
            Log.Msg("[Setup] HTTP URL ACL already configured");
            return;
        }

        RunProcess("netsh", "http add urlacl url=http://+:48080/ sddl=D:(A;;GX;;;S-1-1-0)", timeoutMs: 10000);
        Log.Msg(IsUrlAclConfigured()
            ? "[Setup] HTTP URL ACL added"
            : "[Setup] WARNING: HTTP URL ACL was not detected after setup");
    }

    private static bool IsUrlAclConfigured()
    {
        var result = RunProcess("netsh", "http show urlacl url=http://+:48080/", timeoutMs: 10000, captureOutput: true);
        return result.ExitCode == 0 && result.Output.Contains("48080", StringComparison.OrdinalIgnoreCase);
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments, string workingDirectory = null, int timeoutMs = 60000, bool captureOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? ResolveResoniteRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            return (-1, "");

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch { }
            Log.Msg($"[Setup] {fileName} timed out");
            return (-1, "");
        }

        string output = "";
        if (captureOutput)
            output = (process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd()).Trim();

        Log.Msg($"[Setup] {fileName} {arguments} exit={process.ExitCode}");
        return (process.ExitCode, output);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string GetNativeDir()
    {
        return DesktopBuddyRuntimePaths.GetDirectory();
    }

    private static string ResolveResoniteRoot()
    {
        string assemblyDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? ".";
        var dir = new DirectoryInfo(assemblyDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Resonite.exe")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return AppDomain.CurrentDomain.BaseDirectory ?? assemblyDir;
    }
}
