namespace DesktopBuddy;

internal readonly struct LinuxPortalSelection
{
    public readonly uint NodeId;
    public readonly int Width;
    public readonly int Height;

    public LinuxPortalSelection(uint nodeId, int width, int height)
    {
        NodeId = nodeId;
        Width = width;
        Height = height;
    }
}

internal static class LinuxPortalSelectionStore
{
    private static readonly object Lock = new();
    private static LinuxPortalSelection? _pending;

    internal static void Set(LinuxPortalSelection selection)
    {
        lock (Lock)
            _pending = selection;
    }

    internal static bool TryConsume(out LinuxPortalSelection selection)
    {
        lock (Lock)
        {
            if (_pending.HasValue)
            {
                selection = _pending.Value;
                _pending = null;
                return true;
            }
        }

        selection = default;
        return false;
    }
}
