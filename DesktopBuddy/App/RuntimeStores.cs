using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Elements.Core;
using FrooxEngine;

namespace DesktopBuddy;

internal static class RuntimeStore
{
    internal static readonly List<DesktopSession> ActiveSessions = new();
    internal static readonly HashSet<RefID> DesktopCanvasIds = new();
    internal static readonly object TopBarRaycastGate = new();
    internal static readonly Dictionary<RefID, Slot> TopBarRaycastTargets = new();

    internal static BuiltInStreamServer StreamServer;
    internal static string TunnelUrl;
    internal static Process TunnelProcess;
    internal static string CloudflaredPath;
    internal static volatile bool TunnelRestarting;

    internal static VirtualCamera VCam;
    internal static VirtualMic VMic;

    internal static SharedTextureBridgeChannel TextureBridgeChannel;
    internal static bool TextureBridgeOpened;
    internal static readonly HashSet<DesktopTextureProvider> OurProviders = new();

    internal static Thread WindowPollerThread;
    internal static volatile bool WindowPollerRunning;
    internal static readonly ConcurrentQueue<WindowEvent> WindowEvents = new();
    internal static readonly HashSet<IntPtr> AutoSpawnSeenWindows = new();
    internal static bool AutoSpawnWindowBaselineInitialized;

    internal static string LatestVersion;
    internal static string RemoteVersion;
    internal static string RemoteSha;
    internal static string RemoteChangelog;
    internal static string UpdateCheckError;
    internal static DateTime LastUpdateCheckUtc;
    internal static volatile bool UpdateCheckInProgress;
    internal static bool UpdateShown;
    internal static volatile bool SettingsConfigDirty;
}

internal struct WindowEvent
{
    public DesktopSession Session;
    public IntPtr WindowHwnd;
    public string Title;
    public WindowEventType EventType;
}

internal enum WindowEventType
{
    NewTopLevelWindow,
    TitleChanged
}
