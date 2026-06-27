using HarmonyLib;
using FrooxEngine;

namespace DesktopBuddy;

[HarmonyPatch(typeof(DesktopTextureProvider), "UpdateAsset")]
static class DesktopTextureProviderPatch
{

    private static System.Reflection.FieldInfo _assetField;
    private static System.Reflection.PropertyInfo _assetMgrProp;
    private static System.Reflection.MethodInfo _initMethod;
    private static System.Reflection.MethodInfo _onCreatedMethod;
    private static bool _reflectionCached;
    private static bool _reflectionValid;
    static bool Prefix(DesktopTextureProvider __instance)
    {

        if (!DesktopBuddyMod.OurProviders.Contains(__instance))
            return true;

        if (__instance.DisplayIndex.Value == int.MinValue)
            return false;

        try
        {
            if (!_reflectionCached)
            {
                CacheReflection();
                _reflectionCached = true;
            }

            if (!_reflectionValid)
                return true;

            var desktopTex = _assetField.GetValue(__instance) as DesktopTexture;

            if (desktopTex == null)
            {
                desktopTex = new DesktopTexture();
                var assetMgr = _assetMgrProp?.GetValue(__instance);
                if (assetMgr != null)
                    _initMethod?.Invoke(desktopTex, new[] { assetMgr });
                else
                    Log.Msg("[DesktopTextureProviderPatch] WARNING: AssetManager is null");

                _assetField.SetValue(__instance, desktopTex);
            }

            var callback = (System.Action)System.Delegate.CreateDelegate(
                typeof(System.Action), __instance, _onCreatedMethod);
            desktopTex.Update(__instance.DisplayIndex.Value, callback);
        }
        catch (System.Exception ex)
        {
            Log.Msg($"[DesktopTextureProviderPatch] Error: {ex}");
            return true;
        }

        return false;
    }

    internal static void PrewarmReflection()
    {
        if (_reflectionCached) return;
        CacheReflection();
        _reflectionCached = true;
    }

    private static void CacheReflection()
    {
        var type = typeof(DesktopTextureProvider);

        _assetField = type.GetField("_desktopTex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

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
