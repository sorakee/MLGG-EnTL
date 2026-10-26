using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace GGXEnPatch;

public enum LineBreakMode
{
    Keep,
    Strip,
}

[BepInPlugin(Guid, "GGX EN Patch", Version)]
public partial class Plugin : BasePlugin
{
    public const string Guid = "ggx.enpatch";
    public const string Version = "0.5.1";
    /// <summary>Where pack/publish.py publishes the packs: &lt;url&gt;/&lt;channel&gt;/manifest-&lt;channel&gt;.json.
    /// The config's PackUrl defaults to empty (= this), so a later version can move every player.</summary>
    public const string DefaultPackUrl = "https://pub-c34991024c2e4fa89421fb9c498384c0.r2.dev";
#if PERSONAL
    public const string Edition = "Personal";
#else
    public const string Edition = "Community";
#endif

    internal static ManualLogSource Logger;
    internal static ConfigEntry<LineBreakMode> LineBreaks;
    internal static ConfigEntry<bool> MarkUntranslated;
    internal static ConfigEntry<bool> FitUi;
    internal static ConfigEntry<bool> LogUntranslatedUi;
    internal static ConfigEntry<bool> AutoUpdate;
    internal static ConfigEntry<bool> IncludeR18;
    internal static ConfigEntry<string> PackUrl;
    internal static TranslationStore Store;

    public override void Load()
    {
        Logger = Log;
        LineBreaks = Config.Bind("Text", "LineBreaks", LineBreakMode.Keep,
            "Keep: use line breaks exactly as written in the translation. " +
            "Strip: replace them with spaces and let the text box wrap.");
        MarkUntranslated = Config.Bind("Debug", "MarkUntranslated", false,
            "Prefix story lines that have no translation yet with \"[JP] \".");
        FitUi = Config.Bind("Text", "FitUiLabels", true,
            "Let translated menu labels shrink (down to 60%) to fit their box instead of overflowing.");
        LogUntranslatedUi = Config.Bind("Debug", "LogUntranslatedUi", false,
            "Write each Japanese label text that has no English yet, once, to GGXEnPatch\\ui-untranslated.tsv.");
        AutoUpdate = Config.Bind("Update", "AutoUpdate", true,
            "On launch, download new translation packs in the background (from PackUrl only; " +
            "nothing about you or the game is sent).");
        IncludeR18 = Config.Bind("Update", "IncludeR18", true,
            "Also download the adult scenes' text (a separate pack, translations-r18.json).");
        PackUrl = Config.Bind("Update", "PackUrl", "",
            "Where the translation packs are published. Leave empty for the patch's own address.");

        var dir = Path.Combine(Paths.PluginPath, "GGXEnPatch");
        Store = new TranslationStore(Path.Combine(dir, "translations"));
        Store.Reload();
        if (AutoUpdate.Value)
        {
            var url = (string.IsNullOrWhiteSpace(PackUrl.Value) ? DefaultPackUrl : PackUrl.Value.Trim()).TrimEnd('/');
            var channels = IncludeR18.Value ? new[] { "main", "r18" } : new[] { "main" };
            if (!IncludeR18.Value) Updater.RemoveChannel(Store.Dir, "r18");
            Updater.Start(Store.Dir, channels.Select(c => $"{url}/{c}/manifest-{c}.json"));
        }
        Harmony.CreateAndPatchAll(typeof(ScenarioPatches));
        Harmony.CreateAndPatchAll(typeof(UiTextPatches));
        AddComponent<MasterTextRunner>();
        AddComponent<TextFitRunner>();
        LoadPersonal(dir);
        Logger.LogInfo($"GGX EN Patch {Version} ({Edition}) loaded.");
    }

    /// <summary>Extra local tools of the Personal edition (src/Personal/, not in the public repository).</summary>
    partial void LoadPersonal(string dir);
}
