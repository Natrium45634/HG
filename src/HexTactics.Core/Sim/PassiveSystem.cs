using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>A passive a unit has, with where it comes from.</summary>
public sealed class PassiveInstance
{
    public PassiveDef Def { get; init; } = null!;
    public PassiveSource SourceKind { get; init; }
    /// <summary>Readable source: «происхождение «Эльф»», «состояние «Горение»», «аура: Жрица»…</summary>
    public string Source { get; set; } = "";
    public List<string> AllSources { get; } = new();
    public int Stacks { get; set; } = 1;
    /// <summary>The unit whose aura gives the passive (-1 — not an aura).</summary>
    public int AuraOwner { get; init; } = -1;
    /// <summary>Conditions hold (or a triggered passive is in its active time): modifiers apply.</summary>
    public bool Active { get; set; }
    /// <summary>Why it does not act now (shown greyed out).</summary>
    public string Inactive { get; set; } = "";
    /// <summary>«За каждого»: modifiers with their value multiplied by the count (and capped) — docs/DECISIONS_4.md §62.</summary>
    public Dictionary<ModifierDef, ModifierDef> Scaled { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>Sources ignored by «запрещать дубликаты».</summary>
    public List<string> Duplicates { get; } = new();

    /// <summary>The modifier as it acts now (scaled by «за каждого»).</summary>
    public ModifierDef Effective(ModifierDef m) => Scaled.TryGetValue(m, out var s) ? s : m;
}

/// <summary>
/// Passives and talents (docs/DECISIONS_3.md §46): collects every passive of a unit from all its sources
/// (unit, origins, statuses, terrain, known skills, auras of others, the scenario — with its switches and
/// replacements), evaluates the conditions at the passive's check interval and answers the modifier
/// queries of the other systems (damage, costs, hits, immunities, target choice, movement). Stats get the
/// modifiers through <see cref="StatusSystem.RecalculateModifiers"/>.
/// </summary>
public sealed class PassiveSystem
{
    private readonly Simulation _sim;
    private readonly Dictionary<int, string> _signature = new();

    public PassiveSystem(Simulation sim)
    {
        _sim = sim;
    }

    // ------------------------------------------------------------------ collection

