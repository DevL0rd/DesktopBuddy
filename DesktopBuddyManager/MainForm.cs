using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopBuddyManager;

internal sealed class MainForm : Form
{
    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static readonly Color C_BgBody   = Color.FromArgb(16,  16,  20);
    private static readonly Color C_BgHeader = Color.FromArgb(22,  22,  28);
    private static readonly Color C_BgCard   = Color.FromArgb(26,  26,  34);
    private static readonly Color C_BgInput  = Color.FromArgb(32,  32,  42);
    private static readonly Color C_Border   = Color.FromArgb(46,  46,  60);
    private static readonly Color C_TxPrime  = Color.FromArgb(240, 240, 248);
    private static readonly Color C_TxSecond = Color.FromArgb(120, 120, 148);
    private static readonly Color C_TxMuted  = Color.FromArgb(60,  60,  80);
    private static readonly Color C_Blue     = Color.FromArgb(10,  132, 255);
    private static readonly Color C_BlueHov  = Color.FromArgb(56,  166, 255);
    private static readonly Color C_BlueDim  = Color.FromArgb(28,  36,  58);
    private static readonly Color C_Green    = Color.FromArgb(48,  209, 88);
    private static readonly Color C_Amber    = Color.FromArgb(255, 159, 10);
    private static readonly Color C_Red      = Color.FromArgb(255, 69,  58);

    private static readonly Font F_Title   = new("Segoe UI", 15f, FontStyle.Bold);
    private static readonly Font F_Sub     = new("Segoe UI",  9f);
    private static readonly Font F_Section = new("Segoe UI",  7.5f, FontStyle.Bold);
    private static readonly Font F_Base    = new("Segoe UI",  9f);
    private static readonly Font F_Bold    = new("Segoe UI",  9f, FontStyle.Bold);
    private static readonly Font F_Small   = new("Segoe UI",  8f);
    private static readonly Font F_Mono    = new("Consolas",  8.5f);
    private static readonly Font F_Btn     = new("Segoe UI", 11f, FontStyle.Bold);

    private const string SoftCamClsid   = "{AEF3B972-5FA5-4647-9571-358EB472BC9E}";
    private const int    ResoniteAppId  = 2519830;
    private const string SavedPathKey   = @"Software\DesktopBuddy";
    private const string SavedPathValue = "ManagerPath";
    private const int    FormW          = 760;
    private const int    FormH          = 760;
    private const int    Pad            = 20;
    private const int    IW             = FormW - Pad * 2;
    private static readonly TimeSpan CameraScanInterval = TimeSpan.FromSeconds(10);

    private TextBox     _pathBox    = null!;
    private Label       _vbDot      = null!;
    private Label       _vbLbl      = null!;
    private Label       _scDot      = null!;
    private Label       _scLbl      = null!;
    private Label       _runDot     = null!;
    private Label       _runLbl     = null!;
    private Label       _camDot     = null!;
    private Label       _camLbl     = null!;
    private CheckBox    _chkLicense = null!;
    private CheckBox    _chkClose   = null!;
    private Button      _installBtn = null!;
    private Button      _reportBtn  = null!;
    private RichTextBox _log        = null!;
    private System.Windows.Forms.Timer _timer = null!;

    private List<ProcessInfo> _camProcs  = [];
    private bool              _installing;
    private bool              _updateCheckInProgress;
    private bool              _closingForUpdate;
    private bool              _reportGenerating;
    private int               _statusRefreshInProgress;
    private int               _detectPathInProgress;
    private readonly object   _cameraCacheLock = new();
    private DateTime          _lastCameraScanUtc = DateTime.MinValue;
    private List<ProcessInfo> _cachedCameraProcs = [];
    private readonly ManagerUpdateService _updateService = new();
    private readonly SupportReportService _supportReportService = new();

    internal MainForm()
    {
        Text            = "DesktopBuddy Manager";
        ClientSize      = new Size(FormW, FormH);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = C_BgBody;
        ForeColor       = C_TxPrime;
        Font            = F_Base;
        DoubleBuffered  = true;

        BuildUI();

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += async (_, _) =>
        {
            if (!_installing && !_updateCheckInProgress && !_closingForUpdate)
                await RefreshStatusAsync();
        };

        Shown += async (_, _) =>
        {
            await DetectResonitePathAsync(logWhenMissing: true);
            _timer.Start();
            _ = CheckForManagerUpdateAsync();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int v = 1; DwmSetWindowAttribute(Handle, 20, ref v, 4); } catch { }
    }

