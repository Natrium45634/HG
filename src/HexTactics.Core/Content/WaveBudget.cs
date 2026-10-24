using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Sim;

namespace HexTactics.Core.Content;

/// <summary>
/// Threat cost of a template (docs/DECISIONS_4.md §58, §67): the author's value, else the threat model (stage 6);
/// in a scene with routes and defenders — the contextual cost. <see cref="Estimate"/> is the simple estimate of
/// stage 2, kept for comparison.
/// </summary>
public static class ThreatCost
{
    public static double Of(ContentDatabase db, UnitDef u) => u.ThreatCost > 0 ? u.ThreatCost : ThreatModel.Base(db, u);

    /// <summary>The cost in a scene: contextual when the scene has enough data (docs/DECISIONS_4.md §67).</summary>
    public static double Of(ContentDatabase db, UnitDef u, ScenarioDef? sc) =>
        u.ThreatCost > 0 ? u.ThreatCost : sc != null && ThreatModel.HasContext(sc) ? ThreatModel.Evaluate(db, u, sc).Effective : ThreatModel.Base(db, u);

    public static double Estimate(ContentDatabase db, UnitDef u)
    {
        double S(string id) => u.Stats.TryGetValue(id, out var v) ? v : db.Stat(id)?.Default ?? 0;
        double hp = Math.Max(1, S(Stats.HpMax));
        double armor = Math.Max(0, S(Stats.Armor));
        double evasion = Math.Clamp(S(Stats.Evasion), 0, 0.8);
        double durability = hp * (1 + armor / 10) / (1 - evasion);
        double offence = 1 + (Math.Max(0, S(Stats.Power)) + Math.Max(0, S(Stats.MagicPower))) / 10;
        if (db.Skill(u.BasicAttack) is { RangeMax: > 1 } atk) offence *= 1 + Math.Min(0.5, (atk.RangeMax - 1) * 0.1);
        double cost = durability / 25 * offence;
        if (u.Boss.IsBoss) cost *= 1.5;
        return Math.Max(0.5, Math.Round(cost * 2, MidpointRounding.AwayFromZero) / 2);
    }

    /// <summary>An elite enemy: rank elite, leader or boss, tag «elite» or a boss (limited by «макс. доля элитных»).</summary>
    public static bool IsElite(UnitDef u) => u.Boss.IsBoss || u.Rank is UnitRank.Elite or UnitRank.Leader or UnitRank.Boss || u.Tags.Contains("elite") || u.Tags.Contains("boss");

    /// <summary>Elite with the tags of its categories too.</summary>
    public static bool IsElite(ContentDatabase db, UnitDef u) => IsElite(u) || CategoryTree.TagsOf(db, u) is var t && (t.Contains("elite") || t.Contains("boss"));
}

/// <summary>The composition picked by a budget: rows in order of the first pick, the leader first.</summary>
public sealed class BudgetResult
{
    public List<(UnitDef Unit, int Count, double Cost)> Rows { get; } = new();
    public double Budget { get; init; }
    public double Spent { get; set; }
    public int Units => Rows.Sum(r => r.Count);
    public List<string> Problems { get; } = new();

    public override string ToString() =>
        string.Join(", ", Rows.Select(r => $"{r.Unit.DisplayName} ×{r.Count}")) + $" — {Spent:0.#} из {Budget:0.#}";
}

/// <summary>
/// Picking a wave by its threat budget (docs/DECISIONS_4.md §58): deterministic greedy-random — the same seed gives
/// the same composition; every limit of the budget is respected; what cannot be met is reported, not hidden.
/// </summary>
public static class WaveBudget
{
    /// <summary>Templates the budget may use: the wave's faction, categories, tags, move types, cost limit.</summary>
    public static List<UnitDef> Candidates(ContentDatabase db, WaveDef w, WaveBudgetDef b, Func<UnitDef, double>? cost = null) =>
        db.All<UnitDef>().Where(u =>
            u.Faction == w.Faction
            && (b.Categories.Count == 0 || b.Categories.Any(c => CategoryTree.Matches(db, u, c)))
            && b.RequiredTags.All(t => CategoryTree.TagsOf(db, u).Contains(t))
            && !b.ForbiddenTags.Any(t => CategoryTree.TagsOf(db, u).Contains(t))
            && (b.MoveCategories.Count == 0 || b.MoveCategories.Contains(u.MoveCategory))
            && u.Stats.GetValueOrDefault(Stats.MoveSpeed, db.Stat(Stats.MoveSpeed)?.Default ?? 1) > 0
            && (b.MaxUnitCost <= 0 || (cost ?? (x => ThreatCost.Of(db, x)))(u) <= b.MaxUnitCost)).ToList();