    public List<PassiveInstance> Collect(Unit u)
    {
        var list = new List<PassiveInstance>();
        var db = _sim.Content;
        void Add(string id, PassiveSource kind, string source, int auraOwner = -1)
        {
            var p = db.Get<PassiveDef>(id);
            if (p == null) return;
            // Scenario switches: «disable» or «replace with».
            foreach (var o in _sim.Scenario.Def?.PassiveOverrides ?? Enumerable.Empty<PassiveOverrideDef>())
                if (o.Passive == p.Id)
                {
                    if (string.IsNullOrEmpty(o.ReplaceWith)) return;
                    p = db.Get<PassiveDef>(o.ReplaceWith);
                    if (p == null) return;
                    source += " (заменено сценарием)";
                    break;
                }
            // The owner's radiating copy of an aura and a copy received from another owner stay separate;
            // «по экземпляру от каждого источника» keeps one instance per source.
            var same = list.FirstOrDefault(x => x.Def == p && (x.AuraOwner >= 0) == (auraOwner >= 0)
                                                && (p.Stacking != PassiveStacking.BySource || (auraOwner >= 0 ? x.AuraOwner == auraOwner : x.AllSources.Contains(source))));
            if (same != null)
            {
                if (p.Stacking == PassiveStacking.NoDuplicates)
                {
                    // Another source (another aura owner even with the same name) is ignored and listed.
                    if (same.AuraOwner != auraOwner || !same.AllSources.Contains(source)) same.Duplicates.Add(source);
                    return;
                }
                same.AllSources.Add(source);
                if (p.Stacking == PassiveStacking.Stack) same.Stacks = Math.Min(Math.Max(1, p.MaxStacks), same.Stacks + 1);
                return;
            }
            var inst = new PassiveInstance { Def = p, SourceKind = kind, Source = source, AuraOwner = auraOwner };
            inst.AllSources.Add(source);
            list.Add(inst);
        }

        foreach (var id in u.Def.Passives) Add(id, PassiveSource.Unit, "юнит");
        // Passives of the category tree: inherited, overridden, excluded or conditional (docs/DECISIONS_4.md §61).
        var cats = u.CategoryInfo ?? _sim.Categories.Of(u.Def, u.Tag);
        foreach (var r in cats.Of(CategoryRuleKind.Passive))
        {
            var rule = r.Rule.Mode == CategoryInherit.Conditional && !_sim.Conditions.Evaluate(r.Rule.Condition, new ConditionContext(u, null, u.Pos, null)) ? r.Otherwise : r.Rule;
            if (rule is not { Kind: CategoryRuleKind.Passive } || rule.Passive == "") continue;
            Add(rule.Passive, r.From.Kind switch
            {
                OriginKind.Race => PassiveSource.Race,
                OriginKind.Species => PassiveSource.Species,
                OriginKind.Custom => PassiveSource.CustomTag,
                _ => PassiveSource.ClassRole,
            }, $"{Names(r.From.Kind)} «{r.From.DisplayName}»" + (rule != r.Rule ? " (условие не выполнено — правило родителя)" : ""));
        }
        foreach (var s in u.Statuses)
            if (s.Level != null)
                foreach (var id in s.Level.Passives)
                    Add(id, PassiveSource.Status, $"состояние «{s.Def.DisplayName}»");
        var cell = _sim.Map.Get(u.Pos);
        if (cell != null)
        {
            foreach (var id in cell.Terrain.Passives) Add(id, PassiveSource.Terrain, $"гекс «{cell.Terrain.DisplayName}»");
        }
        foreach (var s in u.Skills.Concat(u.BasicAttack != null ? new[] { u.BasicAttack } : Array.Empty<SkillDef>()).Concat(u.DefenseSkill != null ? new[] { u.DefenseSkill } : Array.Empty<SkillDef>()))
            foreach (var id in s.GrantsPassives)
                Add(id, PassiveSource.Skill, $"навык «{s.DisplayName}»");
        foreach (var sp in _sim.Scenario.Def?.Passives ?? Enumerable.Empty<ScenarioPassiveDef>())
        {
            if (!string.IsNullOrEmpty(sp.Faction) && sp.Faction != u.FactionId) continue;
            if (!string.IsNullOrEmpty(sp.Tag) && !u.HasTag(sp.Tag)) continue;
            Add(sp.Passive, PassiveSource.Scenario, "правило сценария");
        }
        // Auras of other units (and the owner's own, if it includes itself).
        foreach (var o in _sim.Units)
        {
            if (!o.Alive || o.PassiveInstances.Count == 0) continue;
            foreach (var pi in o.PassiveInstances.ToList())
            {
                var p = pi.Def;
                if (p.AuraRadius <= 0 || pi.AuraOwner >= 0 || !pi.Active) continue;
                if (o == u && !p.AuraIncludesSelf) continue;
                if (HexCoord.Distance(o.Pos, u.Pos) > p.AuraRadius) continue;
                if (p.AuraTypes.Count > 0 && o != u && !p.AuraTypes.Any(t => _sim.Conditions.TypeMatches(u, t))) continue;
                bool ok = p.AuraAffects switch
                {
                    AuraAffects.Allies => o == u || _sim.RelationOf(o, u) == Relation.Ally,
                    AuraAffects.Enemies => _sim.IsHostile(o, u),
                    _ => true,
                };
                if (!ok) continue;
                foreach (var id in p.AuraPassives.Count > 0 ? p.AuraPassives : new List<string> { p.Id })
                    if (id != p.Id || o != u)
                        Add(id, PassiveSource.AllyAura, $"аура «{p.DisplayName}» ({o.Name})", o.Id);
            }
        }
        return list;
    }

    /// <summary>Categories of a unit with their ancestors: the main one, the additional ones, those assigned by tags.</summary>
    public IEnumerable<OriginDef> OriginsOf(Unit u)
    {
        if (u.CategoryInfo != null)
        {
            foreach (var o in u.CategoryInfo.All) yield return o;
            yield break;
        }
        var seen = new HashSet<string>();
        foreach (var id in u.Def.Origins)
            if (_sim.Content.Get<OriginDef>(id) is { } o && seen.Add(o.Id))
                yield return o;
        foreach (var o in _sim.Content.All<OriginDef>())
            if (o.AutoTags.Count > 0 && !seen.Contains(o.Id) && o.AutoTags.Any(t => u.Def.Tags.Contains(t) || u.Tag == t))
            {
                seen.Add(o.Id);
                yield return o;
            }
    }

