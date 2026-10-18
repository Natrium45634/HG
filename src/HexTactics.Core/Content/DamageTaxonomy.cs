using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>An element with inherited fields resolved (docs/DECISIONS_2.md §29).</summary>
public sealed class ResolvedElement
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The element and its ancestors, nearest first.</summary>
    public List<string> Chain { get; init; } = new();
    public string Color { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Vfx { get; init; } = "";
    public List<string> DamageTypes { get; init; } = new();
    public List<ElementStatusDef> Statuses { get; init; } = new();
    public List<string> CellEffects { get; init; } = new();
    public double CellEffectChance { get; init; } = 1;
    public List<ElementInteractionDef> Interactions { get; init; } = new();
}

/// <summary>
/// Damage type hierarchy, elements with inheritance and damage matches ("*", type, "el:", "cat:", "tag:").
/// Built from a content database; a simulation keeps one (content does not change during a battle).
/// </summary>
public sealed class DamageTaxonomy
{
    public const string Any = "*";
    public const string ElementPrefix = "el:";
    public const string CategoryPrefix = "cat:";
    public const string TagPrefix = "tag:";
    private const int MaxDepth = 16;

    private readonly ContentDatabase _db;
    private readonly Dictionary<string, HashSet<string>> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedElement?> _elements = new(StringComparer.Ordinal);

    public DamageTaxonomy(ContentDatabase db)
    {
        _db = db;
    }

    /// <summary>The damage type and its ancestors, nearest first (cycle-safe).</summary>
    public static List<string> TypeChain(ContentDatabase db, string damageType)
    {
        var list = new List<string>();
        var cur = damageType;
        while (!string.IsNullOrEmpty(cur) && list.Count < MaxDepth && !list.Contains(cur))
        {
            list.Add(cur);
            cur = db.DamageType(cur)?.Parent ?? "";
        }
        return list;
    }

    /// <summary>The element and its ancestors, nearest first (cycle-safe).</summary>
    public static List<string> ElementChain(ContentDatabase db, string element)
    {
        var list = new List<string>();
        var cur = element;
        while (!string.IsNullOrEmpty(cur) && list.Count < MaxDepth && !list.Contains(cur))
        {
            if (db.Element(cur) == null) break;
            list.Add(cur);
            cur = db.Element(cur)!.Parent;
        }
        return list;
    }

    /// <summary>Element of a damage type (its own or the nearest ancestor's).</summary>
    public static string ElementOf(ContentDatabase db, string damageType)
    {
        foreach (var t in TypeChain(db, damageType))
        {
            var e = db.DamageType(t)?.Element;
            if (!string.IsNullOrEmpty(e)) return e!;
        }
        return "";
    }

    public string ElementOf(string damageType) => ElementOf(_db, damageType);

    /// <summary>Every match key the damage type answers to.</summary>
    public IReadOnlyCollection<string> KeysOf(string damageType)
    {
        if (_keys.TryGetValue(damageType, out var set)) return set;
        set = new HashSet<string>(StringComparer.Ordinal) { Any };
        foreach (var t in TypeChain(_db, damageType))
        {
            set.Add(t);
            var d = _db.DamageType(t);
            if (d == null) continue;
            set.Add(CategoryPrefix + d.Kind);
            if (d.Kind == DamageCategory.Custom && !string.IsNullOrEmpty(d.CustomCategory)) set.Add(CategoryPrefix + d.CustomCategory);
            foreach (var tag in d.Tags) set.Add(TagPrefix + tag);
        }
        foreach (var e in ElementChain(_db, ElementOf(damageType))) set.Add(ElementPrefix + e);
        _keys[damageType] = set;
        return set;
    }

    public bool Matches(string match, string damageType) =>
        string.IsNullOrEmpty(match) || KeysOf(damageType).Contains(match);

    public bool MatchesAny(IReadOnlyList<string> matches, string damageType)
    {
        if (matches.Count == 0) return true;
        foreach (var m in matches)
            if (Matches(m, damageType))
                return true;
        return false;
    }

    /// <summary>Is the element (or one of its ancestors) the given one?</summary>
    public bool ElementIs(string element, string ancestorOrSelf) =>
        !string.IsNullOrEmpty(element) && ElementChain(_db, element).Contains(ancestorOrSelf);