    private void BuildUI()
    {
        var y = 0;

        // ── Header ────────────────────────────────────────────────
        var header = new Panel
        {
            Location  = Point.Empty,
            Size      = new Size(FormW, 70),
            BackColor = C_BgHeader,
        };
        header.Paint += (_, e) =>
        {
            using var p = new Pen(C_Border);
            e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        Controls.Add(header);

        header.Controls.Add(new Label
        {
            Text      = "DesktopBuddy",
            Font      = F_Title,
            ForeColor = C_TxPrime,
            BackColor = Color.Transparent,
            AutoSize  = true,
            Location  = new Point(Pad, 12),
        });
        header.Controls.Add(new Label
        {
            Text      = "Resonite desktop manager and diagnostics",
            Font      = F_Sub,
            ForeColor = C_TxSecond,
            BackColor = Color.Transparent,
            AutoSize  = true,
            Location  = new Point(Pad + 2, 40),
        });

        y = 70 + 22;

        // ── Installation path ─────────────────────────────────────
        Controls.Add(SectionLabel("Managed Resonite Path", Pad, y));
        y += 22;

        var browseBtn = new Button
        {
            Text      = "Browse",
            Size      = new Size(76, 32),
            BackColor = C_BgCard,
            ForeColor = C_TxSecond,
            FlatStyle = FlatStyle.Flat,
            Font      = F_Small,
            Cursor    = Cursors.Hand,
        };
        browseBtn.FlatAppearance.BorderColor = C_Border;
        browseBtn.Location = new Point(FormW - Pad - browseBtn.Width, y);

        _pathBox = new TextBox
        {
            Location    = new Point(Pad, y),
            Size        = new Size(FormW - Pad * 2 - browseBtn.Width - 8, 32),
            BackColor   = C_BgInput,
            ForeColor   = C_TxPrime,
            BorderStyle = BorderStyle.FixedSingle,
            Font        = F_Base,
        };
        _pathBox.TextChanged += (_, _) => UpdateInstallBtn();
        browseBtn.Click += async (_, _) =>
        {
            using var d = new FolderBrowserDialog
            {
                Description        = "Select Resonite root folder",
                UseDescriptionForTitle = true,
                SelectedPath       = _pathBox.Text,
            };
            if (d.ShowDialog() == DialogResult.OK)
            {
                _pathBox.Text = d.SelectedPath;
                await RefreshStatusAsync(forceCameraScan: true);
            }
        };
        Controls.Add(_pathBox);
        Controls.Add(browseBtn);
        y += 38;

        var detectBtn = new Button
        {
            Text      = "Auto-Detect",
            Location  = new Point(Pad, y),
            Size      = new Size(88, 20),
            BackColor = C_BgBody,
            ForeColor = C_TxMuted,
            FlatStyle = FlatStyle.Flat,
            Font      = F_Small,
            Cursor    = Cursors.Hand,
        };
        detectBtn.FlatAppearance.BorderColor = C_Border;
        detectBtn.Click += async (_, _) => { await DetectResonitePathAsync(logWhenMissing: true); };
        Controls.Add(detectBtn);
        y += 30;

        // ── System status ─────────────────────────────────────────
        y += 10;
        Controls.Add(SectionLabel("System Status", Pad, y));
        y += 22;

        var statusCard = new DoubleBufferedPanel
        {
            Location  = new Point(Pad, y),
            Size      = new Size(IW, 144),
            BackColor = C_BgCard,
        };
        statusCard.Paint += (_, e) =>
        {
            var g = e.Graphics;
            using var border = new Pen(C_Border);
            g.DrawRectangle(border, 0, 0, statusCard.Width - 1, statusCard.Height - 1);
            using var sep = new Pen(C_Border);
            for (var i = 1; i < 4; i++)
                g.DrawLine(sep, 1, i * 36, statusCard.Width - 2, i * 36);
        };
        Controls.Add(statusCard);

        (_vbDot,  _vbLbl)  = AddStatusRow(statusCard, "VB-Cable Audio Driver",  0);
        (_scDot,  _scLbl)  = AddStatusRow(statusCard, "SoftCam Virtual Camera", 1);
        (_runDot, _runLbl) = AddStatusRow(statusCard, "Resonite",               2);
        (_camDot, _camLbl) = AddStatusRow(statusCard, "Camera Driver",          3);

        y += 152;

        // ── License ───────────────────────────────────────────────
        y += 10;
        Controls.Add(SectionLabel("License Agreement", Pad, y));
        y += 22;

        var licenseBox = new RichTextBox
        {
            Location    = new Point(Pad, y),
            Size        = new Size(IW, 100),
            BackColor   = C_BgCard,
            ForeColor   = Color.FromArgb(100, 100, 130),
            ReadOnly    = true,
            BorderStyle = BorderStyle.FixedSingle,
            Font        = new Font("Consolas", 7.5f),
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Text        = LoadLicenseText(),
        };
        Controls.Add(licenseBox);
        y += 108;

        _chkLicense = MakeCheckBox(
            "I have read and accept the GNU Affero General Public License v3.0",
            Pad, y);
        _chkLicense.CheckedChanged += (_, _) => UpdateInstallBtn();
        Controls.Add(_chkLicense);
        y += 28;

        _chkClose = MakeCheckBox(
            "I understand Resonite and processes using the camera driver will be closed during management actions",
            Pad, y);
        _chkClose.CheckedChanged += (_, _) => UpdateInstallBtn();
        Controls.Add(_chkClose);
        y += 34;

        // ── Install / Update button ───────────────────────────────
        y += 8;
        _installBtn = new Button
        {
            Text      = "Install / Update",
            Location  = new Point(Pad, y),
            Size      = new Size(IW, 44),
            BackColor = C_BlueDim,
            ForeColor = C_TxMuted,
            FlatStyle = FlatStyle.Flat,
            Font      = F_Btn,
            Cursor    = Cursors.Hand,
            Enabled   = false,
        };
        _installBtn.FlatAppearance.BorderColor        = C_Border;
        _installBtn.FlatAppearance.MouseOverBackColor = C_BlueHov;
        _installBtn.FlatAppearance.BorderSize         = 0;
        _installBtn.EnabledChanged += (_, _) =>
        {
            _installBtn.BackColor = _installBtn.Enabled ? C_Blue    : C_BlueDim;
            _installBtn.ForeColor = _installBtn.Enabled ? Color.White : C_TxMuted;
            _installBtn.FlatAppearance.BorderColor = _installBtn.Enabled ? C_Blue : C_Border;
        };
        _installBtn.Click += (_, _) => BeginInstall();
        Controls.Add(_installBtn);
        y += 52;

        _reportBtn = new Button
        {
            Text      = "Generate Support Report",
            Location  = new Point(Pad, y),
            Size      = new Size(IW, 34),
            BackColor = C_BgCard,
            ForeColor = C_TxPrime,
            FlatStyle = FlatStyle.Flat,
            Font      = F_Base,
            Cursor    = Cursors.Hand,
        };
        _reportBtn.FlatAppearance.BorderColor = C_Border;
        _reportBtn.FlatAppearance.MouseOverBackColor = C_BlueDim;
        _reportBtn.Click += async (_, _) => await BeginGenerateSupportReportAsync();
        Controls.Add(_reportBtn);
        y += 42;

        // ── Log ───────────────────────────────────────────────────
        Controls.Add(new Panel
        {
            Location  = new Point(0, y),
            Size      = new Size(FormW, 1),
            BackColor = C_Border,
        });
        y += 9;
        Controls.Add(SectionLabel("Log", Pad, y));
        y += 22;

        _log = new RichTextBox
        {
            Location    = new Point(Pad, y),
            Size        = new Size(IW, FormH - y - Pad),
            BackColor   = C_BgBody,
            ForeColor   = Color.FromArgb(140, 140, 170),
            ReadOnly    = true,
            BorderStyle = BorderStyle.None,
            Font        = F_Mono,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
        };
        Controls.Add(_log);
    }

    private static (Label dot, Label status) AddStatusRow(Panel card, string name, int row)
    {
        const int rh = 36;
        var ry = row * rh;

        var dot = new Label
        {
            Text      = "\u25CF",
            Font      = new Font("Segoe UI", 8f),
            ForeColor = Color.FromArgb(50, 50, 70),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(12, ry),
            Size      = new Size(18, rh),
        };

        card.Controls.Add(new Label
        {
            Text      = name,
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(110, 110, 140),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Location  = new Point(36, ry),
            Size      = new Size(340, rh),
        });

        var status = new Label
        {
            Text      = "-",
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 50, 70),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleRight,
            Location  = new Point(card.Width - 170 - 12, ry),
            Size      = new Size(170, rh),
        };

        card.Controls.Add(dot);
        card.Controls.Add(status);
        return (dot, status);
    }

