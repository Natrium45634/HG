using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>A reference of the calibration: a template, alone or with its typical pack.</summary>
public sealed record CalibrationRef(string Label, string Unit, int Pack = 0, string PackUnit = "");

/// <summary>A pair of references: the forecast of the power estimate against the duels.</summary>
public sealed class CalibrationRow
{
    public string A { get; init; } = "";
    public string B { get; init; } = "";
    public double PowerA { get; init; }
    public double PowerB { get; init; }
    /// <summary>The chance of A by the estimate.</summary>
    public double Predicted { get; init; }
    /// <summary>The share of A's wins (a draw — a half).</summary>
    public double Actual { get; set; }
    public int Runs { get; set; }
    /// <summary>A's damage per second and the share of A's health left, mean over the runs.</summary>
    public double DpsA { get; set; }
    public double DpsB { get; set; }
    public double SurvivalA { get; set; }
    public double Error => Actual - Predicted;
    public bool Bad => Math.Abs(Error) > Calibration.BadError;
}

public sealed class CalibrationReport
{
    public List<CalibrationRow> Rows { get; } = new();
    public int Runs { get; init; }
    public ulong Seed { get; init; }
    public string RatingHash { get; init; } = "";
    public double MeanAbsError => Rows.Count == 0 ? 0 : Rows.Average(r => Math.Abs(r.Error));
    public IEnumerable<CalibrationRow> Bad => Rows.Where(r => r.Bad).OrderByDescending(r => Math.Abs(r.Error));

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Калибровка оценки силы: {Rows.Count} пар × {Runs} боёв (seed {Seed}…), правила оценки {RatingHash}");
        sb.AppendLine($"Средняя ошибка прогноза доли побед: {MeanAbsError:P0}. Оценка силы — эвристика до калибровки, не «истинный уровень».");
        foreach (var r in Rows.OrderByDescending(r => Math.Abs(r.Error)))
            sb.AppendLine($"{(r.Bad ? "⚠ " : "  ")}{r.A} против {r.B}: прогноз {r.Predicted:P0}, факт {r.Actual:P0} (сила {r.PowerA:0.00} / {r.PowerB:0.00}; " +
                          $"DPS {r.DpsA:0.#} / {r.DpsB:0.#}; осталось здоровья у первого {r.SurvivalA:P0})");
        var bad = Bad.ToList();
        sb.AppendLine(bad.Count == 0
            ? "Плохо предсказанных пар нет."
            : $"Плохо предсказано пар: {bad.Count} (ошибка больше {BadErrorText}). Поправьте веса ролей и кривые (Правила → «Оценка силы») и повторите пакет.");
        return sb.ToString();
    }

    private static string BadErrorText => Calibration.BadError.ToString("P0", CultureInfo.GetCultureInfo("ru-RU"));

    public string ToCsv() => string.Join("\r\n", new[] { ReportCsv.Row("a", "b", "power_a", "power_b", "predicted", "actual", "error", "runs", "dps_a", "dps_b", "survival_a", "bad") }
        .Concat(Rows.Select(r => ReportCsv.Row(r.A, r.B, r.PowerA, r.PowerB, r.Predicted, r.Actual, r.Error, r.Runs, r.DpsA, r.DpsB, r.SurvivalA, r.Bad)))) + "\r\n";
}

/// <summary>
/// Calibration of the power estimate (ТЗ ч. 5, 24; docs/DECISIONS_5.md §88): reference archetypes fight each other in
/// duels on several seeds; the share of wins is set against the forecast of their powers (a Bradley–Terry chance with the
/// powers squared — the estimate is a heuristic); the pairs it forecasts badly are listed to adjust the weights.
/// </summary>
public static class Calibration
{
    public const double BadError = 0.25;
    public const double Exponent = 2;