    private static string Names(OriginKind k) => k switch
    {
        OriginKind.Race => "раса",
        OriginKind.Species => "вид",
        OriginKind.Class => "класс",
        OriginKind.Role => "роль",
        _ => "происхождение",
    };

    /// <summary>Re-collects the passives of the unit and evaluates their conditions (before the stats are rebuilt).</summary>
    public void Refresh(Unit u)
    {
        var list = Collect(u);
        foreach (var pi in list) Evaluate(u, pi);
        ResolveGroups(list);
        u.PassiveInstances = list;
        u.Passives.Clear();
        u.Passives.AddRange(list.Select(x => x.Def).Distinct());
    }

    /// <summary>
    /// Stacking groups (ТЗ ч. 5, 13.4, 14.2): of the acting passives of one group only the strongest / the latest / the
    /// first acts (by the group rule of the first member); the others are inactive with the reason — the panel shows it.
    /// </summary>
    private static void ResolveGroups(List<PassiveInstance> list)
    {
        // Only what acts on the unit competes: the owner's radiating copy of an aura stays (it is for the others).
        foreach (var g in list.Where(x => x.Active && x.Def.StackGroup != "" && ActsOnOwner(x)).GroupBy(x => x.Def.StackGroup))
        {
            var members = g.ToList();
            if (members.Count < 2) continue;
            var rule = members[0].Def.GroupRule;
            if (rule == StackGroupRule.All) continue;
            var winner = rule switch
            {
                StackGroupRule.Latest => members[^1],
                StackGroupRule.First => members[0],
                // Ties: the earlier one (the collection order is stable: the unit, categories, statuses, …, auras by unit ID).
                _ => members.Select((m, i) => (m, i)).OrderByDescending(x => Rank(x.m)).ThenBy(x => x.i).First().m,
            };
            foreach (var m in members.Where(m => m != winner))
            {
                m.Active = false;
                m.Inactive = $"группа «{g.Key}»: действует «{winner.Def.DisplayName}» ({winner.Source})";
            }
        }
    }

    /// <summary>The strength of a member of a stacking group: its rank, or the size of its (scaled) modifiers.</summary>
    public static double Rank(PassiveInstance pi) =>
        pi.Def.StackRank > 0 ? pi.Def.StackRank : pi.Def.Modifiers.Sum(m => Math.Abs(pi.Effective(m).Value)) * Math.Max(1, pi.Stacks);

    private void Evaluate(Unit u, PassiveInstance pi)
    {
        var p = pi.Def;
        pi.Inactive = "";
        // «За каждого»: the owner of the passive counts (an aura's copy — its owner).
        var counter = pi.AuraOwner >= 0 ? _sim.GetUnit(pi.AuraOwner) ?? u : u;
        foreach (var m in p.Modifiers)
            if (m.PerEach is { } pe)
            {
                int n = CountFor(counter, pe);
                double v = m.Value * n;
                if (pe.MaxValue > 0) v = Math.Clamp(v, -pe.MaxValue, pe.MaxValue);
                var sm = ContentJson.Clone(m);
                sm.Value = v;
                sm.PerEach = null;
                pi.Scaled[m] = sm;
            }
        if (p.Trigger != PassiveTrigger.Always && p.ActiveSeconds > 0)
        {
            // A triggered passive with modifiers acts for its time after the trigger.
            bool on = u.TimedPassives.TryGetValue(p.Id, out var until) && until > _sim.Tick;
            pi.Active = on;
            if (!on) pi.Inactive = "ждёт события «" + p.Trigger + "»";
            return;
        }
        if (p.When != null && !_sim.Conditions.Evaluate(p.When, new ConditionContext(u, null, u.Pos, null)))
        {
            pi.Active = false;
            pi.Inactive = "условие: " + p.When.Describe();
            return;
        }
        if (p.Condition != null && p.Trigger == PassiveTrigger.Always && !_sim.Scenario.Evaluate(p.Condition, u))
        {
            pi.Active = false;
            pi.Inactive = "условие: " + p.Condition.Describe();
            return;
        }
        pi.Active = true;
    }

