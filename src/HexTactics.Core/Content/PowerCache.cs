using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HexTactics.Core.Content;

/// <summary>
/// The cache of the power estimate (ТЗ ч. 5, 23.6, T24; docs/DECISIONS_5.md §88): the key is the hash of everything the
/// estimate reads — the template, its skills, passives (own, of its categories, given by its auras) and the statuses of
/// those, its categories with their rules, the rules of the estimate, the reference and the context. A change of any of
/// them gives another key: the old result is never served. A context with a scenario is not cached (it reads the scene).
/// </summary>
public static class PowerCache
{
    private sealed class Store
    {
        public readonly Dictionary<string, PowerReport> Map = new(StringComparer.Ordinal);
        public int Hits, Misses;
    }

    private static readonly ConditionalWeakTable<ContentDatabase, Store> Stores = new();

    /// <summary>The hits and misses so far for the database (tests, diagnostics).</summary>
    public static (int Hits, int Misses) Stats(ContentDatabase db)
    {
        var s = Stores.GetOrCreateValue(db);
        lock (s) return (s.Hits, s.Misses);
    }

    public static void Clear(ContentDatabase db)
    {
        var s = Stores.GetOrCreateValue(db);
        lock (s) s.Map.Clear();
    }

    /// <summary>The estimate, from the cache when nothing it reads has changed.</summary>
    public static PowerReport Evaluate(ContentDatabase db, UnitDef def, PowerContext? context = null)
    {
        var ctx = context ?? PowerContext.Solo;
        if (ctx.Scenario != null) return PowerModel.Evaluate(db, def, ctx);
        string key = KeyOf(db, def, ctx);
        var store = Stores.GetOrCreateValue(db);
        lock (store)
        {
            if (store.Map.TryGetValue(key, out var hit))
            {
                store.Hits++;
                return hit;
            }
        }
        var report = PowerModel.Evaluate(db, def, ctx);
        lock (store)
        {
            store.Misses++;
            if (store.Map.Count > 4000) store.Map.Clear();
            store.Map[key] = report;
        }
        return report;
    }

    /// <summary>The hash of what the estimate of <paramref name="def"/> reads.</summary>
    public static string KeyOf(ContentDatabase db, UnitDef def, PowerContext ctx)
    {
        var sb = new StringBuilder();
        void Add(Def? d)
        {
            if (d == null) return;
            sb.Append(d.Category).Append(':').Append(d.Id).Append('=').Append(JsonSerializer.Serialize(d, d.GetType(), ContentJson.Options)).Append('\n');
        }
        void AddUnit(UnitDef u)
        {
            Add(u);
            var skills = u.Skills.Append(u.BasicAttack).Append(u.DefenseSkill).Where(x => x != "").Distinct().Select(db.Skill).Where(x => x != null).Select(x => x!).ToList();
            var cats = CategoryTree.Resolve(db, u);
            var passives = u.Passives.Concat(cats.Of(CategoryRuleKind.Passive).Select(r => r.Rule.Passive))
                .Concat(skills.SelectMany(s => s.GrantsPassives)).Where(x => x != "").Distinct().Select(id => db.Get<PassiveDef>(id)).Where(x => x != null).Select(x => x!).ToList();
            foreach (var p in passives.ToList())
                foreach (var id in p.AuraPassives)
                    if (db.Get<PassiveDef>(id) is { } given && !passives.Contains(given))
                        passives.Add(given);
            foreach (var s in skills) Add(s);
            foreach (var p in passives) Add(p);
            foreach (var c in cats.All) Add(c);
            // The statuses the skills and passives apply (control, buffs) — what the estimate reads of them.
            foreach (var d in skills.Cast<Def>().Concat(passives))
                foreach (var st in ContentDependencies.Uses(d).Where(x => x.Category == DefCategory.Status).Select(x => x.Id).Distinct())
                    Add(db.Get<StatusDef>(st));
        }
        AddUnit(def);
        sb.Append("rules=").Append(ContentJson.Serialize(db.Rules.Power)).Append('\n');
        if (db.Unit(db.Rules.Power.ReferenceUnit) is { } reference && reference != def) AddUnit(reference);
        sb.Append("ctx=").Append(ctx.Kind).Append('/').Append(ctx.Allies).Append('/').Append(ctx.VsCategory).Append('\n');
        foreach (var a in ctx.AllyDefs.Distinct()) AddUnit(a);
        foreach (var a in ctx.AllyDefs) sb.Append(a.Id).Append(',');
        return HexTactics.Core.Sim.Simulation.StableHash(sb.ToString()).ToString("X") + ":" + sb.Length;
    }
}
