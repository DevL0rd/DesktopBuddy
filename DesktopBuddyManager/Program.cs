using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using DesktopBuddyManager;

// Handle --delete-old <path> arg passed by self-update
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--delete-old")
    {
        var oldPath = args[i + 1];
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(oldPath))
                    File.Delete(oldPath);
                break;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
        break;
    }
}

// Check for --auto-install <resonitePath> from self-update flow
string? autoInstallPath = null;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--auto-install")
    {
        autoInstallPath = args[i + 1];
        break;
    }
}

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new MainForm(autoInstallPath));
