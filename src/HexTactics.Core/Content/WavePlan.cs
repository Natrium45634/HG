using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>One planned appearance: the template, the second after the start of its wave, its route, its instance level.</summary>
public sealed record WaveSpawn(UnitDef Unit, double At, string Route, int Level, string Group);

/// <summary>A wave as it is planned (before a battle): the start, every appearance, the cost.</summary>
public sealed class PlannedWave
{
    public int Index { get; init; }
    public WaveDef Def { get; init; } = null!;
    /// <summary>The estimated start from the beginning of the battle; null — by a condition or a trigger.</summary>
    public double? Start { get; set; }
    public string StartText { get; set; } = "";
    public List<WaveSpawn> Spawns { get; } = new();
    /// <summary>The composition is picked at the start by the budget (a preview with the scene's seed).</summary>
    public bool ByBudget { get; set; }
    public double Budget { get; set; }
    public double Cost { get; set; }
    /// <summary>The last appearance after the start of the wave.</summary>
    public double SpawnSeconds => Spawns.Count == 0 ? 0 : Spawns.Max(s => s.At);
}

/// <summary>
/// The plan of the waves of a scenario without a battle (docs/DECISIONS_4.md §70, §72): who appears when and where;
/// a budget wave that is not pinned is previewed with the scene's seed, as the battle would pick it.
/// </summary>
public static class WavePlan
{
    public static List<PlannedWave> Of(ContentDatabase db, ScenarioDef sc)
    {
        var list = new List<PlannedWave>();
        double? prevEnd = 0, prevSpawnEnd = 0;
        for (int i = 0; i < sc.Waves.Count; i++)
        {
            var w = sc.Waves[i];
            var p = new PlannedWave { Index = i, Def = w };
            double countMul = Math.Max(0.1, w.Difficulty.Count);
            int Scaled(int n) => (int)Math.Ceiling(n * countMul - 1e-9);
            if (w.Budget.Enabled && !w.Budget.Pinned)
            {
                p.ByBudget = true;
                p.Budget = w.Budget.Budget;
                var comp = WaveBudget.Compose(db, w, w.Budget, WaveBudget.SeedOf(w.Budget, sc.Seed, i), sc);
                double t = 0;
                foreach (var (unit, count, _) in comp.Rows)
                    for (int k = 0; k < count; k++)
                    {
                        p.Spawns.Add(new WaveSpawn(unit, t, w.Budget.Route, 0, "бюджет"));
                        t += w.Budget.Interval;
                    }
            }
            else if (w.Budget.Enabled) p.Budget = w.Budget.Budget;
            foreach (var g in w.Groups.Where(g => g.Enabled))
            {
                var u = db.Unit(g.Unit);
                if (u == null) continue;
                int n = Scaled(g.Count);
                for (int r = 0; r <= g.Repeats; r++)
                {
                    double start = g.Delay + r * g.RepeatEvery;
                    for (int k = 0; k < n; k++)
                    {
                        double at = start + k * g.Interval + (g.SubgroupSize > 0 ? k / g.SubgroupSize * g.SubgroupPause : 0);
                        p.Spawns.Add(new WaveSpawn(u, at, g.Route, g.Level, g.Name != "" ? g.Name : g.Id));
                    }
                }
            }
            int idx = 0;
            foreach (var wu in w.Units)
            {
                var u = db.Unit(wu.Unit);
                if (u == null) continue;
                for (int k = 0; k < Scaled(wu.Count); k++) p.Spawns.Add(new WaveSpawn(u, idx++ * w.Interval, w.Route, 0, wu.Tag));
            }
            p.Cost = p.Spawns.Sum(s => ThreatCost.Of(db, s.Unit, sc));
            switch (w.Start)
            {
                case WaveStart.Time:
                    p.Start = w.Time;
                    p.StartText = $"на {w.Time:0.#} с";
                    break;
                case WaveStart.AfterPrevious:
                    p.Start = prevEnd is { } e ? e + w.Delay : null;
                    p.StartText = i == 0 ? "сразу" : "после зачистки предыдущей" + (w.Delay > 0 ? $" + {w.Delay:0.#} с" : "");
                    break;
                case WaveStart.AfterPreviousSpawned:
                    p.Start = prevSpawnEnd is { } se ? se + w.Delay : null;
                    p.StartText = i == 0 ? "сразу" : "после появления предыдущей" + (w.Delay > 0 ? $" + {w.Delay:0.#} с" : "");
                    break;
                case WaveStart.OnCondition:
                    p.StartText = "по условию";
                    break;
                default:
                    p.StartText = "действием триггера";
                    break;
            }
            // The wave is cleared about when its last enemy has walked its route.
            prevEnd = p.Start is { } s0 ? s0 + p.SpawnSeconds + p.Spawns.Select(s => WalkSeconds(db, sc, s)).DefaultIfEmpty(0).Max() : null;
            prevSpawnEnd = p.Start is { } s1 ? s1 + p.SpawnSeconds : null;
            list.Add(p);
        }
        return list;
    }

    /// <summary>Seconds to walk the route of an appearance (its length / the speed of the template).</summary>
    public static double WalkSeconds(ContentDatabase db, ScenarioDef sc, WaveSpawn s)
    {
        var route = sc.Routes.FirstOrDefault(r => r.Id == s.Route) ?? sc.Routes.FirstOrDefault(r => r.Nodes.Count > 0);
        int len = route == null ? 10 : Math.Max(1, route.MainPath().Count - 1);
        double speed = s.Unit.Stats.TryGetValue(Stats.MoveSpeed, out var v) ? v : db.Stat(Stats.MoveSpeed)?.Default ?? 1;
        return len / Math.Max(0.1, speed);
    }

    /// <summary>The level of an appearance: the instance level, else the template's (exact).</summary>
    public static double LevelOf(ContentDatabase db, WaveSpawn s, Dictionary<UnitDef, double>? cache = null)
    {
        if (s.Level > 0) return s.Level;
        if (cache != null && cache.TryGetValue(s.Unit, out var l)) return l;
        l = PowerModel.Evaluate(db, s.Unit).Level;
        if (cache != null) cache[s.Unit] = l;
        return l;
    }
}
