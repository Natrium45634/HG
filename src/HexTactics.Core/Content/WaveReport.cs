using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HexTactics.Core.Sim;

namespace HexTactics.Core.Content;

/// <summary>One wave in the report: the plan and, if there were runs, what happened.</summary>
public sealed class WaveReportRow
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Start { get; init; } = "";
    /// <summary>The budget of threat (0 — none).</summary>
    public double Budget { get; init; }
    /// <summary>The cost of the enemies it creates (contextual when the scene allows).</summary>
    public double Cost { get; init; }
    public int Count { get; init; }
    public double AvgLevel { get; init; }
    public double MaxLevel { get; init; }
    /// <summary>The last appearance plus the longest walk of its route.</summary>
    public double ExpectedSeconds { get; init; }
    /// <summary>From the runs: their number, the average of the reached goals, of the damage to the bases, of the duration.</summary>
    public int Runs { get; init; }
    public double? ReachedGoal { get; init; }
    public double? BaseDamage { get; init; }
    public double? Seconds { get; init; }
    /// <summary>The cost relative to the previous wave (1 — the same; 0 — the first wave).</summary>
    public double Relative { get; init; }
    public bool Jump { get; init; }
    public bool ByBudget { get; init; }
}

public sealed class WaveReport
{
    public string Scenario { get; init; } = "";
    public List<WaveReportRow> Rows { get; } = new();
    public double JumpRatio { get; init; }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Отчёт по волнам «{Scenario}»" + (Rows.Any(r => r.Runs > 0) ? $" (прогонов: {Rows.Max(r => r.Runs)})" : " (прогонов не было — только план)"));
        foreach (var r in Rows)
        {
            sb.Append($"{r.Index + 1}. «{r.Name}» ({r.Start}): ");
            sb.Append(r.Budget > 0 ? $"бюджет {F(r.Budget)}, стоимость {F(r.Cost)}" : $"стоимость {F(r.Cost)}");
            if (r.ByBudget) sb.Append(" (состав по бюджету, предпросмотр)");
            sb.Append($"; врагов {r.Count}; уровень ср. {F(r.AvgLevel)}, макс. {F(r.MaxLevel)}; ожидаемо {F(r.ExpectedSeconds)} с");
            if (r.Runs > 0) sb.Append($"; в прогонах: дошли до цели {F(r.ReachedGoal ?? 0)}, урон по базе {F(r.BaseDamage ?? 0)}" + (r.Seconds is { } s ? $", длилась {F(s)} с" : ""));
            if (r.Index > 0) sb.Append($"; к предыдущей ×{r.Relative:0.00}");
            if (r.Jump) sb.Append(" — СКАЧОК СЛОЖНОСТИ");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>
/// «Отчёт по волнам» (docs/DECISIONS_4.md §70, TZ §18.3): per wave the budget, the actual cost, the number of enemies,
/// the average and maximum level, the expected duration, and — from the runs of the autotest — the reached goals
/// and the damage to the bases; the difficulty against the previous wave, a jump highlighted, before a battle.
/// </summary>
public static class WaveReports
{
    public static WaveReport Build(ContentDatabase db, ScenarioDef sc, IEnumerable<BattleReport>? runs = null, double jumpRatio = 1.5)
    {
        var plan = WavePlan.Of(db, sc);
        var runList = (runs ?? Enumerable.Empty<BattleReport>()).ToList();
        var rep = new WaveReport { Scenario = sc.DisplayName, JumpRatio = jumpRatio };
        var levels = new Dictionary<UnitDef, double>(ReferenceEqualityComparer.Instance);
        double prev = 0;
        foreach (var p in plan)
        {
            var lv = p.Spawns.Select(s => WavePlan.LevelOf(db, s, levels)).ToList();
            double walk = p.Spawns.Select(s => WavePlan.WalkSeconds(db, sc, s)).DefaultIfEmpty(0).Max();
            var stats = runList.Select(r => r.WaveStats.FirstOrDefault(w => w.Index == p.Index)).Where(w => w != null && (w.Spawned > 0 || w.Started >= 0)).Select(w => w!).ToList();
            double rel = p.Index == 0 || prev <= 0 ? 0 : p.Cost / prev;
            rep.Rows.Add(new WaveReportRow
            {
                Index = p.Index, Name = p.Def.Name, Start = p.StartText, Budget = p.Budget, Cost = Math.Round(p.Cost, 1), Count = p.Spawns.Count,
                AvgLevel = lv.Count == 0 ? 0 : Math.Round(lv.Average(), 2), MaxLevel = lv.Count == 0 ? 0 : Math.Round(lv.Max(), 2),
                ExpectedSeconds = Math.Round(p.SpawnSeconds + walk, 1), ByBudget = p.ByBudget,
                Runs = stats.Count,
                ReachedGoal = stats.Count == 0 ? null : stats.Average(w => w.ReachedGoal),
                BaseDamage = stats.Count == 0 ? null : Math.Round(stats.Average(w => w.BaseDamage), 1),
                Seconds = stats.Any(w => w.Started >= 0 && w.Ended >= 0) ? Math.Round(stats.Where(w => w.Started >= 0 && w.Ended >= 0).Average(w => w.Ended - w.Started), 1) : null,
                Relative = Math.Round(rel, 2), Jump = rel >= jumpRatio,
            });
            if (p.Cost > 0) prev = p.Cost;
        }
        return rep;
    }
}
