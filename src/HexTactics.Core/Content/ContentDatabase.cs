using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>Ordered table of definitions of one category, unique by Id.</summary>
public sealed class DefTable
{
    private readonly List<Def> _items = new();
    private readonly Dictionary<string, Def> _byId = new(StringComparer.Ordinal);

    public DefTable(DefCategory category, Type type)
    {
        Category = category;
        DefType = type;
    }

    public DefCategory Category { get; }
    public Type DefType { get; }
    public IReadOnlyList<Def> Items => _items;
    public int Count => _items.Count;

    public bool Contains(string id) => _byId.ContainsKey(id);

    public Def? Get(string id) => id != null && _byId.TryGetValue(id, out var d) ? d : null;

    /// <summary>Adds a definition. Returns false if the id already exists (the existing one is kept).</summary>
    public bool Add(Def def)
    {
        if (!DefType.IsInstanceOfType(def)) throw new ArgumentException($"{def.GetType().Name} не подходит для {Category}");
        if (_byId.ContainsKey(def.Id)) return false;
        _items.Add(def);
        _byId[def.Id] = def;
        return true;
    }

    public void Replace(Def def)
    {
        if (_byId.TryGetValue(def.Id, out var old))
        {
            int idx = _items.IndexOf(old);
            _items[idx] = def;
        }
        else
        {
            _items.Add(def);
        }
        _byId[def.Id] = def;
    }

    public bool Remove(string id)
    {
        if (!_byId.TryGetValue(id, out var d)) return false;
        _byId.Remove(id);
        _items.Remove(d);
        return true;
    }

    public bool Rename(string oldId, string newId)
    {
        if (!_byId.TryGetValue(oldId, out var d) || _byId.ContainsKey(newId)) return false;
        _byId.Remove(oldId);
        d.Id = newId;
        _byId[newId] = d;
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _byId.Clear();
    }
}

/// <summary>
/// All content definitions + rules. Content (definitions) is strictly separated from the
/// battle state (Simulation). The editor works on a draft copy (Clone).
/// </summary>
public sealed class ContentDatabase
{
    public static readonly (DefCategory Category, Type Type, string File)[] Layout =
    {
        (DefCategory.Stat, typeof(StatDef), "stats.json"),
        (DefCategory.Resource, typeof(ResourceDef), "resources.json"),
        (DefCategory.DamageType, typeof(DamageTypeDef), "damage_types.json"),
        (DefCategory.Faction, typeof(FactionDef), "factions.json"),
        (DefCategory.Terrain, typeof(TerrainDef), "terrains.json"),
        (DefCategory.CellEffect, typeof(CellEffectDef), "cell_effects.json"),
        (DefCategory.Synergy, typeof(SynergyDef), "synergies.json"),
        (DefCategory.Status, typeof(StatusDef), "statuses.json"),
        (DefCategory.Skill, typeof(SkillDef), "skills.json"),
        (DefCategory.Passive, typeof(PassiveDef), "passives.json"),
        (DefCategory.AiProfile, typeof(AiProfileDef), "ai_profiles.json"),
        (DefCategory.Unit, typeof(UnitDef), "units.json"),
        (DefCategory.Map, typeof(MapDef), "maps"),
        (DefCategory.Scenario, typeof(ScenarioDef), "scenarios"),
    };

    private readonly Dictionary<DefCategory, DefTable> _tables = new();

    public ContentDatabase()
    {
        foreach (var (cat, type, _) in Layout) _tables[cat] = new DefTable(cat, type);
    }

    public RulesDef Rules { get; set; } = new();

    /// <summary>Skills disabled by validation, with the reason (they stay in content but cannot be used).</summary>
    public Dictionary<string, string> DisabledSkills { get; } = new();

    public DefTable Table(DefCategory c) => _tables[c];

    public IEnumerable<DefTable> Tables => _tables.Values;

    public static Type TypeOf(DefCategory c)
    {
        foreach (var (cat, type, _) in Layout)
            if (cat == c)
                return type;
        throw new ArgumentException(c.ToString());
    }

