using System;
using Assets.GameUi.Externals;
using Assets.GameUi.Scenario;
using HarmonyLib;

namespace GGXEnPatch;

/// <summary>
/// Off by default ([QoL] UninterruptedVoice). Advancing a story line calls AudioManager.StopVoice, which cuts
/// the current voice off. While a scenario is open that call is skipped, so the line
/// plays out (the next voice gets its own source; the manager keeps five). Leaving the
/// scenario stops voices as usual. Purely client-side: nothing the game sends changes.
///
/// The "in a scenario" flag is set from void methods only: ScenarioController's public
/// Refresh/Leave return UniTask structs, which the Il2CppInterop trampoline can't box.
/// </summary>
[HarmonyPatch]
static class UninterruptedVoicePatches
{
    static bool inScenario;

    internal static void Apply() => Harmony.CreateAndPatchAll(typeof(UninterruptedVoicePatches));

    [HarmonyPatch(typeof(ScenarioController), "Refresh", new Type[0])]
    [HarmonyPostfix]
    static void AfterRefresh() => inScenario = true;

    [HarmonyPatch(typeof(ScenarioController), nameof(ScenarioController.StartPlayVoices))]
    [HarmonyPostfix]
    static void AfterStartPlayVoices() => inScenario = true;

    [HarmonyPatch(typeof(ScenarioComponent), "OnDisable")]
    [HarmonyPostfix]
    static void AfterScenarioClosed(ScenarioComponent __instance)
    {
        if (!inScenario) return;
        inScenario = false;
        try
        {
            // Its own StopVoice may have been skipped on the way out.
            __instance.AudioManager?.StopVoice();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.StopVoice))]
    [HarmonyPrefix]
    static bool BeforeStopVoice() => !inScenario;
}
