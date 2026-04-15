using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using DesktopBuddyManager;

// ── --relay-install <resonitePath> <stagingDir> ─────────────────────────────
// Launched from a staging dir by the old Manager.  Copies all staged files to
// resonitePath (including our own DesktopBuddyManager.exe since we are NOT running from there),
// then relaunches DesktopBuddyManager.exe from resonitePath with --cleanup-staging + --auto-install
// and exits without showing any UI.
var relayInstall    = GetArg(args, "--relay-install");
var relayStagingDir = args.Length > 0 ? GetRelayStaging(args) : null;
if (relayInstall != null && relayStagingDir != null)
{
    // Log to temp during relay (we don't own resonitePath yet)
    var relayLog = Path.Combine(Path.GetTempPath(), "DesktopBuddyManager-relay.log");
    Logger.Init(relayLog);
    Logger.Write($"=== relay-install started ===");
    Logger.Write($"  resonitePath : {relayInstall}");
    Logger.Write($"  stagingDir   : {relayStagingDir}");

    try
    {
        // Kill processes that would hold file locks
        Logger.Write("Killing processes that may hold file locks...");
        KillProcesses("Resonite", "Renderite.Host", "Renderite.Renderer", "cloudflared", "DesktopBuddyManager");

        // Copy everything from stagingDir → resonitePath
        Logger.Write($"Copying staging → resonitePath...");
        int copied = CopyDirectory(relayStagingDir, relayInstall);
        Logger.Write($"  {copied} file(s) copied");

        // Copy relay log into resonitePath so DoInstall phase can append to it
        var destLog = Path.Combine(relayInstall, "DesktopBuddyManager.log");
        try { File.Copy(relayLog, destLog, overwrite: true); } catch { }

        // Relaunch from resonitePath
        var finalManager = Path.Combine(relayInstall, "DesktopBuddyManager.exe");
        Logger.Write($"Launching: {finalManager}");
        if (!File.Exists(finalManager))
        {
            Logger.Write($"ERROR: DesktopBuddyManager.exe not found at {finalManager}");
            MessageBox.Show($"Relay install failed:\nDesktopBuddyManager.exe not found at:\n{finalManager}",
                "DesktopBuddy", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(1);
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName         = finalManager,
            Arguments        = $"--cleanup-staging \"{relayStagingDir}\" --auto-install \"{relayInstall}\"",
            WorkingDirectory = relayInstall,
            UseShellExecute  = true,
        });
        Logger.Write("Relay complete — exiting");
    }
    catch (Exception ex)
    {
        Logger.Write($"EXCEPTION in relay-install: {ex}");
        MessageBox.Show($"Relay install failed:\n{ex.Message}", "DesktopBuddy",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    Environment.Exit(0);
    return;
}

// ── --cleanup-staging <stagingDir> ──────────────────────────────────────────
// Delete the temp staging directory left by a relay install.
var cleanupStaging = GetArg(args, "--cleanup-staging");
if (cleanupStaging != null)
{
    for (int attempt = 0; attempt < 10; attempt++)
    {
        try
        {
            if (Directory.Exists(cleanupStaging))
                Directory.Delete(cleanupStaging, recursive: true);
            break;
        }
        catch { Thread.Sleep(500); }
    }
}

// ── --delete-old <path> (legacy self-update) ────────────────────────────────
var deleteOld = GetArg(args, "--delete-old");
if (deleteOld != null)
{
    for (int attempt = 0; attempt < 10; attempt++)
    {
        try { if (File.Exists(deleteOld)) File.Delete(deleteOld); break; }
        catch { Thread.Sleep(500); }
    }
}

// ── --auto-install <resonitePath> ───────────────────────────────────────────
// Passed after a relay that has already copied files; tells the form to run
// the post-copy setup steps (SoftCam, URL ACL, renderer deps) immediately.
string? autoInstallPath = GetArg(args, "--auto-install");

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new MainForm(autoInstallPath));

// ── Helpers ──────────────────────────────────────────────────────────────────

static string? GetArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static string? GetRelayStaging(string[] args)
{
    // --relay-install <resonitePath> <stagingDir>  ← two positional args after flag
    for (int i = 0; i < args.Length - 2; i++)
        if (args[i] == "--relay-install") return args[i + 2];
    return null;
}

static void KillProcesses(params string[] names)
{
    // Don't kill ourselves
    int selfPid = Environment.ProcessId;
    foreach (var name in names)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            if (p.Id == selfPid) { p.Dispose(); continue; }
            try
            {
                Logger.Write($"  Killing {name} (PID {p.Id})...");
                p.Kill();
                p.WaitForExit(3000);
                Logger.Write($"  {name} (PID {p.Id}) killed");
            }
            catch (Exception ex) { Logger.Write($"  Could not kill {name} (PID {p.Id}): {ex.Message}"); }
            finally { p.Dispose(); }
        }
    }
    Thread.Sleep(500);
}

static int CopyDirectory(string sourceDir, string destDir)
{
    Directory.CreateDirectory(destDir);
    int count = 0;
    foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceDir, file);
        var dest     = Path.Combine(destDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            File.Copy(file, dest, overwrite: true);
            Logger.Write($"  copy {relative}");
            count++;
        }
        catch (Exception ex)
        {
            Logger.Write($"  FAILED to copy {relative}: {ex.Message}");
        }
    }
    return count;
}