    /// <summary>Every check interval: when the set of acting passives changed, the unit's stats are rebuilt.</summary>
    public void Update()
    {
        if (_sim.Tick % CheckTicks != 0) return;
        foreach (var u in _sim.Units)
        {
            if (!u.Alive) continue;
            var list = Collect(u);
            foreach (var pi in list) Evaluate(u, pi);
            ResolveGroups(list);
            string sig = string.Join(";", list.Select(x => $"{x.Def.Id}:{x.Stacks}:{(x.Active ? 1 : 0)}:{x.AuraOwner}:{string.Join(",", x.Scaled.Values.Select(v => v.Value))}"));
            if (_signature.TryGetValue(u.Id, out var old) && old == sig) continue;
            _signature[u.Id] = sig;
            _sim.Statuses.RecalculateModifiers(u);
        }
        foreach (var u in _sim.Units)
            foreach (var key in u.TimedPassives.Where(kv => kv.Value <= _sim.Tick).Select(kv => kv.Key).ToList())
                u.TimedPassives.Remove(key);
    }

    /// <summary>Ticks between checks: the shortest «частота проверки» of the content (default 0.25 s).</summary>
    private long CheckTicks
    {
        get
        {
            if (_checkTicks > 0) return _checkTicks;
            double s = _sim.Content.All<PassiveDef>().Where(p => p.CheckInterval > 0).Select(p => p.CheckInterval).DefaultIfEmpty(0.25).Min();
            _checkTicks = Math.Max(1, _sim.TicksFor(s));
            return _checkTicks;
        }
    }

    private long _checkTicks;

    // ------------------------------------------------------------------ events

    /// <summary>«Потерял N здоровья»: accumulated per passive, fires each time the amount is reached.</summary>
    public void OnHealthLost(Unit u, double lost, Unit? source)
    {
        foreach (var p in u.Passives)
        {
            if (p.Trigger != PassiveTrigger.OnHpLost) continue;
            double acc = u.HpLostSince.GetValueOrDefault(p.Id) + lost;
            if (acc + 1e-9 >= Math.Max(1, p.HpLoss))
            {
                acc = 0;
                _sim.Reactions.Enqueue(PassiveTrigger.OnHpLost, u, source);
            }
            u.HpLostSince[p.Id] = acc;
        }
    }

    /// <summary>A unit moved: its own «переместился» and «противник вошёл в область» of the others.</summary>
    public void OnMoved(Unit mover, HexCoord from)
    {
        if (mover.Passives.Any(p => p.Trigger == PassiveTrigger.OnMoved)) _sim.Reactions.Enqueue(PassiveTrigger.OnMoved, mover, null);
        foreach (var o in _sim.Units)
        {
            if (!o.Alive || o == mover || !_sim.IsHostile(o, mover)) continue;
            int r = 0;
            foreach (var p in o.Passives)
                if (p.Trigger == PassiveTrigger.OnEnemyEnteredArea)
                    r = Math.Max(r, p.TriggerRadius);
            if (r <= 0) continue;
            if (HexCoord.Distance(o.Pos, mover.Pos) <= r && HexCoord.Distance(o.Pos, from) > r)
                _sim.Reactions.Enqueue(PassiveTrigger.OnEnemyEnteredArea, o, mover);
        }
    }

    // ------------------------------------------------------------------ modifier queries

    /// <summary>
    /// The passive acts on its holder: conditions hold, and it is not the owner's copy of an aura meant for
    /// others (unless the aura includes the owner).
    /// </summary>
    public static bool ActsOnOwner(PassiveInstance pi)
    {
        var p = pi.Def;
        if (!pi.Active) return false;
        if (pi.AuraOwner < 0 && p.AuraRadius > 0 && p.AuraPassives.Count == 0 && !p.AuraIncludesSelf) return false;
        return true;
    }

    /// <summary>Modifiers act: always-on passives, or triggered ones during their time after the event.</summary>
    public static bool ModifiersAct(PassiveInstance pi) =>
        ActsOnOwner(pi) && (pi.Def.Trigger == PassiveTrigger.Always || pi.Def.ActiveSeconds > 0);

