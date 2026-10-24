using System;
using System.Collections.Generic;
using Assets.GameUi.Episode;
using Assets.GameUi.Episode.EventChapter;
using Assets.GameUi.Episode.MainChapter;
using Assets.GameUi.Trophy;
using Assets.Utilities;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;

namespace GGXEnPatch;

/// <summary>
/// English runs longer than Japanese, and some screens size their labels for the Japanese
/// (episode list subtitles, the chapter section buttons). Once a second, on the screens below,
/// every TextMeshPro label that doesn't auto-size already gets auto-sizing from its own size
/// down to <see cref="MinScale"/> of it: text that fits is untouched, overflowing text shrinks
/// into its box. The game reuses list cells, so each label is set up once.
/// Trophy badges are fitted harder: their titles run up to three times the Japanese, and the game's
/// own auto-size wraps them onto a second line the badge has no room for. They stay on one line and
/// shrink down to <see cref="BadgeMinScale"/>.
/// </summary>
public class TextFitRunner : MonoBehaviour
{
    const float Interval = 1f;
    const float MinScale = 0.6f;
    const float BadgeMinScale = 0.4f;

    // Screens whose labels may overflow in English. Children (list cells) are covered too.
    static readonly Il2CppSystem.Type[] Screens =
    {
        Il2CppType.Of<EpisodeComponent>(),
        Il2CppType.Of<MainChapterComponent>(),
        Il2CppType.Of<EventChapterComponent>(),
    };
    static readonly Il2CppSystem.Type Badge = Il2CppType.Of<TrophyComponent>();

    public TextFitRunner(IntPtr ptr) : base(ptr) { }

    readonly HashSet<int> fitted = new();
    float nextAt;

    void Update()
    {
        if (Time.unscaledTime < nextAt) return;
        nextAt = Time.unscaledTime + Interval;
        try
        {
            foreach (var type in Screens)
                foreach (var found in UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None))
                    Fit(found.Cast<Component>());
            foreach (var found in UnityEngine.Object.FindObjectsByType(Badge, FindObjectsSortMode.None))
                FitBadge(found.Cast<Component>());
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
            nextAt += 30f;
        }
    }

    void Fit(Component screen)
    {
        foreach (var label in screen.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (!fitted.Add(label.GetInstanceID()) || label.enableAutoSizing || label.GetComponent<TextMarquee>() != null) continue;
            var size = label.fontSize;
            label.fontSizeMax = size;
            label.fontSizeMin = Mathf.Max(8f, size * MinScale);
            label.enableAutoSizing = true;
        }
    }

    void FitBadge(Component badge)
    {
        foreach (var label in badge.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (!fitted.Add(label.GetInstanceID())) continue;
            var size = label.enableAutoSizing ? label.fontSizeMax : label.fontSize;
            label.fontSizeMax = size;
            label.fontSizeMin = Mathf.Max(8f, size * BadgeMinScale);
            label.enableAutoSizing = true;
            label.textWrappingMode = TextWrappingModes.NoWrap;
        }
    }
}