    private static Label SectionLabel(string text, int x, int y) => new()
    {
        Text      = text.ToUpper(),
        Font      = F_Section,
        ForeColor = Color.FromArgb(80, 80, 110),
        BackColor = Color.Transparent,
        AutoSize  = true,
        Location  = new Point(x, y),
    };

    private CheckBox MakeCheckBox(string text, int x, int y) => new()
    {
        Text      = text,
        Location  = new Point(x, y),
        Size      = new Size(IW, 24),
        Font      = F_Small,
        ForeColor = C_TxSecond,
        BackColor = Color.Transparent,
        FlatStyle = FlatStyle.Flat,
        Cursor    = Cursors.Hand,
    };

    private static string LoadLicenseText()
    {
        try
        {
            var asm = typeof(MainForm).Assembly;
            using var s = asm.GetManifestResourceStream("DesktopBuddyManager.LICENSE")
                ?? asm.GetManifestResourceNames()
                    .Where(name => name.EndsWith(".LICENSE", StringComparison.OrdinalIgnoreCase))
                    .Select(asm.GetManifestResourceStream)
                    .FirstOrDefault(stream => stream != null);
            if (s != null) return new StreamReader(s).ReadToEnd();
        }
        catch { }
        return "GNU Affero General Public License v3.0\nSee https://www.gnu.org/licenses/agpl-3.0.html";
    }

