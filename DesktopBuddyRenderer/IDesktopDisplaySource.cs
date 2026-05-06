using System;
using Renderite.Unity;

namespace DesktopBuddyRenderer
{
    internal interface IDesktopDisplaySource : IDisplayTextureSource, IDisposable
    {
        int Width { get; }
        int Height { get; }
        bool IsValid { get; }
        string SourceName { get; }

        bool TryBind();
        void Tick();
    }
}