    public ResolvedElement? Element(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_elements.TryGetValue(id, out var r)) return r;
        r = Resolve(_db, id);
        _elements[id] = r;
        return r;
    }

    public static ResolvedElement? Resolve(ContentDatabase db, string id)
    {
        var chain = ElementChain(db, id);
        if (chain.Count == 0) return null;
        var defs = chain.Select(c => db.Element(c)!).ToList();
        string First(Func<ElementDef, string> f) => defs.Select(f).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
        List<T> FirstList<T>(Func<ElementDef, List<T>> f)
        {
            foreach (var d in defs)
            {
                var l = f(d);
                if (l.Count > 0 || d.NoListInheritance) return new List<T>(l);
            }
            return new List<T>();
        }
        // Interactions: own entries override inherited ones with the same partner element.
        var interactions = new List<ElementInteractionDef>();
        foreach (var d in defs)
            foreach (var i in d.Interactions)
                if (!interactions.Any(x => x.With == i.With))
                    interactions.Add(i);
        return new ResolvedElement
        {
            Id = id,
            Name = defs[0].DisplayName,
            Chain = chain,
            Color = First(d => d.Color),
            Icon = First(d => d.Icon),
            Vfx = First(d => d.Vfx),
            DamageTypes = FirstList(d => d.DamageTypes),
            Statuses = FirstList(d => d.Statuses),
            CellEffects = FirstList(d => d.CellEffects),
            CellEffectChance = defs[0].CellEffectChance,
            Interactions = interactions,
        };
    }

    /// <summary>Colour of a damage type: its own, its element's, then its parent's.</summary>
    public string ColorOf(string damageType)
    {
        foreach (var t in TypeChain(_db, damageType))
        {
            var d = _db.DamageType(t);
            if (d == null) continue;
            if (!string.IsNullOrEmpty(d.Color)) return d.Color;
            var el = Element(d.Element);
            if (el != null && !string.IsNullOrEmpty(el.Color)) return el.Color;
        }
        return "#d0d0d0";
    }

    public string NameOfMatch(string match)
    {
        if (string.IsNullOrEmpty(match) || match == Any) return "любой урон";
        if (match.StartsWith(ElementPrefix, StringComparison.Ordinal))
            return "элемент «" + (_db.Element(match[ElementPrefix.Length..])?.DisplayName ?? match[ElementPrefix.Length..]) + "»";
        if (match.StartsWith(CategoryPrefix, StringComparison.Ordinal)) return "категория «" + CategoryName(match[CategoryPrefix.Length..]) + "»";
        if (match.StartsWith(TagPrefix, StringComparison.Ordinal)) return "тег " + match[TagPrefix.Length..];
        return _db.DamageType(match)?.DisplayName ?? match;
    }

    public static string CategoryName(string c) => c switch
    {
        nameof(DamageCategory.Physical) => "физический",
        nameof(DamageCategory.Magical) => "магический",
        nameof(DamageCategory.Elemental) => "элементальный",
        nameof(DamageCategory.Environment) => "окружение",
        nameof(DamageCategory.Periodic) => "периодический",
        nameof(DamageCategory.Custom) => "пользовательский",
        _ => c,
    };

    /// <summary>All matches the editor can offer: any, damage types, elements, categories and custom categories.</summary>
    public static List<(string Value, string Label)> MatchOptions(ContentDatabase db)
    {
        var list = new List<(string, string)> { (Any, "любой урон") };
        foreach (var d in db.All<DamageTypeDef>()) list.Add((d.Id, $"тип: {d.DisplayName}"));
        foreach (var e in db.All<ElementDef>()) list.Add((ElementPrefix + e.Id, $"элемент: {e.DisplayName}"));
        foreach (var c in Enum.GetNames(typeof(DamageCategory))) list.Add((CategoryPrefix + c, $"категория: {CategoryName(c)}"));
        foreach (var c in db.All<DamageTypeDef>().Where(d => d.Kind == DamageCategory.Custom && !string.IsNullOrEmpty(d.CustomCategory)).Select(d => d.CustomCategory).Distinct())
            list.Add((CategoryPrefix + c, $"категория: {c}"));
        return list;
    }

    /// <summary>Is the match string known in this content (for validation)?</summary>
    public static bool IsKnownMatch(ContentDatabase db, string match)
    {
        if (string.IsNullOrEmpty(match) || match == Any || match.StartsWith(TagPrefix, StringComparison.Ordinal)) return true;
        if (match.StartsWith(ElementPrefix, StringComparison.Ordinal)) return db.Element(match[ElementPrefix.Length..]) != null;
        if (match.StartsWith(CategoryPrefix, StringComparison.Ordinal))
        {
            var c = match[CategoryPrefix.Length..];
            return Enum.TryParse<DamageCategory>(c, out _) || db.All<DamageTypeDef>().Any(d => d.CustomCategory == c);
        }
        return db.DamageType(match) != null;
    }

    /// <summary>Editor hint for an empty inheritable field.</summary>
    public static string? InheritedHint(ContentDatabase db, Def def, string property)
    {
        var prop = def.GetType().GetProperty(property);
        if (prop == null) return null;
        var own = prop.GetValue(def);
        bool empty = own is string s ? string.IsNullOrEmpty(s) : own is System.Collections.ICollection c && c.Count == 0;
        if (!empty) return "задано здесь";
        IEnumerable<Def> parents = def switch
        {
            ElementDef e => ElementChain(db, e.Parent).Select(x => (Def)db.Element(x)!),
            DamageTypeDef t => TypeChain(db, t.Parent).Select(x => (Def?)db.DamageType(x)).Where(x => x != null).Select(x => x!)
                .Concat(string.IsNullOrEmpty(t.Element) ? Enumerable.Empty<Def>() : ElementChain(db, t.Element).Select(x => (Def)db.Element(x)!)),
            _ => Enumerable.Empty<Def>(),
        };
        if (def is ElementDef ed && ed.NoListInheritance && own is System.Collections.ICollection) return "не наследуется (пусто)";
        foreach (var p in parents)
        {
            var pp = p.GetType().GetProperty(property);
            if (pp == null) continue;
            var v = pp.GetValue(p);
            string? text = v switch
            {
                string str when !string.IsNullOrEmpty(str) => str,
                System.Collections.IEnumerable en when v is not string && en.Cast<object>().Any() => string.Join(", ", en.Cast<object>().Select(o => o is ElementStatusDef es ? es.Status : o.ToString())),
                _ => null,
            };
            if (text != null) return $"унаследовано от «{p.DisplayName}»: {text}";
        }
        return "не задано (и не наследуется)";
    }
}
