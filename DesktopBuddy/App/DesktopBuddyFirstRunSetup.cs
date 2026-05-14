using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DesktopBuddy;

internal static class DesktopBuddyFirstRunSetup
{
    private const string SoftCamClsid = "{AEF3B972-5FA5-4647-9571-358EB472BC9E}";
    private const string VideoInputCategoryClsid = "{860BB310-5D01-11d0-BD3B-00A0C911CE86}";

    private enum SetupAction
    {
        SoftCamRegistration,
        VBCableInstall,
        VBCableLoopback,
        UrlAcl,
    }

    internal static void Run()
    {
        try
        {
            string resoniteRoot = ResolveResoniteRoot();
            string nativeDir = GetNativeDir();

            Log.Msg("[Setup] Checking DesktopBuddy local setup");
            Log.Msg($"[Setup] Native path: {nativeDir}");

            CheckPackagedFiles(resoniteRoot, nativeDir);

            var required = GetRequiredAdminActions(nativeDir);
            if (required.Count == 0)
            {
                Log.Msg("[Setup] Admin setup already satisfied");
                return;
            }

            Log.Msg("[Setup] Admin setup required: " + string.Join(", ", required.Select(GetActionLabel)));
            if (IsAdministrator())
            {
                RunAdminSetup(required, nativeDir);
                return;
            }

            StartElevatedSetupHelper(required, nativeDir);
        }
        catch (Exception ex)
        {
            Log.Msg($"[Setup] First-run setup check failed: {ex}");
        }
    }

    private static void CheckPackagedFiles(string resoniteRoot, string nativeDir)
    {
        Log.Msg($"[Setup] DesktopBuddy.dll: {File.Exists(typeof(DesktopBuddyMod).Assembly.Location)}");
        Log.Msg($"[Setup] DesktopBuddyNative: {Directory.Exists(nativeDir)}");
        Log.Msg($"[Setup] cloudflared.exe: {File.Exists(Path.Combine(nativeDir, "cloudflared.exe"))}");
        Log.Msg($"[Setup] VB-Cable installer: {File.Exists(Path.Combine(nativeDir, "VBCABLE_Setup_x64.exe"))}");
        string bridge = Path.Combine(resoniteRoot, "Renderer", "BepInEx", "plugins", "DesktopBuddySharedTextureBridge", "DesktopBuddySharedTextureBridge.dll");
        Log.Msg($"[Setup] Renderer bridge: {File.Exists(bridge)}");
    }

    private static List<SetupAction> GetRequiredAdminActions(string nativeDir)
    {
        var actions = new List<SetupAction>();
        if (!IsSoftCamRegistered(nativeDir))
            actions.Add(SetupAction.SoftCamRegistration);
        if (!IsVBCableInstalled())
            actions.Add(SetupAction.VBCableInstall);
        if (!IsVBCableLoopbackDisabled())
            actions.Add(SetupAction.VBCableLoopback);
        if (!IsUrlAclConfigured())
            actions.Add(SetupAction.UrlAcl);
        return actions;
    }

    private static string GetActionLabel(SetupAction action)
    {
        return action switch
        {
            SetupAction.SoftCamRegistration => "SoftCam registration",
            SetupAction.VBCableInstall => "VB-Cable install",
            SetupAction.VBCableLoopback => "VB-Cable loopback disable",
            SetupAction.UrlAcl => "HTTP URL ACL",
            _ => action.ToString(),
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
        Log.Msg("[Setup] Admin setup complete");
    }

    private static void StartElevatedSetupHelper(IReadOnlyCollection<SetupAction> actions, string nativeDir)
    {
        string logPath = Path.Combine(nativeDir, "DesktopBuddySetup.log");
        string script = BuildElevatedSetupScript(actions, nativeDir, logPath);
        string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        string args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encodedScript}";
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
            Log.Msg("[Setup] Requesting administrator permission for first-run setup");
            var process = Process.Start(startInfo);
            if (process == null)
            {
                Log.Msg("[Setup] Elevated setup helper did not start");
                return;
            }

            Task.Run(() => WaitForElevatedSetup(process, logPath));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Msg("[Setup] Administrator setup was cancelled by the user");
        }
        catch (Exception ex)
        {
            Log.Msg($"[Setup] Failed to start elevated setup helper: {ex.Message}");
        }
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

    private static string PsSingleQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static void WaitForElevatedSetup(Process process, string logPath)
    {
        try
        {
            process.WaitForExit();
            Log.Msg($"[Setup] Elevated setup helper exited: {process.ExitCode}");
            foreach (string line in ReadTail(logPath, 200))
                Log.Msg($"[SetupHelper] {line}");
        }
        catch (Exception ex)
        {
            Log.Msg($"[Setup] Failed to collect elevated setup result: {ex.Message}");
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    private static IEnumerable<string> ReadTail(string path, int maxLines)
    {
        if (!File.Exists(path))
            yield break;

        var queue = new Queue<string>(maxLines);
        foreach (string line in File.ReadLines(path))
        {
            if (queue.Count == maxLines)
                queue.Dequeue();
            queue.Enqueue(line);
        }

        foreach (string line in queue)
            yield return line;
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

    private static void InstallVBCable(string nativeDir)
    {
        if (IsVBCableInstalled())
        {
            Log.Msg("[Setup] VB-Cable already installed");
            return;
        }

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
        string pluginDir = Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
        return Path.Combine(pluginDir, "DesktopBuddyNative");
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