    public static string FileOf(DefCategory c)
    {
        foreach (var (cat, _, file) in Layout)
            if (cat == c)
                return file;
        throw new ArgumentException(c.ToString());
    }

    public static DefCategory? CategoryOf(Type t)
    {
        foreach (var (cat, type, _) in Layout)
            if (type == t)
                return cat;
        return null;
    }

    public bool Add(Def d) => _tables[d.Category].Add(d);

    public T? Get<T>(string? id) where T : Def
    {
        if (string.IsNullOrEmpty(id)) return null;
        var cat = CategoryOf(typeof(T));
        if (cat == null) return null;
        return _tables[cat.Value].Get(id) as T;
    }

    public T Require<T>(string id) where T : Def =>
        Get<T>(id) ?? throw new KeyNotFoundException($"{typeof(T).Name} '{id}' не найден");

    public bool Exists(DefCategory c, string id)
    {
        if (c == DefCategory.MoveCategory) return Rules.Move.MovementCategories.Contains(id);
        return _tables.TryGetValue(c, out var t) && t.Contains(id);
    }

    public IEnumerable<string> Ids(DefCategory c)
    {
        if (c == DefCategory.MoveCategory) return Rules.Move.MovementCategories;
        return _tables[c].Items.Select(d => d.Id);
    }

    public IEnumerable<T> All<T>() where T : Def
    {
        var cat = CategoryOf(typeof(T));
        if (cat == null) return Enumerable.Empty<T>();
        return _tables[cat.Value].Items.Cast<T>();
    }

    public IEnumerable<Def> AllDefs()
    {
        foreach (var (cat, _, _) in Layout)
        foreach (var d in _tables[cat].Items)
            yield return d;
    }

    public TerrainDef? Terrain(string id) => Get<TerrainDef>(id);
    public UnitDef? Unit(string id) => Get<UnitDef>(id);
    public SkillDef? Skill(string id) => Get<SkillDef>(id);
    public StatusDef? Status(string id) => Get<StatusDef>(id);
    public CellEffectDef? CellEffect(string id) => Get<CellEffectDef>(id);
    public FactionDef? Faction(string id) => Get<FactionDef>(id);
    public StatDef? Stat(string id) => Get<StatDef>(id);

    /// <summary>Relation of faction a towards faction b.</summary>
    public Relation RelationOf(string a, string b)
    {
        if (a == b) return Relation.Ally;
        var fa = Faction(a);
        var fb = Faction(b);
        if ((fa != null && fa.AlliedWith.Contains(b)) || (fb != null && fb.AlliedWith.Contains(a))) return Relation.Ally;
        if ((fa != null && fa.HostileTo.Contains(b)) || (fb != null && fb.HostileTo.Contains(a))) return Relation.Enemy;
        return Relation.Neutral;
    }

    public double StatDefault(string statId)
    {
        var s = Stat(statId);
        return s?.Default ?? 0.0;
    }

    /// <summary>Deep copy (draft for the editor / isolated test battles).</summary>
    public ContentDatabase Clone()
    {
        var copy = new ContentDatabase { Rules = ContentJson.Clone(Rules) };
        foreach (var d in AllDefs()) copy.Add(ContentJson.Clone(d));
        foreach (var kv in DisabledSkills) copy.DisabledSkills[kv.Key] = kv.Value;
        return copy;
    }

    /// <summary>Renames a definition and updates every reference to it.</summary>
    public bool Rename(DefCategory c, string oldId, string newId)
    {
        if (string.IsNullOrWhiteSpace(newId) || Exists(c, newId)) return false;
        if (!_tables[c].Rename(oldId, newId)) return false;
        ReferenceWalker.ReplaceReferences(this, c, oldId, newId);
        return true;
    }

    /// <summary>Finds every definition that references the given one (for delete confirmation).</summary>
    public List<ReferenceInfo> FindReferences(DefCategory c, string id) => ReferenceWalker.FindReferences(this, c, id);
}