    /// <summary>Combined modifiers of a resource (maximum or regeneration).</summary>
    public Combined ResourceCombined(Unit u, ModifierTarget target, string resource) =>
        ModifierMath.Combine(Modifiers(u, target).Where(x => x.M.Resource == resource)
            .Select(x => new Contribution(x.M.Mode, x.M.Mode == ModifierMode.Multiplier ? x.M.Value : x.M.Value * x.Stacks, x.M.Priority, x.M.Category, x.Source, x.Stacks)));

    /// <summary>Modifiers of the acting passives of the unit, with their stacks and sources («за каждого» applied).</summary>
    public IEnumerable<(ModifierDef M, int Stacks, string Source)> Modifiers(Unit u, ModifierTarget target)
    {
        foreach (var pi in u.PassiveInstances)
        {
            if (!ModifiersAct(pi)) continue;
            foreach (var m in pi.Def.Modifiers)
                if (m.Target == target)
                    yield return (pi.Effective(m), pi.Stacks, $"{pi.Def.DisplayName} — {pi.Source}");
        }
    }

    /// <summary>
    /// The units «за каждого» counts for its owner: by types (tags, categories, templates), side, radius; summoned,
    /// dead and outside-the-radius ones only when allowed; capped by «предел количества».
    /// </summary>
    public int CountFor(Unit owner, PerEachDef pe)
    {
        int n = 0;
        foreach (var o in _sim.Units)
        {
            if (o.IsStructure || o == owner && !pe.IncludeSelf) continue;
            if (!o.Alive && !pe.IncludeDead) continue;
            if (o.SummonerId >= 0 && !pe.IncludeSummoned) continue;
            bool side = pe.Side switch
            {
                CountSide.Allies => o == owner || _sim.RelationOf(owner, o) == Relation.Ally,
                CountSide.Enemies => _sim.IsHostile(owner, o),
                _ => true,
            };
            if (!side) continue;
            if (o.Alive && pe.Radius > 0 && !pe.IncludeOutside && HexCoord.Distance(o.Pos, owner.Pos) > pe.Radius) continue;
            if (pe.Types.Count > 0 && !pe.Types.Any(t => _sim.Conditions.TypeMatches(o, t))) continue;
            n++;
        }
        return pe.MaxCount > 0 ? Math.Min(n, pe.MaxCount) : n;
    }

    /// <summary>
    /// Combines modifiers of one parameter by the summing modes into an equivalent percent change:
    /// total = (1 + Σ%/100) × Π× − 1 (flat additions are not percent and are ignored here).
    /// </summary>
    public static double CombinedPercent(IEnumerable<(ModifierDef M, int Stacks, string Source)> mods)
    {
        var c = ModifierMath.Combine(mods.Select(x => (x.M.Mode, x.M.Value * (x.M.Mode == ModifierMode.Multiplier ? 1 : x.Stacks), x.M.Priority, x.M.Category, x.Source, x.Stacks)));
        return ((1 + c.Percent / 100.0) * c.Multiplier - 1) * 100.0;
    }

    private bool SkillMatches(ModifierDef m, SkillDef? s)
    {
        if (!string.IsNullOrEmpty(m.Skill) && s?.Id != m.Skill) return false;
        if (!string.IsNullOrEmpty(m.SkillTag) && (s == null || !s.Tags.Contains(m.SkillTag))) return false;
        return true;
    }

    private bool TypesMatch(ModifierDef m, Unit? other) =>
        m.VsTypes.Count == 0 || other != null && m.VsTypes.Any(t => _sim.Conditions.TypeMatches(other, t));

    /// <summary>Incoming damage of a type (with the attacker for «против типов»), percent.</summary>
    public double IncomingPercent(Unit target, string damageType, Unit? attacker) =>
        target.PassiveInstances.Count == 0 ? 0 : CombinedPercent(Modifiers(target, ModifierTarget.IncomingDamage)
            .Where(x => _sim.Damage.Taxonomy.Matches(string.IsNullOrEmpty(x.M.Match) ? "*" : x.M.Match, damageType) && TypesMatch(x.M, attacker)));

