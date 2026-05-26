using System;
using Renderite.Unity;

namespace DesktopBuddySharedTextureBridge
{
    internal interface IBridgeTextureSlot : IDisplayTextureSource, IDisposable
    {
        int Width { get; }
        int Height { get; }
        int RequestCount { get; }
        bool TryBind();
        void Tick();
    }
}