    /// <summary>The references of ТЗ 24.1 found in the project: warrior, tank, raider, shooter, healer, controller, undead, the leader alone and with its pack.</summary>
    public static List<CalibrationRef> DefaultSet(ContentDatabase db)
    {
        var all = new List<CalibrationRef>
        {
            new("базовый воин", "knight"), new("защищённый танк", "sentinel"), new("быстрый рейдер", "wolf_scout"), new("дальний стрелок", "archer"),
            new("лекарь", "priestess"), new("контролёр", "shaman"), new("нежить", "skeleton"), new("волк-вожак один", "wolf_leader"),
            new("волк-вожак со стаей", "wolf_leader", 3, "wolf"),
        };
        return all.Where(r => db.Unit(r.Unit) != null && (r.Pack == 0 || db.Unit(r.PackUnit) != null)).ToList();
    }

    /// <summary>The power of a reference as a side: the unit (in its group's context) and its pack.</summary>
    public static double PowerOf(ContentDatabase db, CalibrationRef r)
    {
        var u = db.Unit(r.Unit)!;
        if (r.Pack <= 0) return PowerCache.Evaluate(db, u).Power;
        var pack = db.Unit(r.PackUnit)!;
        var ctx = new PowerContext { Kind = PowerContextKind.TypicalGroup, Allies = r.Pack, AllyDefs = Enumerable.Repeat(pack, r.Pack).ToList() };
        var packCtx = new PowerContext { Kind = PowerContextKind.TypicalGroup, Allies = r.Pack, AllyDefs = Enumerable.Repeat(pack, r.Pack - 1).Append(u).ToList() };
        return PowerCache.Evaluate(db, u, ctx).Power + r.Pack * PowerCache.Evaluate(db, pack, packCtx).Power;
    }

    public static double Forecast(double a, double b) => Math.Pow(a, Exponent) / Math.Max(1e-9, Math.Pow(a, Exponent) + Math.Pow(b, Exponent));

