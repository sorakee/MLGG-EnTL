using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Api.MemoryDB;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace GGXEnPatch;

/// <summary>
/// Master data text: choice answers, scene and episode titles, item names, menus...
/// Every couple of seconds, walks the memory tables on MemoryDB.Instance that the pack
/// has text for and swaps the string properties named in its "master" section for
/// English, in place. A table is walked again only when it changes (another table
/// object, count or first/last row), e.g. after a master data update.
///
/// Nested fields come by shape (build_pack.py): "Field[]" is a list, whose string elements
/// are swapped, or every string property of its element objects (skill description
/// templates, enemy stage skills); "Field{}" is a dictionary keyed by an enum, whose
/// string values are swapped (relic names by rarity). Their English is by Japanese only.
///
/// Display only: only text properties are set, never ids, and none of this is sent back
/// to the server. No memory table indexes a text field, so lookups keep working.
/// Swapped objects keep their English, so master text edited in a pack shows after the
/// next master data load (a game restart), unlike story lines.
/// </summary>
public class MasterTextRunner : MonoBehaviour
{
    const float Interval = 2f;

    public MasterTextRunner(IntPtr ptr) : base(ptr) { }

    float nextAt;
    int revision = -1;
    Table[] tables = Array.Empty<Table>();

    void Update()
    {
        if (Time.unscaledTime < nextAt) return;
        nextAt = Time.unscaledTime + Interval;
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
            nextAt += 30f;
        }
    }

    void Tick()
    {
        var store = Plugin.Store;
        store.ReloadIfChanged();
        if (revision != store.Revision)
        {
            revision = store.Revision;
            tables = Table.All(store);
        }
        if (tables.Length == 0) return;
        var db = MemoryDB.Instance;
        if (db == null) return;

        int swapped = 0, walked = 0;
        foreach (var table in tables)
        {
            var n = table.Walk(db, store);
            if (n < 0) continue;
            walked++;
            swapped += n;
        }
        if (swapped > 0) Plugin.Logger.LogInfo($"Master text: {swapped} strings swapped in {walked} table(s).");
    }

    enum Shape { Plain, List, Dictionary }

    sealed class Field
    {
        public string Name;
        public PropertyInfo Property;
        public Dictionary<string, string> ByJp;
        public Shape Shape;
        public PropertyInfo[] ElementStrings;  // List of objects: their settable string properties
        public object[] Keys;  // Dictionary: every value of its enum key type
        public MethodInfo ContainsKey, GetItem, SetItem;

        /// <summary>A pack field ("Name", "Name[]", "Name{}") on an entity type, or null if it can't be swapped.</summary>
        public static Field For(Type entity, string name, Dictionary<string, string> byJp)
        {
            var shape = name.EndsWith("[]", StringComparison.Ordinal) ? Shape.List
                : name.EndsWith("{}", StringComparison.Ordinal) ? Shape.Dictionary : Shape.Plain;
            var property = entity.GetProperty(shape == Shape.Plain ? name : name[..^2],
                BindingFlags.Public | BindingFlags.Instance);
            if (property == null) return null;
            var type = property.PropertyType;
            var field = new Field { Name = name, Property = property, ByJp = byJp, Shape = shape };
            switch (shape)
            {
                case Shape.Plain:
                    return type == typeof(string) && property.CanWrite ? field : null;
                case Shape.List:
                    if (type == typeof(Il2CppStringArray)) return field;
                    var element = ArrayElement(type);
                    if (element == null) return null;
                    field.ElementStrings = element.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.PropertyType == typeof(string) && p.CanWrite).ToArray();
                    return field.ElementStrings.Length > 0 ? field : null;
                default:
                    var args = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
                    if (args.Length != 2 || !args[0].IsEnum || args[1] != typeof(string)) return null;
                    field.Keys = Enum.GetValues(args[0]).Cast<object>().ToArray();
                    field.ContainsKey = type.GetMethod("ContainsKey", new[] { args[0] });
                    field.GetItem = type.GetMethod("get_Item", new[] { args[0] });
                    field.SetItem = type.GetMethod("set_Item", new[] { args[0], typeof(string) });
                    return field.ContainsKey != null && field.GetItem != null && field.SetItem != null ? field : null;
            }
        }

        static Type ArrayElement(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Il2CppArrayBase<>))
                    return t.GetGenericArguments()[0];
            return null;
        }

        /// <summary>Swaps this field's text on one row object; returns how many strings.</summary>
        public int Swap(object obj, Dictionary<string, string> rows, string row)
        {
            if (Shape == Shape.Plain)
            {
                var jp = (string)Property.GetValue(obj);
                if (string.IsNullOrEmpty(jp)) return 0;
                string en = null;
                if (row != null) rows.TryGetValue(row + Name, out en);
                if (en == null) ByJp?.TryGetValue(jp, out en);
                if (en == null || en == jp) return 0;
                Property.SetValue(obj, en);
                return 1;
            }
            if (ByJp == null) return 0;
            var value = Property.GetValue(obj);
            if (value == null) return 0;
            var swapped = 0;
            if (value is Il2CppStringArray strings)
            {
                for (var i = 0; i < strings.Length; i++)
                    if (Lookup(strings[i]) is { } en)
                    {
                        strings[i] = en;
                        swapped++;
                    }
            }
            else if (value is Il2CppArrayBase list)
            {
                foreach (var element in list)
                {
                    if (element == null) continue;
                    foreach (var p in ElementStrings)
                        if (Lookup((string)p.GetValue(element)) is { } en)
                        {
                            p.SetValue(element, en);
                            swapped++;
                        }
                }
            }
            else
            {
                foreach (var key in Keys)
                {
                    var args = new[] { key };
                    if (!(bool)ContainsKey.Invoke(value, args)) continue;
                    if (Lookup((string)GetItem.Invoke(value, args)) is { } en)
                    {
                        SetItem.Invoke(value, new[] { key, en });
                        swapped++;
                    }
                }
            }
            return swapped;
        }

        string Lookup(string jp) =>
            !string.IsNullOrEmpty(jp) && ByJp.TryGetValue(jp, out var en) && en != jp ? en : null;
    }

    /// <summary>One memory table on MemoryDB with text to swap.</summary>
    sealed class Table
    {
        string name;
        PropertyInfo onDb, count, id;
        MethodInfo item;
        Field[] fields;
        bool hasRows;
        (IntPtr table, int count, IntPtr first, IntPtr last) seen;

        public static Table[] All(TranslationStore store)
        {
            var wanted = store.Master.Keys.Select(k => k.Split('.')[0])
                .Concat(store.MasterRows.Keys.Select(k => k.Split('/')[0]))
                .ToHashSet();
            var found = new List<Table>();
            foreach (var property in typeof(MemoryDB).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name.StartsWith("_", StringComparison.Ordinal)) continue;  // interop's _X_k__BackingField twin
                var tableType = property.PropertyType;
                var entity = BaseMemoryTable(tableType)?.GetGenericArguments()[0];
                if (entity == null || !wanted.Remove(entity.Name)) continue;
                var table = For(property, tableType, entity, store);
                if (table != null) found.Add(table);
            }
            foreach (var missing in wanted)
                Plugin.Logger.LogWarning($"Master text for {missing}: no such table on MemoryDB, skipped.");
            return found.ToArray();
        }

        static Type BaseMemoryTable(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.IsGenericType && t.Name.StartsWith("MemoryTable", StringComparison.Ordinal))
                    return t;
            return null;
        }

        static Table For(PropertyInfo onDb, Type tableType, Type entity, TranslationStore store)
        {
            var name = entity.Name;
            var names = store.Master.Keys
                .Where(k => k.StartsWith(name + ".", StringComparison.Ordinal))
                .Select(k => k.Substring(name.Length + 1))
                .ToHashSet();
            var hasRows = false;
            foreach (var key in store.MasterRows.Keys.Where(k => k.StartsWith(name + "/", StringComparison.Ordinal)))
            {
                names.Add(key.Substring(key.LastIndexOf('/') + 1));
                hasRows = true;
            }
            var fields = new List<Field>();
            foreach (var field in names)
            {
                store.Master.TryGetValue(name + "." + field, out var byJp);
                var f = Field.For(entity, field, byJp);
                if (f == null)
                {
                    Plugin.Logger.LogWarning($"Master text for {name}.{field}: no settable text of that shape, skipped.");
                    continue;
                }
                fields.Add(f);
            }
            var count = tableType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var item = tableType.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(int) }, null);
            if (fields.Count == 0 || count == null || item == null)
            {
                if (fields.Count > 0) Plugin.Logger.LogWarning($"Master text for {name}: can't read its table, skipped.");
                return null;
            }
            return new Table
            {
                name = name,
                onDb = onDb,
                count = count,
                item = item,
                id = hasRows ? entity.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) : null,
                hasRows = hasRows,
                fields = fields.ToArray(),
            };
        }

        /// <summary>Swaps the table's strings if it changed; returns how many, or -1 if not walked.</summary>
        public int Walk(MemoryDB db, TranslationStore store)
        {
            if (onDb.GetValue(db) is not Il2CppObjectBase table) return -1;
            var n = (int)count.GetValue(table);
            if (n == 0) return -1;
            var state = (table.Pointer, n, Pointer(table, 0), Pointer(table, n - 1));
            if (state == seen) return -1;
            seen = state;

            var swapped = 0;
            for (var i = 0; i < n; i++)
            {
                var obj = item.Invoke(table, new object[] { i });
                if (obj == null) continue;
                var row = hasRows && id != null ? $"{name}/{id.GetValue(obj)}/" : null;
                foreach (var field in fields)
                    swapped += field.Swap(obj, store.MasterRows, row);
            }
            return swapped;
        }

        IntPtr Pointer(Il2CppObjectBase table, int index) =>
            item.Invoke(table, new object[] { index }) is Il2CppObjectBase o ? o.Pointer : IntPtr.Zero;
    }
}
