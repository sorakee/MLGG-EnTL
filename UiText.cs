using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Assets.Utilities;
using BepInEx;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GGXEnPatch;

/// <summary>
/// UI text that isn't story or master data: fixed prefab labels (キャンセル, 編成中...) and
/// strings the game builds from its own format strings (「5日23時間」 from 「{0}日{1}時間」).
/// Every TextMeshPro label is checked when its text is set and when it's enabled (prefab text
/// is deserialized, never set). Lookup order, on the text with \r\n and literal \n made plain
/// newlines:
///   1. the whole text in the pack's "ui" section, else a name or any master text;
///   2. a marquee "X&lt;space=…&gt;X" (scrolling labels repeat their text): X translated twice;
///   3. the text inside leading/trailing tags and spaces (「&lt;sprite…&gt;題名&lt;sprite…&gt;」), re-wrapped;
///      "ui" entries are also indexed by their inner text, so a different wrapper still matches;
///   4. "uiFormats" patterns; a Japanese capture (a name, a timer) must translate by these same
///      steps or the pattern doesn't apply, so 「あと{0}」 + 「{0}日{1}時間」 covers 「あと10日6時間」;
///   5. per piece between tags and line breaks (code that glues 「戦闘力」 to a number);
///   Text with 「 but no kana/kanji is looked up too: the Tweety post header, Name「Card」 @handle,
///   is glued from master text that is English already, and only its brackets need formatting.
///   6. on the typewriter labels (TextViewText; the Maze craft navigator, Navigator/Back/Text), a prefix
///      of a known text shows the same share of its English, also for "uiFormats" with numbers in them
///      (「素敵です！…<color>83%</color>の確率で…」 while typing). These skip step 5, so a half-typed line
///      never gets one word swapped in. The navigator's highlight sweep (an empty color tag moving
///      through the text) is dropped first.
///
/// SetText(string) and SetText(string, bool) share one native body, so only one is patched.
/// The ruby component (TextMeshProRuby) is left alone: the typewriter grows its Text one character
/// at a time, so rewriting it corrupts the next frame. Its converted output reaches the TMP label,
/// which is translated like any other.
/// </summary>
static class UiText
{
    const int CacheLimit = 8192;
    const int MaxDepth = 2;
    const string TypewriterLabel = "TextViewText";

    static readonly Dictionary<string, string> cache = new();  // results, misses included
    static readonly HashSet<int> fitted = new();
    static int revision = -1;
    static Dictionary<string, string> exact = new();  // "ui" by normalized text and by inner text
    static HashSet<string> masterEnglish = new();  // English of master text, for fitting labels that show it
    static string[] typed = Array.Empty<string>();  // exact keys sorted ordinally, for prefix search
    static string[] typedFormats = Array.Empty<string>();  // "uiFormats" keys sorted ordinally, likewise
    static Dictionary<string, string> formatEn = new();
    static Format[] formats = Array.Empty<Format>();
    static HashSet<string> logged;
    static StreamWriter log;