    /// <summary>
    /// The composition of a wave by its budget. With a scene that has routes and defenders the costs are contextual
    /// (docs/DECISIONS_4.md §67); each template's cost is computed once.
    /// </summary>
    public static BudgetResult Compose(ContentDatabase db, WaveDef w, WaveBudgetDef b, ulong seed, ScenarioDef? sc = null)
    {
        var r = new BudgetResult { Budget = b.Budget };
        var costs = new Dictionary<UnitDef, double>(ReferenceEqualityComparer.Instance);
        double Cost(UnitDef u)
        {
            if (!costs.TryGetValue(u, out var c)) costs[u] = c = ThreatCost.Of(db, u, sc);
            return c;
        }
        var candidates = Candidates(db, w, b, Cost);
        var picks = new List<UnitDef>();
        double left = b.Budget;
        int elites = 0;
        if (b.Leader != "")
        {
            var leader = db.Unit(b.Leader);
            if (leader == null) r.Problems.Add($"лидер «{b.Leader}» не найден");
            else
            {
                double c = Cost(leader);
                if (c > left) r.Problems.Add($"лидер «{leader.DisplayName}» ({c:0.#}) дороже бюджета {b.Budget:0.#}");
                picks.Add(leader);
                left -= c;
                if (ThreatCost.IsElite(db, leader)) elites++;
            }
        }
        if (candidates.Count == 0) r.Problems.Add("нет шаблонов под ограничения бюджета (фракция, категории, теги, типы перемещения, предел стоимости)");
        var rng = new SimRng(seed == 0 ? 1 : seed);
        while (picks.Count < Math.Max(1, b.MaxUnits))
        {
            int n = picks.Count + 1;
            var affordable = candidates.Where(u => Cost(u) <= left + 1e-9
                                                   && (!ThreatCost.IsElite(db, u) || elites + 1 <= Math.Floor(b.MaxEliteShare * Math.Max(n, b.MinUnits) + 1e-9))).ToList();
            if (affordable.Count == 0) break;
            var u = affordable[rng.NextInt(0, affordable.Count)];
            picks.Add(u);
            left -= Cost(u);
            if (ThreatCost.IsElite(db, u)) elites++;
        }
        if (picks.Count < b.MinUnits)
            r.Problems.Add($"бюджета {b.Budget:0.#} хватает на {picks.Count} врагов из минимума {b.MinUnits}");
        foreach (var u in picks)
        {
            int i = r.Rows.FindIndex(x => x.Unit == u);
            if (i < 0) r.Rows.Add((u, 1, Cost(u)));
            else r.Rows[i] = (u, r.Rows[i].Count + 1, r.Rows[i].Cost);
        }
        r.Spent = b.Budget - left;
        return r;
    }

    /// <summary>The seed of a wave's budget: its own, else from the scenario's seed and the wave's number.</summary>
    public static ulong SeedOf(WaveBudgetDef b, ulong scenarioSeed, int waveIndex) =>
        b.Seed != 0 ? b.Seed : scenarioSeed * 1_000_003UL + (ulong)(waveIndex + 1) * 7919UL;

    /// <summary>Groups of a composition: one per row, one after another with the budget's interval.</summary>
    public static List<SpawnGroupDef> ToGroups(BudgetResult r, WaveBudgetDef b, string prefix)
    {
        var list = new List<SpawnGroupDef>();
        double delay = 0;
        for (int i = 0; i < r.Rows.Count; i++)
        {
            var (u, count, _) = r.Rows[i];
            list.Add(new SpawnGroupDef
            {
                Id = $"{prefix}_b{i + 1}", Unit = u.Id, Count = count, Interval = b.Interval, Delay = delay,
                SpawnBase = b.SpawnBase, Route = b.Route, FromBudget = true,
            });
            delay += count * b.Interval;
        }
        return list;
    }

    /// <summary>«Закрепить»: the composition becomes the wave's groups (replacing an earlier pinned one).</summary>
    public static BudgetResult Pin(ContentDatabase db, ScenarioDef sc, WaveDef w)
    {
        int index = sc.Waves.IndexOf(w);
        var r = Compose(db, w, w.Budget, SeedOf(w.Budget, sc.Seed, index), sc);
        w.Groups.RemoveAll(g => g.FromBudget);
        w.Groups.AddRange(ToGroups(r, w.Budget, w.Id != "" ? w.Id : $"w{index + 1}"));
        w.Budget.Pinned = true;
        return r;
    }

    /// <summary>«Снять закрепление»: the picked groups are removed, the wave is picked again at every start.</summary>
    public static void Unpin(WaveDef w)
    {
        w.Groups.RemoveAll(g => g.FromBudget);
        w.Budget.Pinned = false;
    }
}

/// <summary>One line of the schedule preview of a scene: when, which wave and group, how many, from where.</summary>
public sealed record ScheduleLine(int Wave, string WaveName, string Group, string Unit, int Count, double Start, double End, string Base, bool Absolute);

/// <summary>The schedule of the waves of a scene (TD editor «Волны»): times of the groups, relative for event waves.</summary>
public static class WaveSchedule
{
    public static int CountOf(SpawnGroupDef g, WaveDef w) => (int)Math.Ceiling(g.Count * Math.Max(0.01, w.Difficulty.Count) - 1e-9);

    public static List<ScheduleLine> Preview(ContentDatabase db, ScenarioDef sc)
    {
        var lines = new List<ScheduleLine>();
        for (int i = 0; i < sc.Waves.Count; i++)
        {
            var w = sc.Waves[i];
            bool abs = w.Start == WaveStart.Time;
            double t0 = abs ? w.Time : w.Delay;
            foreach (var g in w.Groups.Where(g => g.Enabled))
            {
                int n = CountOf(g, w);
                var b = sc.Object(g.SpawnBase);
                double init = b?.Spawn?.InitialDelay ?? 0;
                for (int rep = 0; rep <= g.Repeats; rep++)
                {
                    double s = t0 + init + rep * g.RepeatEvery;
                    lines.Add(new ScheduleLine(i, w.Name, g.Id, db.Unit(g.Unit)?.DisplayName ?? g.Unit, n, s + g.OffsetOf(0), s + g.OffsetOf(Math.Max(0, n - 1)),
                        b?.DisplayName ?? g.SpawnBase, abs));
                }
            }
            if (w.Budget.Enabled && !w.Budget.Pinned)
                lines.Add(new ScheduleLine(i, w.Name, "", $"по бюджету {w.Budget.Budget:0.#}", 0, t0, t0, sc.Object(w.Budget.SpawnBase)?.DisplayName ?? w.Budget.SpawnBase, abs));
        }
        return lines.OrderBy(l => l.Absolute ? 0 : 1).ThenBy(l => l.Wave).ThenBy(l => l.Start).ToList();
    }
}
