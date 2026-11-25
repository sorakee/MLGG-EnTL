using System;
using Assets.CustomRendererFeatures;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace GGXEnPatch;

/// <summary>
/// Off by default ([QoL] Demosaic). The mosaic is a URP renderer feature: its pass still draws the
/// masked layer, but only pixelates it while the shader keyword in its settings is on.
/// Pointing that keyword at a name no shader uses leaves the layer drawn as is.
/// Purely client-side: nothing the game sends changes.
/// </summary>
[HarmonyPatch]
static class DemosaicPatches
{
    const string NeutralKeyword = "GGX_ENPATCH_NO_MOSAIC";

    internal static void Apply()
    {
        Harmony.CreateAndPatchAll(typeof(DemosaicPatches));
        // The renderer asset may already be loaded, with Create() run before we hooked it.
        try
        {
            foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<MosaicRendererFeature>()))
                Neutralize(obj.TryCast<MosaicRendererFeature>());
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    [HarmonyPatch(typeof(MosaicRendererFeature), nameof(MosaicRendererFeature.Create))]
    [HarmonyPostfix]
    static void AfterCreate(MosaicRendererFeature __instance)
    {
        try
        {
            Neutralize(__instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    static void Neutralize(MosaicRendererFeature feature)
    {
        var settings = feature?.passSettings;
        if (settings == null) return;
        var keyword = settings.Keyword;
        if (keyword == NeutralKeyword) return;
        settings.Keyword = NeutralKeyword;
        Plugin.Logger.LogInfo($"Demosaic: mosaic keyword \"{keyword}\" turned off.");
    }
}
