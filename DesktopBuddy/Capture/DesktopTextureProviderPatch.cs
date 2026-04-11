using HarmonyLib;
using FrooxEngine;

namespace DesktopBuddy;

/// <summary>
/// Patches DesktopTextureProvider.UpdateAsset to remove the 
///   if (base.World != Userspace.UserspaceWorld) return;
/// check. This allows DesktopTextureProvider to work in any world,
/// not just Userspace — needed so our magic DisplayIndex gets sent to Renderite.
/// </summary>
[HarmonyPatch(typeof(DesktopTextureProvider), "UpdateAsset")]
static class DesktopTextureProviderPatch
{
    // Cache reflection lookups — these never change at runtime
    private static System.Reflection.FieldInfo _assetField;
    private static System.Reflection.PropertyInfo _assetMgrProp;
    private static System.Reflection.MethodInfo _initMethod;
    private static System.Reflection.MethodInfo _onCreatedMethod;
    private static bool _reflectionCached;
    private static bool _reflectionValid;
    private static int _callCount;

    static bool Prefix(DesktopTextureProvider __instance)
    {
        // Only bypass the world check for our instances (both magic indices and real monitor indices).
        if (!DesktopBuddyMod.OurProviders.Contains(__instance))
            return true; // Not ours — run original

        try
        {
            if (!_reflectionCached)
            {
                CacheReflection();
                _reflectionCached = true;
            }

            if (!_reflectionValid)
                return true; // Reflection failed — fall back to original

            bool verbose = _callCount++ < 3;

            var desktopTex = _assetField.GetValue(__instance) as DesktopTexture;
            if (verbose)
                Log.Msg($"[DesktopTextureProviderPatch] DisplayIndex={__instance.DisplayIndex.Value} desktopTex={(desktopTex != null ? "present" : "null")}");

            if (desktopTex == null)
            {
                desktopTex = new DesktopTexture();
                var assetMgr = _assetMgrProp?.GetValue(__instance);
                if (assetMgr != null)
                    _initMethod?.Invoke(desktopTex, new[] { assetMgr });
                else
                    Log.Msg("[DesktopTextureProviderPatch] WARNING: AssetManager is null");

                _assetField.SetValue(__instance, desktopTex);
                if (verbose) Log.Msg("[DesktopTextureProviderPatch] Created new DesktopTexture");
            }

            var callback = (System.Action)System.Delegate.CreateDelegate(
                typeof(System.Action), __instance, _onCreatedMethod);
            desktopTex.Update(__instance.DisplayIndex.Value, callback);
            if (verbose)
                Log.Msg($"[DesktopTextureProviderPatch] Sent Update(displayIndex={__instance.DisplayIndex.Value})");
        }
        catch (System.Exception ex)
        {
            Log.Msg($"[DesktopTextureProviderPatch] Error: {ex}");
            return true; // Fallback
        }

        return false; // Skip original
    }

    private static void CacheReflection()
    {
        var type = typeof(DesktopTextureProvider);

        _assetField = type.GetField("_desktopTex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Walk the full type hierarchy to find AssetManager (declared on AssetProvider<T>).
        for (var t = type; t != null && _assetMgrProp == null; t = t.BaseType)
        {
            _assetMgrProp = t.GetProperty("AssetManager",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly);
        }

        _initMethod = typeof(DesktopTexture).GetMethod("InitializeDynamic",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        _onCreatedMethod = type.GetMethod("OnTextureCreated",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        _reflectionValid = _assetField != null && _onCreatedMethod != null;

        Log.Msg($"[DesktopTextureProviderPatch] Reflection cached: assetField={_assetField != null} " +
            $"assetMgr={_assetMgrProp != null} init={_initMethod != null} onCreated={_onCreatedMethod != null}");
    }
}