    static readonly Regex Marquee = new(@"^(.+?)(<space=[^>]+>)\1$", RegexOptions.Singleline);
    static readonly Regex Edges = new(@"^((?:<[^>]+>|\s)*)(.*?)((?:<[^>]+>|\s)*)$", RegexOptions.Singleline);
    static readonly Regex Pieces = new(@"(<[^>]+>|\n)");
    static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}");
    static readonly Regex Sweep = new(@"<color=[^>]*></color>|</color><color=[^>]*>");
    static readonly Regex Tags = new(@"(<[^>]+>)");
    static readonly Regex Digits = new(@"\d+");

    sealed class Format
    {
        public Regex Pattern;
        public string Anchor;  // longest literal piece: a cheap Contains() before the regex
        public string En;
    }

    internal static bool HasJapanese(string s)
    {
        foreach (var c in s)
            if (c >= '぀' && c <= 'ヿ' || c >= '㐀' && c <= '鿿' || c >= '！' && c <= '｠')
                return true;
        return false;
    }

    /// <summary>Kana or kanji: text that needs translating, unlike full-width digits and symbols.</summary>
    static bool HasWords(string s)
    {
        foreach (var c in s)
            if (c >= '぀' && c <= 'ヿ' || c >= '㐀' && c <= '鿿')
                return true;
        return false;
    }

    static string Normalize(string s) =>
        s.IndexOf('\r') >= 0 || s.Contains("\\n") ? s.Replace("\r\n", "\n").Replace("\\n", "\n") : s;

    /// <summary>English for a label's text, or the same string if there's none.</summary>
    internal static string Translate(string jp, TMP_Text label)
    {
        if (string.IsNullOrEmpty(jp)) return jp;
        var store = Plugin.Store;
        if (!HasJapanese(jp) && jp.IndexOf('「') < 0)
        {
            // Master text is swapped in the tables, so its label is set to English already: fit it all the same.
            if (label != null && Plugin.FitUi.Value && !IsTypewriter(label))
            {
                if (revision != store.Revision) Rebuild(store);
                if (masterEnglish.Contains(jp) || jp.IndexOf('<') >= 0 && masterEnglish.Contains(Edges.Match(jp).Groups[2].Value))
                    Fit(label, jp);
            }
            return jp;
        }
        if (label != null && KeepJapanese(label)) return jp;
        if (revision != store.Revision) Rebuild(store);
        string en;
        if (label != null && IsTypewriter(label))
        {
            var s = Sweep.Replace(Normalize(jp), "");  // new text every frame while typing: not cached
            // a finished text is its own prefix; on the navigator a short finished line wins over a longer one
            en = (IsNavigator(label) ? Exact(s, store) : null) ?? Typed(s) ?? Resolve(s, store, 0, split: false);
        }
        else if (!cache.TryGetValue(jp, out en))
        {
            en = Resolve(Normalize(jp), store, 0);
            if (cache.Count >= CacheLimit) cache.Clear();
            cache[jp] = en;
        }
        if (en == null)
        {
            if (Plugin.LogUntranslatedUi.Value) Log(jp, label);
            return jp;
        }
        if (Plugin.FitUi.Value && label != null)
        {
            if (IsNavigator(label)) Wrap(label);
            else Fit(label, en);
        }
        return en;
    }

    static bool IsTypewriter(TMP_Text label) => label.name == TypewriterLabel || IsNavigator(label);

    /// <summary>The Maze craft navigator's speech (Navigator/Back/Text (TMP)): a typewriter.</summary>
    static bool IsNavigator(TMP_Text label)
    {
        var back = label.transform.parent;
        return back != null && back.name == "Back" && back.parent != null && back.parent.name == "Navigator";
    }

    /// <summary>
    /// Labels left in Japanese on purpose: memory skill kind chips (攻撃 補助 回復 妨害 in one label,
    /// the active ones colored). The skill text says it all, and English chips would overflow.
    /// </summary>
    static bool KeepJapanese(TMP_Text label)
    {
        var parent = label.transform.parent;
        return parent != null && parent.name == "SkillKind";
    }

    static void Rebuild(TranslationStore store)
    {
        revision = store.Revision;
        cache.Clear();
        var map = new Dictionary<string, string>();
        foreach (var (jp, en) in store.Ui) map[Normalize(jp)] = Normalize(en);
        foreach (var (jp, en) in map.ToArray())
        {
            var inner = Edges.Match(jp).Groups[2].Value;
            if (inner.Length < jp.Length && HasJapanese(inner))
                map.TryAdd(inner, Edges.Match(en).Groups[2].Value);
        }
        typed = map.Keys.Where(k => k.Length >= 8).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var fmts = new Dictionary<string, string>();
        foreach (var (jp, en) in store.UiFormats) fmts.TryAdd(Normalize(jp), Normalize(en));
        formatEn = fmts;
        typedFormats = fmts.Keys.Where(k => k.Length >= 8).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        // Master text too (UI first): labels that show a table string the walker doesn't swap, and format
        // captures such as a gacha's name and description.
        var english = new HashSet<string>();
        foreach (var field in store.Master.Values)
            foreach (var (jp, en) in field)
            {
                map.TryAdd(Normalize(jp), Normalize(en));
                english.Add(en);
            }
        foreach (var en in store.MasterRows.Values) english.Add(en);
        exact = map;
        masterEnglish = english;

        var list = new List<Format>();
        foreach (var (jp, en) in store.UiFormats)
        {
            try
            {
                list.Add(Compile(Normalize(jp), Normalize(en)));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"UI format {jp}: {e.Message}");
            }
        }
        // Most literal text first, so 「{0}日{1}時間」 wins over 「{0}時間」.
        formats = list.OrderByDescending(f => f.Pattern.ToString().Length).ToArray();
    }

    static string Exact(string s, TranslationStore store) =>
        exact.TryGetValue(s, out var en) || store.Names.TryGetValue(s, out en) ? en : null;

    static string Resolve(string s, TranslationStore store, int depth, bool split = true)
    {
        var en = Exact(s, store);
        if (en != null) return en;

        var marquee = Marquee.Match(s);
        if (marquee.Success)
        {
            var half = Resolve(marquee.Groups[1].Value, store, depth, split);
            return half == null ? null : half + marquee.Groups[2].Value + half;
        }

        var edges = Edges.Match(s);
        var inner = edges.Groups[2].Value;
        if (inner.Length > 0 && inner.Length < s.Length)
        {
            en = Exact(inner, store) ?? Match(inner, store, depth);
            if (en != null) return edges.Groups[1].Value + en + edges.Groups[3].Value;
        }

        en = Match(s, store, depth);
        if (en != null || depth > 0 || !split) return en;
        return Split(s, store);
    }

    /// <summary>Each piece between tags and line breaks on its own; null if none translates.</summary>
    static string Split(string s, TranslationStore store)
    {
        var parts = Pieces.Split(s);
        if (parts.Length < 2) return null;
        var any = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var piece = parts[i].Trim();
            if (piece.Length == 0 || !HasJapanese(piece) || piece[0] == '<') continue;
            var en = Exact(piece, store) ?? Match(piece, store, MaxDepth - 1);
            if (en == null) continue;
            parts[i] = parts[i].Replace(piece, en);
            any = true;
        }
        return any ? string.Concat(parts) : null;
    }

    /// <summary>Typewriter labels show a growing prefix of their text: show the same share of the English.</summary>
    static string Typed(string s)
    {
        var edges = Edges.Match(s);
        var shown = edges.Groups[2].Value.TrimEnd('\\');  // half of a literal \n
        if (shown.Length == 0) return null;
        string en, full;
        var i = Array.BinarySearch(typed, shown, StringComparer.Ordinal);
        if (i < 0) i = ~i;
        if (i < typed.Length && typed[i].StartsWith(shown, StringComparison.Ordinal))
        {
            full = typed[i];
            en = exact[full];
        }
        else if (!TypedFormat(ref shown, out full, out en)) return null;
        if (shown.Length >= full.Length) return edges.Groups[1].Value + en;
        var n = (int)Math.Round(en.Length * (double)shown.Length / full.Length);
        var cut = en.Substring(0, Math.Min(n, en.Length));
        var open = cut.LastIndexOf('<');
        if (open > cut.LastIndexOf('>')) cut = cut.Substring(0, open);  // don't show half a tag
        return edges.Groups[1].Value + cut;
    }

    /// <summary>
    /// A typed prefix of a "uiFormats" text: digit runs outside tags become {0}, {1}... (a half-typed number
    /// too), the format whose Japanese starts with that is the one being typed, and its English gets the
    /// numbers typed so far. shown comes back in format form, for measuring the share.
    /// </summary>
    static bool TypedFormat(ref string shown, out string full, out string en)
    {
        full = en = null;
        var values = new List<string>();
        var key = new StringBuilder();
        foreach (var part in Tags.Split(shown))
        {
            if (part.StartsWith("<", StringComparison.Ordinal))
            {
                key.Append(part);
                continue;
            }
            var at = 0;
            foreach (Match d in Digits.Matches(part))
            {
                key.Append(part, at, d.Index - at).Append('{').Append(values.Count).Append('}');
                values.Add(d.Value);
                at = d.Index + d.Length;
            }
            key.Append(part, at, part.Length - at);
        }
        var k = key.ToString();
        var i = Array.BinarySearch(typedFormats, k, StringComparer.Ordinal);
        if (i < 0) i = ~i;
        if (i >= typedFormats.Length || !typedFormats[i].StartsWith(k, StringComparison.Ordinal)) return false;
        full = typedFormats[i];
        var format = formatEn[full];
        var sb = new StringBuilder();
        var pos = 0;
        foreach (Match p in Placeholder.Matches(format))
        {
            sb.Append(format, pos, p.Index - pos);
            var n = int.Parse(p.Groups[1].Value);
            if (n < values.Count) sb.Append(values[n]);
            pos = p.Index + p.Length;
        }
        sb.Append(format, pos, format.Length - pos);
        en = sb.ToString();
        shown = k;
        return true;
    }

    /// <summary>Navigator speech wraps (its prefab doesn't) and keeps its size: auto-sizing would shrink it mid-line.</summary>
    static void Wrap(TMP_Text label)
    {
        if (!fitted.Add(label.GetInstanceID())) return;
        label.textWrappingMode = TextWrappingModes.Normal;
    }

    static Format Compile(string jp, string en)
    {
        var pattern = new StringBuilder("^");
        var seen = new HashSet<string>();
        var anchor = "";
        var at = 0;
        foreach (Match m in Placeholder.Matches(jp))
        {
            var literal = jp.Substring(at, m.Index - at);
            if (literal.Length > anchor.Length) anchor = literal;
            pattern.Append(Regex.Escape(literal));
            var name = "p" + m.Groups[1].Value;
            pattern.Append(seen.Add(name) ? $"(?<{name}>.*?)" : $"\\k<{name}>");
            at = m.Index + m.Length;
        }
        var tail = jp.Substring(at);
        if (tail.Length > anchor.Length) anchor = tail;
        pattern.Append(Regex.Escape(tail)).Append('$');
        return new Format
        {
            Pattern = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant),
            Anchor = anchor,
            En = en,
        };
    }

    static string Match(string jp, TranslationStore store, int depth)
    {
        foreach (var f in formats)
        {
            if (f.Anchor.Length > 0 && !jp.Contains(f.Anchor, StringComparison.Ordinal)) continue;
            var m = f.Pattern.Match(jp);
            if (!m.Success) continue;
            var en = Fill(f.En, m, store, depth);
            if (en != null) return en;
        }
        return null;
    }

    /// <summary>The English format with the captured values put in; null if a capture is Japanese
    /// with no English of its own.</summary>
    static string Fill(string format, Match m, TranslationStore store, int depth)
    {
        var sb = new StringBuilder();
        var at = 0;
        foreach (Match p in Placeholder.Matches(format))
        {
            sb.Append(format, at, p.Index - at);
            at = p.Index + p.Length;
            var g = m.Groups["p" + p.Groups[1].Value];
            if (!g.Success)
            {
                sb.Append(p.Value);
                continue;
            }
            var value = g.Value;
            if (HasWords(value))  // full-width digits (「１つ」) pass through as they are
            {
                value = depth < MaxDepth ? Resolve(value, store, depth + 1) : Exact(value, store);
                if (value == null) return null;
            }
            sb.Append(value);
        }
        return sb.Append(format, at, format.Length - at).ToString();
    }

    /// <summary>English runs longer: let a translated label shrink (down to 60%) rather than overflow,
    /// and keep a one-word label on one line (shrink rather than break the word). Labels that show master
    /// English (trophy titles, item names) are fitted too, though their text is already English. Labels sized by a
    /// ContentSizeFitter grow instead, and marquee labels (TextMarquee on the label) scroll what doesn't fit,
    /// so both are left alone: shrinking a marquee label only makes long names smaller than short ones.</summary>
    static void Fit(TMP_Text label, string en)
    {
        if (!fitted.Add(label.GetInstanceID())) return;
        if (label.enableAutoSizing || label.GetComponent<ContentSizeFitter>() != null || label.GetComponent<TextMarquee>() != null) return;
        var size = label.fontSize;
        label.fontSizeMax = size;
        label.fontSizeMin = Mathf.Max(8f, size * 0.6f);
        label.enableAutoSizing = true;
        var plain = Edges.Match(en).Groups[2].Value;
        if (plain.IndexOf(' ') < 0 && plain.IndexOf('\n') < 0) label.textWrappingMode = TextWrappingModes.NoWrap;
    }

    /// <summary>[Debug] LogUntranslatedUi: each Japanese label text without English, once, with where it was
    /// seen, to ui-untranslated.tsv (tools/ui_text.py build --harvest reads it).</summary>
    static void Log(string jp, TMP_Text label)
    {
        try
        {
            if (logged == null)
            {
                var path = Path.Combine(Paths.PluginPath, "GGXEnPatch", "ui-untranslated.tsv");
                logged = File.Exists(path)
                    ? File.ReadAllLines(path).Select(l => l.Split('\t')[0]).ToHashSet()
                    : new HashSet<string>();
                log = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
            }
            var escaped = jp.Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");
            if (!logged.Add(escaped)) return;
            log.WriteLine($"{escaped}\t{PathOf(label)}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    static string PathOf(Component c)
    {
        if (c == null) return "";
        var names = new List<string>();
        for (var t = c.transform; t != null && names.Count < 12; t = t.parent) names.Add(t.name);
        names.Reverse();
        return string.Join("/", names);
    }
}

[HarmonyPatch]
static class UiTextPatches
{
    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.text), MethodType.Setter)]
    [HarmonyPrefix]
    static void SetTextProperty(TMP_Text __instance, ref string value)
    {
        try
        {
            value = UiText.Translate(value, __instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.SetText), typeof(string))]
    [HarmonyPrefix]
    static void SetText(TMP_Text __instance, ref string sourceText)
    {
        try
        {
            sourceText = UiText.Translate(sourceText, __instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    // Prefab text is deserialized into the label, not set: check it when the label turns on.
    [HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable")]
    [HarmonyPostfix]
    static void Enabled(TextMeshProUGUI __instance)
    {
        try
        {
            var jp = __instance.text;
            var en = UiText.Translate(jp, __instance);
            if (!ReferenceEquals(en, jp)) __instance.text = en;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }
}
