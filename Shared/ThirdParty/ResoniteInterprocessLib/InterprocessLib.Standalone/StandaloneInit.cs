using Renderite.Shared;

using System;

namespace InterprocessLib;

internal class Defaults
{
	public static bool DefaultIsAuthority => throw new NotImplementedException();
	public static IMemoryPackerEntityPool DefaultPool => throw new NotImplementedException();
	public static string DefaultQueuePrefix => throw new NotImplementedException();
	public static void Init()
	{
		// This only exists so it can be called to trigger the static constructor
	}
	static Defaults()
	{
	}
}
