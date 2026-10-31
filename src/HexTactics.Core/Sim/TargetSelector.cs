using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Common automatic target choice (docs/DECISIONS_3.md §43) for auto-attacks, automatic skills and group
/// orders. Each criterion is a metric (lower is better; yes/no criteria are 0/1): sequentially the pool is
/// narrowed to the best value of each rule in turn — so a yes/no rule means «if there are such, take them,
/// otherwise the others» — or, weighted, normalised metrics are summed. A strict rule removes the targets
/// that do not match. Random choice among equals uses the simulation's generator (reproducible).
/// </summary>
public sealed class TargetSelector
{
    private readonly Simulation _sim;

    public TargetSelector(Simulation sim)
    {
        _sim = sim;
    }

    /// <summary>Metric of a target by a rule (lower is better).</summary>
    public double Metric(Unit u, Unit t, AutoTargetRuleDef r, SkillDef? skill, int currentTarget)
    {
        switch (r.Rule)
        {
            case AutoTargetRule.Nearest: return HexCoord.Distance(u.Pos, t.Pos);
            case AutoTargetRule.Farthest: return -HexCoord.Distance(u.Pos, t.Pos);
            case AutoTargetRule.LowestHp: return t.Hp;
            case AutoTargetRule.HighestHp: return -t.Hp;
            case AutoTargetRule.LowestHpFraction: return t.HpFraction;
            case AutoTargetRule.LowestMaxHp: return t.HpMax;
            case AutoTargetRule.HighestMaxHp: return -t.HpMax;
            case AutoTargetRule.LowestPower: return t.Stat(Stats.Power);
            case AutoTargetRule.HighestPower: return -t.Stat(Stats.Power);
            case AutoTargetRule.LowestMagicPower: return t.Stat(Stats.MagicPower);
            case AutoTargetRule.HighestMagicPower: return -t.Stat(Stats.MagicPower);
            case AutoTargetRule.MostDangerous: return -AiSystem.Threat(t);
            case AutoTargetRule.NearestToProtected:
            {
                var tag = _sim.Rules.AutoAttacks.ProtectedTag;
                var prot = _sim.Units.Where(x => x.Alive && !_sim.IsHostile(u, x) && x.HasTag(tag)).ToList();
                return prot.Count == 0 ? 0 : prot.Min(p => HexCoord.Distance(p.Pos, t.Pos));
            }
            case AutoTargetRule.Boss:
                return HasTag(t, _sim.Rules.AutoAttacks.BossTag) || t.Boss.IsBoss ? 0 : 1;
            case AutoTargetRule.BossOrElite:
                return HasTag(t, _sim.Rules.AutoAttacks.BossTag) || t.Boss.IsBoss || HasTag(t, _sim.Rules.AutoAttacks.EliteTag) ? 0 : 1;
            case AutoTargetRule.WithTag: return !string.IsNullOrEmpty(r.Tag) && HasTag(t, r.Tag) ? 0 : 1;
            case AutoTargetRule.WithStatus: return !string.IsNullOrEmpty(r.Status) && t.FindStatus(r.Status) != null ? 0 : 1;
            case AutoTargetRule.BlockedByMe: return _sim.Blocking.BlockerOf(t) == u ? 0 : 1;
            case AutoTargetRule.BlockingMe: return _sim.Blocking.BlockerOf(u) == t ? 0 : 1;
            case AutoTargetRule.CurrentTarget: return currentTarget == t.Id || u.Ai.TargetUnitId == t.Id ? 0 : 1;
            case AutoTargetRule.Vulnerable:
            {
                // Vulnerable = the damage of the skill gets a positive modifier on this target.
                var type = DamageTypeOf(u, skill);
                return type != null && _sim.Damage.ModifierPercent(t, type) > 1e-9 ? 0 : 1;
            }
            case AutoTargetRule.Killable:
                return ExpectedDamage(u, t, skill) + 1e-9 >= t.Hp ? 0 : 1;
            case AutoTargetRule.MostEnemiesNearby:
            {
                int n = 0;
                foreach (var o in _sim.Units)
                    if (o.Alive && o != t && _sim.IsHostile(u, o) && HexCoord.Distance(o.Pos, t.Pos) <= Math.Max(1, r.Radius))
                        n++;
                return -n;
            }
            case AutoTargetRule.Random:
                return 0;
            default:
                return HexCoord.Distance(u.Pos, t.Pos);
        }
    }

    private static bool HasTag(Unit t, string tag) => t.HasTag(tag);

    private static string? DamageTypeOf(Unit u, SkillDef? s)
    {
        s ??= u.BasicAttack;
        if (s == null) return null;
        foreach (var st in s.Stages)
        foreach (var e in st.Effects)
            if (e.Type == EffectType.Damage)
                return e.DamageType;
        return null;
    }

    /// <summary>Expected damage of one use of the skill on the target (amounts × modifiers; no rolls).</summary>
    public double ExpectedDamage(Unit u, Unit t, SkillDef? s)
    {
        s ??= u.BasicAttack;
        if (s == null) return 0;
        double total = 0;
        foreach (var st in s.Stages)
        foreach (var e in st.Effects)
        {
            if (e.Type != EffectType.Damage) continue;
            double a = _sim.Combat.EffectAmount(e, u, t, 1.0, HexCoord.Distance(u.Pos, t.Pos));
            total += Math.Max(0, a * (1 + _sim.Damage.ModifierPercent(t, e.DamageType) / 100.0));
        }
        return total;
    }

