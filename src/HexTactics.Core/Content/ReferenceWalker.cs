using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HexTactics.Core.Content;

public sealed record ReferenceInfo(Def? Owner, string Path, DefCategory Category, string Id)
{
    public string OwnerLabel => Owner == null ? "Правила" : $"{Owner.Category}: {Owner.DisplayName}";
}

/// <summary>
/// Walks definitions via reflection and reports every reference ([EdRef], [EdKeyRef],
/// synergy operands). Used by validation, delete-dependency checks and renaming, so new
/// fields become checked automatically once they carry an attribute.
/// </summary>
public static class ReferenceWalker
{
    public delegate void Visitor(Def? owner, string path, DefCategory category, string value, Action<string> set, EdRefAttribute? attr);

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropCache = new();

    public static PropertyInfo[] PropsOf(Type t) =>
        PropCache.GetOrAdd(t, tt => tt.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(ContentPart.Extra))
            .ToArray());

    public static void Walk(ContentDatabase db, Visitor visitor)
    {
        foreach (var def in db.AllDefs()) WalkObject(def, def, def.Id, visitor, 0);
        WalkObject(null, db.Rules, "rules", visitor, 0);
    }

    public static void WalkDef(Def def, Visitor visitor) => WalkObject(def, def, def.Id, visitor, 0);

    private static bool IsContentClass(Type t) =>
        t.IsClass && t != typeof(string) && t.Namespace != null && t.Namespace.StartsWith("HexTactics.Core.Content", StringComparison.Ordinal);

    private static void WalkObject(Def? owner, object obj, string path, Visitor v, int depth)
    {
        if (depth > 16) return;

        if (obj is SynergyOperandDef op)
        {
            if (op.Kind == SynergyOperandKind.CellEffect)
                v(owner, path + ".value", DefCategory.CellEffect, op.Value, x => op.Value = x, null);
            return;
        }

        foreach (var prop in PropsOf(obj.GetType()))
        {
            object? value;
            try
            {
                value = prop.GetValue(obj);
            }
            catch
            {
                continue;
            }
            if (value == null) continue;
            if (!EdUtil.IsShown(obj, prop)) continue;
            string p = path + "." + prop.Name;

            var refAttr = EdUtil.Attr<EdRefAttribute>(prop);
            if (refAttr != null)
            {
                if (value is string s)
                {
                    var pi = prop;
                    v(owner, p, refAttr.Category, s, x => pi.SetValue(obj, x), refAttr);
                }
                else if (value is List<string> list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        int idx = i;
                        v(owner, $"{p}[{i}]", refAttr.Category, list[i], x => list[idx] = x, refAttr);
                    }
                }
                continue;
            }

            var keyRef = EdUtil.Attr<EdKeyRefAttribute>(prop);
            if (keyRef != null && value is IDictionary dict)
            {
                var keys = dict.Keys.Cast<object>().Select(k => k.ToString() ?? "").ToList();
                foreach (var key in keys)
                {
                    string k = key;
                    v(owner, $"{p}[{k}]", keyRef.Category, k, x =>
                    {
                        if (x == k || dict.Contains(x)) return;
                        var val = dict[k];
                        dict.Remove(k);
                        dict[x] = val;
                    }, null);
                }
                continue;
            }

            if (value is string) continue;
            if (value is IList l)
            {
                for (int i = 0; i < l.Count; i++)
                {
                    var item = l[i];
                    if (item != null && IsContentClass(item.GetType())) WalkObject(owner, item, $"{p}[{i}]", v, depth + 1);
                }
                continue;
            }
            if (IsContentClass(value.GetType())) WalkObject(owner, value, p, v, depth + 1);
        }
    }

    public static List<ReferenceInfo> FindReferences(ContentDatabase db, DefCategory category, string id)
    {
        var result = new List<ReferenceInfo>();
        Walk(db, (owner, path, cat, value, _, _) =>
        {
            if (cat == category && ContentRef.Refers(value, id) && !(owner != null && owner.Category == category && owner.Id == id && path == owner.Id + ".Id"))
                result.Add(new ReferenceInfo(owner, path, cat, value));
        });
        return result;
    }

    /// <summary>
    /// Every reference of the content grouped by its target (category, bare id) — one walk instead of one
    /// per lookup (publication of many changed objects in a large library).
    /// </summary>
    public static Dictionary<(DefCategory Category, string Id), List<ReferenceInfo>> BuildIndex(ContentDatabase db)
    {
        var index = new Dictionary<(DefCategory, string), List<ReferenceInfo>>();
        Walk(db, (owner, path, cat, value, _, _) =>
        {
            if (string.IsNullOrEmpty(value)) return;
            var id = ContentRef.IdOf(value);
            if (owner != null && owner.Category == cat && owner.Id == id && path == owner.Id + ".Id") return;
            if (!index.TryGetValue((cat, id), out var list)) index[(cat, id)] = list = new List<ReferenceInfo>();
            list.Add(new ReferenceInfo(owner, path, cat, value));
        });
        return index;
    }

    public static void ReplaceReferences(ContentDatabase db, DefCategory category, string oldId, string newId)
    {
        var setters = new List<(Action<string> Set, string Value)>();
        Walk(db, (_, _, cat, value, set, _) =>
        {
            if (cat == category && ContentRef.Refers(value, oldId)) setters.Add((set, value));
        });
        // Pinned references keep their version: "old@2" → "new@2".
        foreach (var (set, value) in setters)
        {
            var (_, v) = ContentRef.Parse(value);
            set(v == null ? newId : ContentRef.Pin(newId, v.Value));
        }
    }
}
