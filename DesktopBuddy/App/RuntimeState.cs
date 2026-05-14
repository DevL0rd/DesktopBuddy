using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    internal static bool IsDesktopMode(World world)
    {
        try { return world?.LocalUser?.HeadDevice == HeadOutputDevice.Screen; }
        catch { return false; }
    }

    internal static List<DesktopSession> ActiveSessions => RuntimeStore.ActiveSessions;
    private static int NextStreamId() => SharedStreamRegistry.NextStreamId();

    internal static HashSet<RefID> DesktopCanvasIds => RuntimeStore.DesktopCanvasIds;
    private static object TopBarRaycastGate => RuntimeStore.TopBarRaycastGate;
    private static Dictionary<RefID, Slot> TopBarRaycastTargets => RuntimeStore.TopBarRaycastTargets;

    private static Dictionary<IntPtr, SharedStream> _sharedStreams => SharedStreamRegistry.Streams;

    internal static BuiltInStreamServer StreamServer { get => RuntimeStore.StreamServer; set => RuntimeStore.StreamServer = value; }
    internal static VirtualCamera VCam { get => RuntimeStore.VCam; set => RuntimeStore.VCam = value; }
    internal static VirtualMic VMic { get => RuntimeStore.VMic; set => RuntimeStore.VMic = value; }
    private const int STREAM_PORT = 48080;
    internal static string TunnelUrl { get => RuntimeStore.TunnelUrl; set => RuntimeStore.TunnelUrl = value; }
    private static Process _tunnelProcess { get => RuntimeStore.TunnelProcess; set => RuntimeStore.TunnelProcess = value; }
    private static string _cfPath { get => RuntimeStore.CloudflaredPath; set => RuntimeStore.CloudflaredPath = value; }
    private static bool _tunnelRestarting { get => RuntimeStore.TunnelRestarting; set => RuntimeStore.TunnelRestarting = value; }
    internal static readonly PerfTimer Perf = new();

    internal static SharedTextureBridgeChannel TextureBridgeChannel { get => RuntimeStore.TextureBridgeChannel; set => RuntimeStore.TextureBridgeChannel = value; }
    private static bool _textureBridgeOpened { get => RuntimeStore.TextureBridgeOpened; set => RuntimeStore.TextureBridgeOpened = value; }

    internal static HashSet<DesktopTextureProvider> OurProviders => RuntimeStore.OurProviders;

    private static Thread _windowPollerThread { get => RuntimeStore.WindowPollerThread; set => RuntimeStore.WindowPollerThread = value; }
    private static bool _windowPollerRunning { get => RuntimeStore.WindowPollerRunning; set => RuntimeStore.WindowPollerRunning = value; }
    internal static ConcurrentQueue<WindowEvent> _windowEvents => RuntimeStore.WindowEvents;

    private static string _latestVersion { get => RuntimeStore.LatestVersion; set => RuntimeStore.LatestVersion = value; }
    private static string _remoteVersion { get => RuntimeStore.RemoteVersion; set => RuntimeStore.RemoteVersion = value; }
    private static string _remoteSha { get => RuntimeStore.RemoteSha; set => RuntimeStore.RemoteSha = value; }
    private static string _remoteChangelog { get => RuntimeStore.RemoteChangelog; set => RuntimeStore.RemoteChangelog = value; }
    private static string _updateCheckError { get => RuntimeStore.UpdateCheckError; set => RuntimeStore.UpdateCheckError = value; }
    private static DateTime _lastUpdateCheckUtc { get => RuntimeStore.LastUpdateCheckUtc; set => RuntimeStore.LastUpdateCheckUtc = value; }
    private static bool _updateCheckInProgress { get => RuntimeStore.UpdateCheckInProgress; set => RuntimeStore.UpdateCheckInProgress = value; }
    private static bool _updateShown { get => RuntimeStore.UpdateShown; set => RuntimeStore.UpdateShown = value; }
    private static bool _settingsConfigDirty { get => RuntimeStore.SettingsConfigDirty; set => RuntimeStore.SettingsConfigDirty = value; }
}
