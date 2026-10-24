using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HexTactics.Core.Sim;

namespace HexTactics.Core.Content;

/// <summary>One measure of the budget: its value, the project's threshold, what it is made of.</summary>
public sealed record BudgetLine(string Key, string Label, double Value, double Limit, string Detail)
{
    public bool Over => Limit > 0 && Value > Limit;
}

public sealed class PerformanceReport
{
    public string Scenario { get; init; } = "";
    public List<BudgetLine> Lines { get; } = new();
    /// <summary>The templates that cost most per unit (passives, auras, summons, areas) — with why.</summary>
    public List<string> Expensive { get; } = new();
    /// <summary>Routes that are recomputed when the scene changes (conditional exits, destructible objects on the way).</summary>
    public List<string> Routes { get; } = new();
    /// <summary>Measured shares of the subsystems (a profiled run), ms per tick.</summary>
    public List<(string Section, double MsPerTick, double Share)> Profile { get; } = new();
    public bool OverBudget => Lines.Any(l => l.Over);

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Производительность «{Scenario}»: " + (OverBudget ? "превышен рекомендуемый бюджет — тест можно запустить, но он выходит за пределы" : "в пределах бюджета"));
        foreach (var l in Lines)
            sb.AppendLine($"{(l.Over ? "⚠" : "·")} {l.Label}: {F(l.Value)}" + (l.Limit > 0 ? $" (порог {F(l.Limit)})" : "") + (l.Detail != "" ? $" — {l.Detail}" : ""));
        if (Expensive.Count > 0) sb.AppendLine("Дорогие шаблоны: " + string.Join("; ", Expensive));
        if (Routes.Count > 0) sb.AppendLine("Маршруты с пересчётом: " + string.Join("; ", Routes));
        if (Profile.Count > 0)
            sb.AppendLine("Нагрузка подсистем (пробный прогон): " + string.Join(", ", Profile.Select(p => $"{p.Section} {p.Share:P0} ({p.MsPerTick:0.###} мс/тик)")));
        return sb.ToString();
    }
}

