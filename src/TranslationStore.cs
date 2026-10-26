using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GGXEnPatch;

/// <summary>
/// Japanese -> English lookups loaded from every *.json pack in a folder (made
/// by pack/build_pack.py). Full packs load in name order and later ones win, so a
/// "zz_" pack overrides the main one; update packs follow (see LoadOrder, PackInfo).
/// Packs are reloaded when they change.
/// </summary>
public class TranslationStore
{
    const int SupportedFormat = 2;  // 2: update packs (null entries remove)
    const long ReloadCheckIntervalMs = 1000;

    readonly string dir;
    long nextCheck;
    string signature = "";

    public Dictionary<string, string> Lines { get; private set; } = new();
    public Dictionary<string, string> Names { get; private set; } = new();
    /// <summary>Master data text: "Table.Field" -> Japanese -> English.</summary>
    public Dictionary<string, Dictionary<string, string>> Master { get; private set; } = new();
    /// <summary>Master data text for one row, checked before Master: "Table/Id/Field" -> English.</summary>
    public Dictionary<string, string> MasterRows { get; private set; } = new();
    /// <summary>Fixed UI text (prefab labels and the like), by the label's whole Japanese text.</summary>
    public Dictionary<string, string> Ui { get; private set; } = new();
    /// <summary>The game's format strings ("{0}日{1}時間") -> English formats, matched as patterns.</summary>
    public Dictionary<string, string> UiFormats { get; private set; } = new();
    /// <summary>Goes up on every reload, so caches built from the maps above know to rebuild.</summary>
    public int Revision { get; private set; }

    public string Dir => dir;

    public TranslationStore(string dir)
    {
        this.dir = dir;
    }

    public string Name(string jp) =>
        jp != null && Names.TryGetValue(jp, out var en) ? en : jp;

    /// <summary>Cheap enough to call per line: only touches the disk once a second.</summary>
    public void ReloadIfChanged()
    {
        var now = Environment.TickCount64;
        if (now < nextCheck) return;
        nextCheck = now + ReloadCheckIntervalMs;
        if (Signature() != signature) Reload();
    }

    public void Reload()
    {
        Directory.CreateDirectory(dir);
        var lines = new Dictionary<string, string>();
        var names = new Dictionary<string, string>();
        var master = new Dictionary<string, Dictionary<string, string>>();
        var masterRows = new Dictionary<string, string>();
        var ui = new Dictionary<string, string>();
        var uiFormats = new Dictionary<string, string>();
        foreach (var path in LoadOrder())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("format", out var format) && format.GetInt32() > SupportedFormat)
                {
                    Plugin.Logger.LogWarning($"{Path.GetFileName(path)} needs a newer plugin, skipped.");
                    continue;
                }
                Merge(root, "lines", lines);
                Merge(root, "names", names);
                Merge(root, "masterRows", masterRows);
                Merge(root, "ui", ui);
                Merge(root, "uiFormats", uiFormats);
                if (root.TryGetProperty("master", out var fields))
                    foreach (var field in fields.EnumerateObject())
                    {
                        if (!master.TryGetValue(field.Name, out var byJp))
                            master[field.Name] = byJp = new Dictionary<string, string>();
                        Merge(fields, field.Name, byJp);
                    }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Couldn't read {Path.GetFileName(path)}: {e.Message}");
            }
        }
        Lines = lines;
        Names = names;
        Master = master;
        MasterRows = masterRows;
        Ui = ui;
        UiFormats = uiFormats;
        Revision++;
        signature = Signature();
        var masterCount = master.Values.Sum(m => m.Count) + masterRows.Count;
        Plugin.Logger.LogInfo($"Translations: {lines.Count} lines, {names.Count} names, {masterCount} master strings, " +
                              $"{ui.Count + uiFormats.Count} UI strings from {PackFiles().Count()} pack(s).");
    }

    IEnumerable<string> PackFiles() => PackFiles(dir);

    public static IEnumerable<string> PackFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    /// <summary>The published and dev packs in name order, then each channel's update packs in sequence
    /// (only those made for the base that is loaded: one left over from an older base is skipped), then
    /// packs without a header (a player's own overrides, "zz_mine.json") in name order, so they win.</summary>
    List<string> LoadOrder()
    {
        var packs = PackFiles().Select(PackInfo.Read).ToList();
        var full = packs.Where(p => !p.IsUpdate && !p.IsOwn).ToList();
        var bases = full.Where(p => p.Id != null).Select(p => (p.Channel, p.Id)).ToHashSet();
        var order = full.Select(p => p.Path).ToList();
        foreach (var update in packs.Where(p => p.IsUpdate).OrderBy(p => p.Channel).ThenBy(p => p.Seq))
        {
            if (bases.Contains((update.Channel, update.Base)))
                order.Add(update.Path);
            else
                Plugin.Logger.LogWarning($"{Path.GetFileName(update.Path)} is for base {update.Channel} {update.Base}, " +
                                         "which isn't installed; skipped.");
        }
        order.AddRange(packs.Where(p => p.IsOwn).Select(p => p.Path));
        return order;
    }

    string Signature() =>
        string.Join("|", PackFiles().Select(p =>
        {
            var info = new FileInfo(p);
            return $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));

    /// <summary>Adds a section's entries; null (in an update pack) removes one.</summary>
    static void Merge(JsonElement root, string section, Dictionary<string, string> into)
    {
        if (!root.TryGetProperty(section, out var entries)) return;
        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Null)
                into.Remove(entry.Name);
            else
                into[entry.Name] = entry.Value.GetString();
        }
    }
}

/// <summary>
/// A pack's header (pack/build_pack.py, pack/publish.py): "channel" (main, r18), "kind" (dev = a local
/// build, base = a published full pack, update = changes on top of base "base", applied in "seq" order)
/// and "id". A pack without a header is the old full translations.json (plugin 0.4, channel main, no
/// id) or, under any other name, a player's own pack.
/// </summary>
public sealed class PackInfo
{
    public const string LegacyBase = "translations.json";
    static readonly Regex StringField = new("\"(channel|kind|id|base)\"\\s*:\\s*\"([^\"]*)\"");
    static readonly Regex SeqField = new("\"seq\"\\s*:\\s*(\\d+)");

    public string Path, Channel = "main", Kind, Id, Base;
    public int Seq;

    public bool IsUpdate => Kind == "update";
    public bool IsLegacyBase => Kind == null &&
        System.IO.Path.GetFileName(Path).Equals(LegacyBase, StringComparison.OrdinalIgnoreCase);
    public bool IsOwn => Kind == null && !IsLegacyBase;

    /// <summary>Reads only the start of the file: the header comes before the first section.</summary>
    public static PackInfo Read(string path)
    {
        var info = new PackInfo { Path = path };
        try
        {
            var buffer = new char[4096];
            int n;
            using (var reader = new StreamReader(path))
                n = reader.ReadBlock(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, n);
            var end = head.IndexOf("\"lines\"", StringComparison.Ordinal);
            if (end >= 0) head = head.Substring(0, end);
            foreach (Match m in StringField.Matches(head))
            {
                var value = m.Groups[2].Value;
                switch (m.Groups[1].Value)
                {
                    case "channel": info.Channel = value; break;
                    case "kind": info.Kind = value; break;
                    case "id": info.Id = value; break;
                    case "base": info.Base = value; break;
                }
            }
            if (SeqField.Match(head) is { Success: true } seq) info.Seq = int.Parse(seq.Groups[1].Value);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't read the header of {System.IO.Path.GetFileName(path)}: {e.Message}");
        }
        return info;
    }
}