    /// <summary>
    /// Chooses among candidates by the rules. Explanation: the rules applied and how the pool narrowed
    /// (shown in diagnostics). Null when a strict rule leaves nobody.
    /// </summary>
    public Unit? Choose(Unit u, IReadOnlyList<Unit> candidates, IReadOnlyList<AutoTargetRuleDef> rules, TargetSelectionMode mode, bool randomTies,
        SkillDef? skill, int currentTarget, out string explanation, bool allowRandom = true)
    {
        var why = new List<string>();
        var pool = candidates.ToList();
        if (rules.Count == 0) rules = new[] { new AutoTargetRuleDef { Rule = AutoTargetRule.Nearest } };
        // Strict rules filter first.
        foreach (var r in rules.Where(r => r.Strict && r.IsBoolean))
        {
            int before = pool.Count;
            pool = pool.Where(t => Metric(u, t, r, skill, currentTarget) < 0.5).ToList();
            why.Add($"фильтр «{Name(r)}»: {before} → {pool.Count}");
        }
        if (pool.Count == 0)
        {
            explanation = string.Join("; ", why.DefaultIfEmpty("нет целей"));
            return null;
        }
        if (mode == TargetSelectionMode.Weighted)
        {
            var scores = pool.ToDictionary(t => t.Id, _ => 0.0);
            foreach (var r in rules)
            {
                if (r.Rule == AutoTargetRule.Random) continue;
                var vals = pool.Select(t => Metric(u, t, r, skill, currentTarget)).ToList();
                double min = vals.Min(), max = vals.Max(), span = max - min;
                for (int i = 0; i < pool.Count; i++) scores[pool[i].Id] += r.Weight * (span < 1e-9 ? 0 : (vals[i] - min) / span);
            }
            double best = scores.Values.Min();
            pool = pool.Where(t => scores[t.Id] <= best + 1e-9).ToList();
            why.Add($"взвешенно: {pool.Count} лучших");
        }
        else
        {
            foreach (var r in rules)
            {
                if (pool.Count <= 1) break;
                if (r.Rule == AutoTargetRule.Random)
                {
                    if (!allowRandom) break; // planning previews never touch the battle's generator
                    var sorted = pool.OrderBy(t => t.Id).ToList();
                    pool = new List<Unit> { sorted[_sim.AiRng.NextInt(0, sorted.Count)] };
                    why.Add("случайно");
                    break;
                }
                int before = pool.Count;
                double best = pool.Min(t => Metric(u, t, r, skill, currentTarget));
                pool = pool.Where(t => Metric(u, t, r, skill, currentTarget) <= best + 1e-9).ToList();
                why.Add($"«{Name(r)}»: {before} → {pool.Count}");
            }
        }
        Unit pick;
        if (pool.Count > 1 && randomTies && allowRandom)
        {
            var sorted = pool.OrderBy(t => t.Id).ToList();
            pick = sorted[_sim.AiRng.NextInt(0, sorted.Count)];
            why.Add($"случайно среди {sorted.Count}");
        }
        else
        {
            pick = pool.OrderBy(t => HexCoord.Distance(u.Pos, t.Pos)).ThenBy(t => t.Id).First();
        }
        explanation = string.Join("; ", why) + $" → {pick.Name}";
        return pick;
    }

    /// <summary>
    /// Candidates of a skill for the unit: visible, accepted by the skill's filter and layers. «Now» — usable from
    /// the position; otherwise only reachable by approaching.
    /// </summary>
    public (List<Unit> Now, List<Unit> Later) Candidates(Unit u, SkillDef s, HexCoord from, HexDirection facing)
    {
        var now = new List<Unit>();
        var later = new List<Unit>();
        var filter = s.Targeting == TargetingMode.Unit ? s.ValidTargets
            : s.Stages.SelectMany(st => st.Effects).FirstOrDefault(e => e.TargetsUnits)?.Filter ?? TargetFilterDef.EnemiesOnly();
        foreach (var t in _sim.Units)
        {
            if (!t.Alive || t == u) continue;
            if (!_sim.FilterAccepts(filter, u, t)) continue;
            if (!_sim.Vision.CanFactionSee(u.FactionId, t)) continue;
            if (!LayerResolver.CanTarget(_sim.Rules, s, t)) continue;
            var chk = _sim.Skills.Validate(u, s, t.Pos, t.Id, from.DirectionTo(t.Pos) ?? facing, from, facing, checkCosts: false, checkState: false);
            bool ok = chk.Ok;
            if (ok && s.Targeting is TargetingMode.Self or TargetingMode.Direction && s.Stages.Count > 0)
                ok = AreaResolver.Resolve(s.Stages[0].Area, from, from.DirectionTo(t.Pos) ?? facing, _sim.Map).Any(c => c.Cell == t.Pos);
            if (ok) now.Add(t);
            else if (chk.Reason is FailureReason.OutOfRange or FailureReason.NoLineOfSight || !ok) later.Add(t);
        }
        return (now, later);
    }

    private static string Name(AutoTargetRuleDef r) => r.Rule switch
    {
        AutoTargetRule.WithTag => "с тегом " + r.Tag,
        AutoTargetRule.WithStatus => "с состоянием " + r.Status,
        _ => r.Rule.ToString(),
    };
}