    /// <summary>Every pair of the references, <paramref name="runs"/> duels each (seed + i), in parallel; deterministic.</summary>
    public static CalibrationReport Run(ContentDatabase db, IReadOnlyList<CalibrationRef> refs, int runs, ulong seed, double maxSeconds = 90, int threads = 0,
        CancellationToken cancel = default, Action<int, int>? progress = null)
    {
        var report = new CalibrationReport { Runs = runs, Seed = seed, RatingHash = LevelFit.RatingHash(db) };
        // The duels run on a copy of the project with an arena added: the author's content is not touched.
        var work = db.Clone();
        work.Add(Arena(work));
        var pairs = new List<(CalibrationRef A, CalibrationRef B)>();
        for (int i = 0; i < refs.Count; i++)
            for (int j = i + 1; j < refs.Count; j++)
                pairs.Add((refs[i], refs[j]));
        foreach (var (a, b) in pairs)
        {
            double pa = PowerOf(db, a), pb = PowerOf(db, b);
            report.Rows.Add(new CalibrationRow { A = a.Label, B = b.Label, PowerA = pa, PowerB = pb, Predicted = Forecast(pa, pb) });
        }
        var results = new (double Win, double DpsA, double DpsB, double Surv)[pairs.Count, runs];
        int done = 0, total = pairs.Count * runs;
        var po = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount, CancellationToken = cancel };
        try
        {
            Parallel.For(0, total, po, k =>
            {
                int p = k / runs, i = k % runs;
                // Sides alternate by the run: who stands where does not decide the pair.
                bool swap = i % 2 == 1;
                var (a, b) = pairs[p];
                var sc = Duel(work, swap ? b : a, swap ? a : b);
                var r = AutoBattle.Run(work, sc, seed + (ulong)(p * 1000 + i), new AutoBattleOptions { MaxSeconds = maxSeconds, AutoPlayer = true, PlacePreset = false }, cancel);
                double win = r.Outcome == "победа" ? 1 : r.Outcome == "поражение" ? 0 : 0.5;
                double secs = Math.Max(1, r.Seconds);
                double dpsFirst = r.DamageDealt.GetValueOrDefault(BattleReport.PlayerSide) / secs, dpsSecond = r.DamageDealt.GetValueOrDefault(BattleReport.EnemySide) / secs;
                var first = r.UnitStats.Where(u => u.Side == BattleReport.PlayerSide).ToList();
                double surv = first.Count == 0 ? 0 : first.Count(u => u.DeathTick < 0) / (double)first.Count;
                results[p, i] = swap ? (1 - win, dpsSecond, dpsFirst, double.NaN) : (win, dpsFirst, dpsSecond, surv);
                progress?.Invoke(Interlocked.Increment(ref done), total);
            });
        }
        catch (OperationCanceledException)
        {
        }
        for (int p = 0; p < pairs.Count; p++)
        {
            var row = report.Rows[p];
            var got = Enumerable.Range(0, runs).Select(i => results[p, i]).Where(x => x.Win == x.Win && (x.DpsA > 0 || x.DpsB > 0 || x.Win != 0)).ToList();
            row.Runs = got.Count;
            if (got.Count == 0) continue;
            row.Actual = got.Average(x => x.Win);
            row.DpsA = got.Average(x => x.DpsA);
            row.DpsB = got.Average(x => x.DpsB);
            var surv = got.Where(x => !double.IsNaN(x.Surv)).ToList();
            row.SurvivalA = surv.Count == 0 ? 0 : surv.Average(x => x.Surv);
        }
        return report;
    }

    public const string ArenaId = "calibration_arena";

    /// <summary>An open field of the first plain terrain of the project.</summary>
    public static MapDef Arena(ContentDatabase db)
    {
        string terrain = db.All<TerrainDef>().FirstOrDefault(t => t.MoveCost <= 1 && !t.Hazard && t.IsPassableFor("walk") && t.AllowsPlacement)?.Id ?? "grass";
        return MapDef.CreateRhombus(ArenaId, 11, 6, terrain);
    }

    private static readonly (int Q, int R)[] PackSpots = { (0, -1), (0, 1), (-1, 1), (-1, 0), (-1, 2), (-2, 2) };

    /// <summary>A duel on the arena (it must be in <paramref name="db"/>): the first side on the left (the player's faction), the second on the right.</summary>
    public static ScenarioDef Duel(ContentDatabase db, CalibrationRef first, CalibrationRef second)
    {
        string profile = db.Get<AiProfileDef>("aggressive") != null ? "aggressive" : "";
        var sc = new ScenarioDef
        {
            Id = "calibration_duel", Name = $"{first.Label} против {second.Label}", Map = ArenaId, Mode = BattleMode.FreeBattle,
            Victory = new() { new ConditionDef { Type = ConditionType.AllEnemiesDead } },
            Defeat = new() { new ConditionDef { Type = ConditionType.AllPlayerUnitsDead } },
            Sides = new() { new ScenarioSideDef { Faction = "player", Controller = SideController.Player }, new ScenarioSideDef { Faction = "enemy", Controller = SideController.Ai } },
        };
        void Place(CalibrationRef r, string faction, HexCoord at, int mirror, HexDirection facing)
        {
            sc.Units.Add(new ScenarioUnitDef { Unit = r.Unit, C = at, Facing = facing, Faction = faction, AiProfile = profile });
            for (int k = 0; k < Math.Min(r.Pack, PackSpots.Length); k++)
            {
                var (dq, dr) = PackSpots[k];
                sc.Units.Add(new ScenarioUnitDef { Unit = r.PackUnit, C = new HexCoord(at.Q + dq * mirror, at.R + dr), Facing = facing, Faction = faction, AiProfile = profile });
            }
        }
        Place(first, "player", new HexCoord(2, 2), 1, HexDirection.E);
        Place(second, "enemy", new HexCoord(7, 3), -1, HexDirection.W);
        return sc;
    }
}
