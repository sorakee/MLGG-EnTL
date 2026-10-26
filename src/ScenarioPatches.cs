using System;
using System.Text.RegularExpressions;
using Assets.GameUi.Scenario.Configuration;
using HarmonyLib;

namespace GGXEnPatch;

/// <summary>
/// Story text. The game parses each scene line into a ScenarioPhraseConfiguration,
/// then ConvertStringToDisplay builds the shown text (player name, ruby, ...).
/// Swapping the raw fields first lets the game format the English itself.
///
/// Don't patch ApiClient methods: they return UniTask&lt;T&gt; structs, and the
/// Il2CppInterop trampoline crashes boxing them (AccessViolation in il2cpp_value_box).
/// </summary>
[HarmonyPatch(typeof(ScenarioPhraseConfiguration), nameof(ScenarioPhraseConfiguration.ConvertStringToDisplay))]
static class ScenarioPatches
{
    const string UntranslatedMark = "[JP] ";
    static readonly Regex Japanese = new(@"[぀-ヿ㐀-䶿一-鿿]");

    [HarmonyPrefix]
    static void BeforeDisplay(ScenarioPhraseConfiguration __instance)
    {
        try
        {
            var store = Plugin.Store;
            store.ReloadIfChanged();

            var text = __instance.Text;
            if (!string.IsNullOrEmpty(text))
            {
                if (store.Lines.TryGetValue(text, out var en))
                    __instance.Text = Plugin.LineBreaks.Value == LineBreakMode.Strip ? en.Replace("\n", " ") : en;
                // The same line can be converted again (e.g. the backlog); only mark untouched Japanese.
                else if (Plugin.MarkUntranslated.Value && !text.StartsWith(UntranslatedMark) && Japanese.IsMatch(text))
                    __instance.Text = UntranslatedMark + text;
            }

            var speaker = __instance.SpeakerName;
            var speakerEn = store.Name(speaker);
            if (!ReferenceEquals(speaker, speakerEn)) __instance.SpeakerName = speakerEn;

            var team = __instance.TeamName;
            var teamEn = store.Name(team);
            if (!ReferenceEquals(team, teamEn)) __instance.TeamName = teamEn;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }
}
