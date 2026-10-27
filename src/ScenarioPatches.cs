using System;
using System.Text.RegularExpressions;
using Assets.GameUi.Scenario.Configuration;
using Assets.GameUi.Services;
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
                    __instance.Text = Line(en);
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

    internal static string Line(string en) => Plugin.LineBreaks.Value == LineBreakMode.Strip ? en.Replace("\n", " ") : en;
}

/// <summary>
/// Since the 2026-10-27 game update the frame builder (ScenarioController's GenerateFrames lambda) has
/// ConvertStringToDisplay inlined, so the patch above only runs on the rare ApplyUserNameToFrames path.
/// The inlined copy still hands the line, the speaker and the team name to this service (an interface
/// call, which the compiler can't inline): translate them on the way in, so the player name still goes
/// into the English. Home speeches, Talk, Tweety and Maze events pass through too; theirs is master text,
/// English already, so it finds nothing here.
/// </summary>
[HarmonyPatch(typeof(ScenarioService), nameof(ScenarioService.ReplaceUsernamePlaceholder))]
static class ScenarioServicePatches
{
    [HarmonyPrefix]
    static void BeforeReplace(ref string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;
            var store = Plugin.Store;
            store.ReloadIfChanged();
            text = store.Lines.TryGetValue(text, out var en) ? ScenarioPatches.Line(en) : store.Name(text);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }
}