/// <summary>
/// The performance budget of a scene (docs/DECISIONS_4.md §72, TZ §20.3): without a battle — how many units will be
/// created and alive at once (the plan of the waves, their walks and the limits), groups per wave, nodes and edges of
/// the routes, rules of passives and effects at the peak, decisions of the AI per second, the expensive templates,
/// the routes recomputed when the scene changes; with <see cref="Profile"/> — the measured load of the subsystems.
/// </summary>
public static class PerformanceBudget
{
    public static PerformanceReport Estimate(ContentDatabase db, ScenarioDef sc)
    {
        var lim = db.Rules.Performance;
        var rep = new PerformanceReport { Scenario = sc.DisplayName };
        var plan = WavePlan.Of(db, sc);

        // The timeline: every enemy lives from its appearance to the end of its walk (an upper bound — defenders kill earlier).
        var events = new List<(double T, int D, UnitDef U)>();
        double t0 = 0;
        foreach (var p in plan)
        {
            double start = p.Start ?? t0;
            foreach (var s in p.Spawns)
            {
                double at = start + s.At;
                events.Add((at, 1, s.Unit));
                events.Add((at + WavePlan.WalkSeconds(db, sc, s), -1, s.Unit));
            }
            t0 = start + p.SpawnSeconds + p.Spawns.Select(s => WavePlan.WalkSeconds(db, sc, s)).DefaultIfEmpty(0).Max();
        }
        int alive = 0, peak = 0;
        var mix = new Dictionary<UnitDef, int>(ReferenceEqualityComparer.Instance);
        var peakMix = new Dictionary<UnitDef, int>(ReferenceEqualityComparer.Instance);
        foreach (var e in events.OrderBy(e => e.T).ThenBy(e => e.D))
        {
            alive += e.D;
            mix[e.U] = mix.GetValueOrDefault(e.U) + e.D;
            if (alive > peak)
            {
                peak = alive;
                peakMix = new Dictionary<UnitDef, int>(mix, ReferenceEqualityComparer.Instance);
            }
        }
        int capWave = plan.Where(p => p.Def.MaxAlive > 0).Select(p => p.Def.MaxAlive).DefaultIfEmpty(int.MaxValue).Max();
        int cap = sc.Limits.MaxAliveEnemies > 0 ? Math.Min(sc.Limits.MaxAliveEnemies, capWave) : capWave;
        if (plan.Any(p => p.Def.MaxAlive <= 0)) cap = sc.Limits.MaxAliveEnemies > 0 ? sc.Limits.MaxAliveEnemies : int.MaxValue;
        int peakEnemies = Math.Min(peak, cap);
        var sceneUnits = sc.Units.Select(u => db.Unit(u.Unit)).Where(u => u != null).Select(u => u!).ToList();
        int defenders = sceneUnits.Count + Math.Max(sc.Placement.MaxUnits, sc.Placement.Preset.Count);
        int structures = sc.Objects.Count(o => o.Health != null);
        int created = plan.Sum(p => p.Spawns.Count);
        if (sc.Limits.MaxCreatedEnemies > 0) created = Math.Min(created, sc.Limits.MaxCreatedEnemies);
        int peakAll = peakEnemies + defenders + structures;
        rep.Lines.Add(new BudgetLine("alive", "Живых юнитов одновременно (оценка)", peakAll, lim.MaxAliveUnits,
            $"врагов до {peakEnemies}" + (peak > peakEnemies ? $" (без лимитов было бы {peak})" : "") + $", защитников {defenders}, сооружений {structures}"));
        rep.Lines.Add(new BudgetLine("created", "Будет создано врагов", created, 0, $"{plan.Count} волн"));
        var groups = plan.Select(p => (Name: p.Def.Name, N: p.Def.Groups.Count + (p.ByBudget ? 1 : 0))).DefaultIfEmpty((Name: "", N: 0)).MaxBy(x => x.N);
        rep.Lines.Add(new BudgetLine("groups", "Групп в волне (наибольшее)", groups.N, lim.MaxGroupsPerWave, groups.N > 0 ? $"«{groups.Name}»" : ""));
        var nodes = sc.Routes.Select(r => (DisplayName: r.DisplayName, N: r.Nodes.Count)).DefaultIfEmpty((DisplayName: "", N: 0)).MaxBy(x => x.N);
        rep.Lines.Add(new BudgetLine("nodes", "Узлов маршрута (наибольшее)", nodes.N, lim.MaxRouteNodes, nodes.N > 0 ? $"«{nodes.DisplayName}»" : ""));
        rep.Lines.Add(new BudgetLine("edges", "Рёбер графа маршрутов", sc.Routes.Sum(r => r.Edges.Count), lim.MaxRouteEdges, $"{sc.Routes.Count} маршрутов"));

        // Per template: rules of passives, effects it keeps alive, decisions of its AI.
        var all = peakMix.Where(kv => kv.Value > 0).Select(kv => (U: kv.Key, N: (double)kv.Value)).ToList();
        double scale = peak > 0 ? (double)peakEnemies / peak : 0;
        all = all.Select(x => (x.U, x.N * scale)).ToList();
        all.AddRange(sceneUnits.GroupBy(u => u, ReferenceEqualityComparer.Instance).Select(g => ((UnitDef)g.Key!, (double)g.Count())));
        foreach (var pu in sc.Placement.Preset)
            if (db.Unit(pu.Unit) is { } d) all.Add((d, 1));
        var cost = new Dictionary<UnitDef, (int Rules, int Effects, double Decisions, double Weight, string Why)>(ReferenceEqualityComparer.Instance);
        (int, int, double, double, string) CostOf(UnitDef u)
        {
            if (cost.TryGetValue(u, out var c)) return c;
            var passives = u.Passives.Concat(CategoryTree.Resolve(db, u).Of(CategoryRuleKind.Passive).Select(r => r.Rule.Passive)).Distinct()
                .Select(id => db.Get<PassiveDef>(id)).Where(p => p != null).Select(p => p!).ToList();
            int rules = passives.Sum(p => Math.Max(1, p.Modifiers.Count));
            int auras = passives.Count(p => p.AuraRadius > 0);
            var skills = u.Skills.Append(u.BasicAttack).Select(id => db.Skill(id)).Where(s => s != null).Select(s => s!).Distinct().ToList();
            var effects = skills.SelectMany(s => s.Stages.SelectMany(st => st.Effects)).ToList();
            int statusEffects = effects.Count(e => e.Type == EffectType.ApplyStatus || e.Type == EffectType.AddCellEffect);
            int summons = effects.Count(e => e.Type == EffectType.Summon);
            int areas = skills.Count(s => s.Stages.Any(st => st.Area.Shape != AreaShape.Single));
            var ai = db.Get<AiProfileDef>(u.AiProfile);
            double decisions = 1 / Math.Max(0.05, ai?.DecisionInterval ?? 0.5);
            double weight = rules + 3 * auras + 5 * summons + 2 * areas + statusEffects;
            var why = new List<string>();
            if (rules > 0) why.Add($"{rules} правил пассивов");
            if (auras > 0) why.Add($"ауры ×{auras}");
            if (summons > 0) why.Add($"призыв ×{summons}");
            if (areas > 0) why.Add($"навыки по области ×{areas}");
            if (statusEffects > 0) why.Add($"накладывает эффекты ×{statusEffects}");
            c = (rules + 2 * auras, statusEffects + auras, decisions, weight, string.Join(", ", why));
            cost[u] = c;
            return c;
        }
        double rulesAtPeak = all.Sum(x => CostOf(x.U).Item1 * x.Item2);
        double effectsAtPeak = all.Sum(x => CostOf(x.U).Item2 * x.Item2);
        double decisionsPerSec = all.Sum(x => CostOf(x.U).Item3 * x.Item2);
        rep.Lines.Add(new BudgetLine("passives", "Правил пассивов на пике (оценка)", Math.Round(rulesAtPeak), lim.MaxPassiveRules, "модификаторы пассивов и аур всех живых"));
        rep.Lines.Add(new BudgetLine("effects", "Одновременных эффектов (оценка)", Math.Round(effectsAtPeak), lim.MaxEffects, "состояния и эффекты клеток от навыков и аур живых"));
        rep.Lines.Add(new BudgetLine("ai", "Решений ИИ в секунду (оценка)", Math.Round(decisionsPerSec), lim.MaxAiDecisionsPerSecond, "живые юниты ÷ интервал решений их профиля"));
        var fastAi = all.Select(x => db.Get<AiProfileDef>(x.U.AiProfile)).Where(a => a != null && a.DecisionInterval < lim.MinAiDecisionInterval).Select(a => a!).Distinct().ToList();
        if (fastAi.Count > 0)
            rep.Lines.Add(new BudgetLine("ai_interval", "Профили ИИ с частыми решениями", fastAi.Count, 0.0001,
                string.Join(", ", fastAi.Select(a => $"«{a.DisplayName}» {a.DecisionInterval:0.##} с")) + $" (мин. {lim.MinAiDecisionInterval:0.##} с)"));
        foreach (var x in all.GroupBy(x => x.U, ReferenceEqualityComparer.Instance).Select(g => (U: (UnitDef)g.Key!, N: g.Sum(y => y.Item2)))
                     .Select(x => (x.U, x.N, C: CostOf(x.U))).Where(x => x.C.Item4 > 0).OrderByDescending(x => x.C.Item4 * x.N).Take(5))
            rep.Expensive.Add($"«{x.U.DisplayName}» ×{x.N:0.#} на пике — {x.C.Item5}");

        // Routes recomputed when the scene changes: conditional exits, destructible or switchable objects on the way.
        var changing = sc.Objects.Where(o => o.Health != null).ToList();
        foreach (var r in sc.Routes)
        {
            var why = new List<string>();
            int cond = r.Edges.Count(e => e.Condition != null);
            if (cond > 0) why.Add($"{cond} рёбер с условием");
            var cells = r.AllCells().ToHashSet();
            var near = changing.Where(o => o.Cells().Any(c => cells.Contains(c) || c.Neighbors().Any(cells.Contains))).Select(o => o.DisplayName).ToList();
            if (near.Count > 0) why.Add("рядом разрушаемые объекты: " + string.Join(", ", near.Take(3)));
            if (r.Fallback != "") why.Add($"запасной маршрут «{r.Fallback}»");
            if (why.Count > 0) rep.Routes.Add($"«{r.DisplayName}» — {string.Join("; ", why)}");
        }
        return rep;
    }

    /// <summary>The measured load: a run without drawing for <paramref name="seconds"/>, the shares of the subsystems.</summary>
    public static PerformanceReport Profile(ContentDatabase db, ScenarioDef sc, double seconds = 60, ulong seed = 1)
    {
        var rep = Estimate(db, sc);
        var (sim, report, breaks) = AutoBattle.Prepare(db, sc, seed, new AutoBattleOptions { MaxSeconds = seconds });
        sim.Profiler.Reset();
        sim.Profiler.Enabled = true;
        AutoBattle.Play(sim, report, breaks, new AutoBattleOptions { MaxSeconds = seconds });
        var totals = sim.Profiler.Totals().GroupBy(t => t.Section).Select(g => (Section: g.Key, Ms: g.Sum(x => x.MsPerTick))).ToList();
        double sum = Math.Max(1e-9, totals.Sum(t => t.Ms));
        foreach (var (section, ms) in totals.OrderByDescending(t => t.Ms))
            rep.Profile.Add((section, ms, ms / sum));
        return rep;
    }
}