    private void UpdateInstallBtn()
    {
        var ok = !_installing &&
                 !_updateCheckInProgress &&
                 !_closingForUpdate &&
                 IsResoniteRoot(_pathBox.Text.Trim()) &&
                 _chkLicense.Checked &&
                 _chkClose.Checked;
        _installBtn.Enabled = ok;
        _reportBtn.Enabled = !_installing && !_updateCheckInProgress && !_closingForUpdate && !_reportGenerating;
    }

    private async Task CheckForManagerUpdateAsync()
    {
        if (_updateCheckInProgress || _closingForUpdate)
            return;

        _updateCheckInProgress = true;
        UpdateInstallBtn();
        Log($"Manager build SHA: {ManagerUpdateService.CurrentBuildSha}");

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            Log(update.Message);

            if (!update.HasUpdate)
                return;

            _closingForUpdate = true;
            _timer.Stop();
            UpdateInstallBtn();
            Log($"Downloading updated manager asset: {update.AssetName}");

            var managerPath = await _updateService.DownloadUpdateAsync(update);
            SaveSelectedPath();
            Log($"Launching updated manager: {managerPath}");
            ManagerUpdateService.LaunchManager(managerPath);

            BeginInvoke(new Action(() =>
            {
                Close();
                Application.ExitThread();
                Environment.Exit(0);
            }));
        }
        catch (Exception ex)
        {
            _closingForUpdate = false;
            Log($"Manager update check failed: {ex.Message}");
        }
        finally
        {
            if (!_closingForUpdate)
            {
                _updateCheckInProgress = false;
                UpdateInstallBtn();
            }
        }
    }

    private async Task BeginGenerateSupportReportAsync()
    {
        if (_reportGenerating)
            return;

        var description = DescriptionPromptForm.Prompt(this);
        if (description == null)
            return;

        _reportGenerating = true;
        UpdateInstallBtn();
        Log("Generating support report...");

        try
        {
            var zipPath = await Task.Run(() => _supportReportService.GenerateReportAsync(
                _pathBox.Text.Trim(),
                description,
                ManagerUpdateService.CurrentBuildSha));

            Log($"Support report created: {zipPath}");

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{zipPath}\"",
                    UseShellExecute = true,
                });
            }
            catch
            {
            }

            MessageBox.Show(
                "The support report has been created and selected in Explorer." + Environment.NewLine + Environment.NewLine +
                zipPath,
                "Support Report Ready",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"Support report generation failed: {ex.Message}");
            MessageBox.Show(
                $"Could not create the support report.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Support Report Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _reportGenerating = false;
            UpdateInstallBtn();
        }
    }

    private void SaveSelectedPath()
    {
        try
        {
            var path = _pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
                return;

            using var key = Registry.CurrentUser.CreateSubKey(SavedPathKey);
            key.SetValue(SavedPathValue, path, RegistryValueKind.String);
        }
        catch
        {
        }
    }

    // ── Detection ─────────────────────────────────────────────────

    private const string SavedPathKeyFull = SavedPathKey;

    private static string? DetectResonitePath()
    {
        foreach (var c in EnumerateCandidates())
            if (IsResoniteRoot(c)) return c;
        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        foreach (var proc in Process.GetProcessesByName("Resonite"))
        {
            string? dir = null;
            try   { dir = Path.GetDirectoryName(proc.MainModule?.FileName); }
            catch { }
            finally { proc.Dispose(); }
            if (dir != null) yield return dir;
        }

        foreach (var node in new[] {
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {ResoniteAppId}",
            $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {ResoniteAppId}" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(node);
            if (key?.GetValue("InstallLocation") is string p) yield return p;
        }

        var steamPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, node) in new (RegistryKey, string)[] {
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (Registry.CurrentUser,  @"SOFTWARE\Valve\Steam"),
            (Registry.CurrentUser,  @"SOFTWARE\WOW6432Node\Valve\Steam") })
        {
            if (hive.OpenSubKey(node)?.GetValue("InstallPath") is string sp)
                steamPaths.Add(sp);
        }

        foreach (var steam in steamPaths)
        {
            yield return Path.Combine(steam, "steamapps", "common", "Resonite");

            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(
                    File.ReadAllText(vdf), @"""path""\s+""([^""]+)"""))
            {
                var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                yield return Path.Combine(lib, "steamapps", "common", "Resonite");
            }
        }

        var drivePatterns = new[] {
            @"Steam\steamapps\common\Resonite",
            @"SteamLibrary\steamapps\common\Resonite",
            @"Games\Steam\steamapps\common\Resonite",
            @"Games\SteamLibrary\steamapps\common\Resonite",
        };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (var pat in drivePatterns)
                yield return Path.Combine(drive.RootDirectory.FullName, pat);

        using var saved = Registry.CurrentUser.OpenSubKey(SavedPathKey);
        if (saved?.GetValue(SavedPathValue) is string last) yield return last;
    }

    private static bool IsResoniteRoot(string? path) =>
        !string.IsNullOrEmpty(path) &&
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "Resonite.exe"));

    private async Task DetectResonitePathAsync(bool logWhenMissing)
    {
        if (Interlocked.Exchange(ref _detectPathInProgress, 1) == 1)
            return;

        try
        {
            var found = await Task.Run(DetectResonitePath);
            if (IsDisposed)
                return;

            if (found != null)
            {
                _pathBox.Text = found;
                Log($"Auto-detected: {found}");
            }
            else if (logWhenMissing)
            {
                Log("Resonite not found - use Browse or Auto-Detect to locate it.");
            }

            await RefreshStatusAsync(forceCameraScan: true);
        }
        finally
        {
            Interlocked.Exchange(ref _detectPathInProgress, 0);
        }
    }

    // ── Status refresh ────────────────────────────────────────────

    private async Task RefreshStatusAsync(bool forceCameraScan = false)
    {
        if (Interlocked.Exchange(ref _statusRefreshInProgress, 1) == 1)
            return;

        try
        {
            var snapshot = await Task.Run(() => CollectStatusSnapshot(forceCameraScan));
            if (IsDisposed)
                return;

            SetRow(_vbDot, _vbLbl, snapshot.VBCableInstalled, "Installed", "Not Installed");
            SetRow(_scDot, _scLbl, snapshot.SoftCamRegistered, "Registered", "Not Registered");
            SetRow(_runDot, _runLbl, !snapshot.ResoniteRunning, "Not Running", "Running - will be closed");

            _camProcs = snapshot.CameraProcesses;
            var camText = _camProcs.Count > 0
                ? string.Join(", ", _camProcs.Select(p => p.Name).Distinct()) + " - will be closed"
                : "Not In Use";
            SetRow(_camDot, _camLbl, _camProcs.Count == 0, "Not In Use", camText);

            UpdateInstallBtn();
        }
        finally
        {
            Interlocked.Exchange(ref _statusRefreshInProgress, 0);
        }
    }

    private static void SetRow(Label dot, Label lbl, bool ok, string okText, string failText)
    {
        var col = ok ? C_Green : C_Amber;
        dot.ForeColor = col;
        lbl.ForeColor = col;
        lbl.Text      = ok ? okText : failText;
    }

    private static bool IsVBCableInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"Software\VB-Audio\Cable");
        return key != null;
    }

    private static bool IsSoftCamRegistered()
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{SoftCamClsid}");
        return key != null;
    }

    private StatusSnapshot CollectStatusSnapshot(bool forceCameraScan)
    {
        return new StatusSnapshot(
            IsVBCableInstalled(),
            IsSoftCamRegistered(),
            IsProcessRunning("Resonite"),
            GetCameraProcesses(forceCameraScan));
    }

    private List<ProcessInfo> GetCameraProcesses(bool forceRefresh)
    {
        lock (_cameraCacheLock)
        {
            if (!forceRefresh && DateTime.UtcNow - _lastCameraScanUtc < CameraScanInterval)
                return [.. _cachedCameraProcs];

            _cachedCameraProcs = FindSoftcamProcesses();
            _lastCameraScanUtc = DateTime.UtcNow;
            return [.. _cachedCameraProcs];
        }
    }

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static List<ProcessInfo> FindSoftcamProcesses()
    {
        var result = new List<ProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (module.ModuleName.Equals("softcam64.dll", StringComparison.OrdinalIgnoreCase) ||
                        module.ModuleName.Equals("softcam.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new ProcessInfo(process.Id, process.ProcessName));
                        break;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return result;
    }

    // ── Install ───────────────────────────────────────────────────

    private void BeginInstall()
    {
        var resonitePath = _pathBox.Text.Trim();
        if (!IsResoniteRoot(resonitePath))
        {
            MessageBox.Show(
                "The selected path doesn't appear to be a valid Resonite installation.\nMake sure Resonite.exe is in the selected folder.",
                "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _installBtn.Enabled = false;
        _installBtn.Text    = "Installing...";
        _installing         = true;
        _timer.Stop();

        Task.Run(() =>
        {
            try   { DoInstall(resonitePath); }
            finally
            {
                Invoke(() =>
                {
                    _installing         = false;
                    _installBtn.Text    = "Install / Update";
                    _timer.Start();
                    _ = RefreshStatusAsync(forceCameraScan: true);
                });
            }
        });
    }

    private void DoInstall(string resonitePath)
    {
        Log("================================");
        Log("  Starting installation");
        Log($"  Target: {resonitePath}");
        Log("================================");

        foreach (var p in Process.GetProcessesByName("Resonite"))
        {
            try   { p.Kill(); Log($"Closed Resonite (PID {p.Id})"); }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var p in FindSoftcamProcesses())
        {
            try
            {
                using var process = Process.GetProcessById(p.Id);
                process.Kill();
                Log($"Closed {p.Name} (PID {p.Id})");
            }
            catch { }
        }
        if (Process.GetProcessesByName("Resonite").Length > 0 || FindSoftcamProcesses().Count > 0)
            System.Threading.Thread.Sleep(1000);

        if (!CopyFiles(resonitePath)) return;

        if (!IsSoftCamRegistered())
            RegisterSoftCam(resonitePath);
        else
            Log("SoftCam: already registered");

        if (!IsVBCableInstalled())
            InstallVBCable(resonitePath);
        else
            Log("VB-Cable: already installed");

        ConfigureVBCableLoopback();
        ConfigureUrlAcl();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SavedPathKey);
            key.SetValue(SavedPathValue, resonitePath, RegistryValueKind.String);
        }
        catch { }

        Log("================================");
        Log("  Installation complete");
        Log("  A restart may be required");
        Log("================================");

        Invoke(() => MessageBox.Show(
            "Installation complete!\n\nA system restart may be required for all virtual devices to function.",
            "Done", MessageBoxButtons.OK, MessageBoxIcon.Information));
    }

    private bool CopyFiles(string resonitePath)
    {
        const string prefix = "payload/";
        var asm       = typeof(MainForm).Assembly;
        var resources = asm.GetManifestResourceNames()
                           .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                           .ToArray();

        if (resources.Length == 0)
        {
            Log("ERROR: No payload embedded - this is a build error.");
            Invoke(() => MessageBox.Show(
                "No payload files found inside this manager.\nThis is a build error - please report it.",
                "Build Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
            return false;
        }

        Log($"Extracting {resources.Length} files...");
        foreach (var name in resources)
        {
            var rel  = name[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.Combine(resonitePath, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            using var stream = asm.GetManifestResourceStream(name)!;
            using var file   = File.Create(dest);
            stream.CopyTo(file);
            Log($"  {name[prefix.Length..]}");
        }

        Log("Files extracted.");
        return true;
    }

    private void RegisterSoftCam(string resonitePath)
    {
        Log("Registering SoftCam DirectShow filter...");
        foreach (var dll in new[] { "softcam64.dll", "softcam.dll" })
        {
            var path = Path.Combine(resonitePath, "softcam", dll);
            if (!File.Exists(path)) continue;
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "regsvr32.exe", Arguments = $"/s \"{path}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                });
                p?.WaitForExit(10000);
                Log($"  regsvr32 {dll}: exit {p?.ExitCode}");
            }
            catch (Exception ex) { Log($"  regsvr32 {dll}: {ex.Message}"); }
        }
    }

    private void InstallVBCable(string resonitePath)
    {
        var exe = Path.Combine(resonitePath, "vbcable", "VBCABLE_Setup_x64.exe");
        if (!File.Exists(exe)) { Log("VB-Cable installer not found."); return; }

        Log("Installing VB-Cable...");
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe, Arguments = "-i -h",
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = true,
            });
            p?.WaitForExit(60000);
            Log($"VB-Cable installer exit: {p?.ExitCode}");
            Log(IsVBCableInstalled() ? "VB-Cable detected." : "VB-Cable not detected yet - restart may be required.");
        }
        catch (Exception ex) { Log($"VB-Cable install error: {ex.Message}"); }
    }

    private void ConfigureVBCableLoopback()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\VB-Audio\Cable", writable: true);
            if (key == null) { Log("VB-Cable registry key not present yet."); return; }

            if (key.GetValue("VBAudioCableWDM_LoopBack") is int v && v == 0)
            { Log("VB-Cable loopback already disabled."); return; }

            key.SetValue("VBAudioCableWDM_LoopBack", 0, RegistryValueKind.DWord);
            Log("VB-Cable loopback disabled.");
            RestartService("AudioEndpointBuilder");
            RestartService("AudioSrv");
        }
        catch (Exception ex) { Log($"Loopback config error: {ex.Message}"); }
    }

    private void ConfigureUrlAcl()
    {
        const string url = "http://+:48080/";
        const string sddl = "D:(A;;GX;;;S-1-1-0)";
        Log("Configuring HTTP URL ACL for stream server...");
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url={url} sddl={sddl}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(10000);
            Log($"  netsh urlacl exit: {p?.ExitCode}");
        }
        catch (Exception ex) { Log($"  netsh urlacl error: {ex.Message}"); }
    }

    private void RestartService(string name)
    {
        Log($"Restarting {name}...");
        RunQuiet("net.exe", $"stop \"{name}\" /yes");
        RunQuiet("net.exe", $"start \"{name}\"");
    }

    private static void RunQuiet(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
            })?.WaitForExit(15000);
        }
        catch { }
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (InvokeRequired) Invoke(() => Append(line));
        else                Append(line);
    }

    private void Append(string line)
    {
        _log.AppendText(line + "\n");
        _log.ScrollToCaret();
    }
}

internal sealed class DoubleBufferedPanel : Panel
{
    internal DoubleBufferedPanel() => DoubleBuffered = true;
}

internal sealed record ProcessInfo(int Id, string Name);

internal sealed record StatusSnapshot(
    bool VBCableInstalled,
    bool SoftCamRegistered,
    bool ResoniteRunning,
    List<ProcessInfo> CameraProcesses);