    /// <summary>Outgoing damage: type, skill (id/tag), hit tag and target types, percent.</summary>
    public double OutgoingPercent(Unit attacker, Unit? target, string damageType, SkillDef? skill, string hitTag) =>
        attacker.PassiveInstances.Count == 0 ? 0 : CombinedPercent(Modifiers(attacker, ModifierTarget.OutgoingDamage)
            .Where(x => _sim.Damage.Taxonomy.Matches(string.IsNullOrEmpty(x.M.Match) ? "*" : x.M.Match, damageType) && SkillMatches(x.M, skill)
                        && (string.IsNullOrEmpty(x.M.HitTag) || x.M.HitTag == hitTag) && TypesMatch(x.M, target)));

    /// <summary>Strength of a hit of a series (stage tag), percent.</summary>
    public double HitPowerPercent(Unit u, SkillDef s, string hitTag) =>
        u.PassiveInstances.Count == 0 ? 0 : CombinedPercent(Modifiers(u, ModifierTarget.HitPower)
            .Where(x => SkillMatches(x.M, s) && (string.IsNullOrEmpty(x.M.HitTag) || x.M.HitTag == hitTag)));

    /// <summary>Extra hits of a series (added to the last damage stage).</summary>
    public int ExtraHits(Unit u, SkillDef s) =>
        u.PassiveInstances.Count == 0 ? 0 : (int)Math.Round(Modifiers(u, ModifierTarget.HitCount).Where(x => SkillMatches(x.M, s)).Sum(x => x.M.Value * x.Stacks));

    /// <summary>AP cost of a skill after the cost modifiers (never below 0).</summary>
    public double ApCost(Unit u, SkillDef s, double baseCost)
    {
        if (u.PassiveInstances.Count == 0 || baseCost <= 0) return baseCost;
        var mods = Modifiers(u, ModifierTarget.SkillCost).Where(x => SkillMatches(x.M, s)).ToList();
        if (mods.Count == 0) return baseCost;
        var c = ModifierMath.Combine(mods.Select(x => (x.M.Mode, x.M.Value * (x.M.Mode == ModifierMode.Multiplier ? 1 : x.Stacks), x.M.Priority, x.M.Category, x.Source, x.Stacks)));
        return Math.Max(0, ModifierMath.Apply(baseCost, c, StatCalcOrder.Standard));
    }

    public bool ImmuneToStatus(Unit u, StatusDef s) =>
        Modifiers(u, ModifierTarget.Immunity).Any(x => !string.IsNullOrEmpty(x.M.Status) &&
            (x.M.Status == s.Id || x.M.Status.StartsWith("tag:", StringComparison.Ordinal) && s.Tags.Contains(x.M.Status[4..])));

    public bool ImmuneToDamage(Unit u, string damageType) =>
        Modifiers(u, ModifierTarget.Immunity).Any(x => string.IsNullOrEmpty(x.M.Status) && !string.IsNullOrEmpty(x.M.Match) && _sim.Damage.Taxonomy.Matches(x.M.Match, damageType));

    /// <summary>Rules put first in the unit's automatic target choice.</summary>
    public List<AutoTargetRuleDef> TargetRules(Unit u) =>
        Modifiers(u, ModifierTarget.TargetChoice).Select(x => new AutoTargetRuleDef { Rule = x.M.Rule, Tag = x.M.RuleTag, Status = x.M.RuleTag }).ToList();

    /// <summary>Movement cost factor of entering the cell (1 — unchanged).</summary>
    public double TerrainFactor(Unit u, Cell cell)
    {
        if (u.PassiveInstances.Count == 0) return 1;
        var mods = Modifiers(u, ModifierTarget.TerrainCost).Where(x => TerrainMatches(x.M.Terrain, cell)).ToList();
        return mods.Count == 0 ? 1 : Math.Max(0.05, 1 + CombinedPercent(mods) / 100.0);
    }

    private static bool TerrainMatches(string t, Cell cell)
    {
        if (string.IsNullOrEmpty(t)) return false;
        if (t == cell.Terrain.Id) return true;
        if (t.StartsWith("tag:", StringComparison.Ordinal)) return cell.Terrain.Tags.Contains(t[4..]) || cell.Tags.Contains(t[4..]);
        if (t.StartsWith("kind:", StringComparison.Ordinal)) return string.Equals(cell.Terrain.Kind.ToString(), t[5..], StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
