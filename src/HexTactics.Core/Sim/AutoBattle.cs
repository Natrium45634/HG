using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>«Остановка на событии» (docs/DECISIONS_4.md §64): what pauses the battle for diagnostics.</summary>
public enum BreakpointKind
{
    [Ed("Смерть юнита с тегом", "Тег, метка, категория или ID шаблона (например, вожак)")] UnitDeath,
    [Ed("Срабатывание пассива", "ID пассива; пусто — любого")] Passive,
    [Ed("Блокировка маршрута")] RouteBlocked,
    [Ed("Враг у базы", "Враг дошёл до базы или бьёт её")] EnemyAtBase,
    [Ed("Начало волны")] WaveStarted,
    [Ed("Конец волны")] WaveEnded,
    [Ed("Своё условие")] Condition,
}

public sealed class BreakpointDef
{
    [Ed("Событие")] public BreakpointKind Kind { get; set; } = BreakpointKind.UnitDeath;
    [Ed("Значение", "Тег юнита или ID пассива"), EdShowIf(nameof(Kind), BreakpointKind.UnitDeath, BreakpointKind.Passive)] public string Value { get; set; } = "";
    [Ed("Условие"), EdShowIf(nameof(Kind), BreakpointKind.Condition)] public ConditionDef? Condition { get; set; }
    [Ed("Включена")] public bool Enabled { get; set; } = true;

    public override string ToString() => EdUtil.EnumLabel(Kind) + (Value != "" ? $" «{Value}»" : "");
}

/// <summary>Watches a simulation and pauses it when a breakpoint matches (the reason is kept in <see cref="Hit"/>).</summary>
public sealed class Breakpoints
{
    private readonly Simulation _sim;
    public List<BreakpointDef> List { get; }
    public string? Hit { get; private set; }

    private Breakpoints(Simulation sim, List<BreakpointDef> list)
    {
        _sim = sim;
        List = list;
    }

    public static Breakpoints Attach(Simulation sim, List<BreakpointDef> list)
    {
        var b = new Breakpoints(sim, list);
        sim.Observed += b.OnEvent;
        sim.Stepped += b.OnTick;
        return b;
    }

    public void Detach()
    {
        _sim.Observed -= OnEvent;
        _sim.Stepped -= OnTick;
    }

    /// <summary>The reason of the last stop, cleared (the battle goes on after it).</summary>
    public string? Consume()
    {
        var h = Hit;
        Hit = null;
        return h;
    }

    private void Stop(string reason)
    {
        Hit ??= reason;
        _sim.RequestBreak(reason);
    }

    private bool IsEnemy(Unit? u) => u != null && !_sim.IsPlayerFaction(u.FactionId) && _sim.Options.AiFactions.Contains(u.FactionId);

    private void OnEvent(SimEvent e)
    {
        foreach (var b in List)
        {
            if (!b.Enabled) continue;
            switch (b.Kind)
            {
                case BreakpointKind.UnitDeath when e.Type == SimEventType.Death:
                {
                    var u = _sim.GetUnit(e.TargetUnit);
                    if (u != null && (b.Value == "" || u.Tag == b.Value || u.HasTag(b.Value) || _sim.Conditions.TypeMatches(u, b.Value)))
                        Stop($"погиб {u.Name}" + (b.Value != "" ? $" («{b.Value}»)" : ""));
                    break;
                }
                case BreakpointKind.Passive when e.Type == SimEventType.PassiveTriggered && (b.Value == "" || e.DamageType == b.Value):
                    Stop(e.Text);
                    break;
                case BreakpointKind.RouteBlocked when e.Type == SimEventType.AiProblem && e.DamageType is "route_blocked" or "no_path":
                    Stop(e.Text);
                    break;
                case BreakpointKind.EnemyAtBase when e.Type == SimEventType.GoalReached && IsEnemy(_sim.GetUnit(e.SourceUnit)):
                    Stop(e.Text);
                    break;
                case BreakpointKind.EnemyAtBase when e.Type == SimEventType.Damage && e.Value > 0 && _sim.GetUnit(e.TargetUnit) is { IsStructure: true } s
                                                     && _sim.IsPlayerFaction(s.FactionId) && IsEnemy(_sim.GetUnit(e.SourceUnit)):
                    Stop($"враг бьёт «{s.Name}»");
                    break;
                case BreakpointKind.WaveStarted when e.Type == SimEventType.WaveStarted:
                    Stop("началась волна: " + e.Text);
                    break;
                case BreakpointKind.WaveEnded when e.Type == SimEventType.WaveEnded:
                    Stop(e.Text);
                    break;
            }
        }
    }

    private readonly HashSet<BreakpointDef> _conditionHeld = new();

    private void OnTick()
    {
        foreach (var b in List)
        {
            if (!b.Enabled || b.Kind != BreakpointKind.Condition || b.Condition == null) continue;
            bool now = _sim.Scenario.Evaluate(b.Condition);
            // Stops when the condition becomes true, not every tick while it holds.
            if (now && _conditionHeld.Add(b)) Stop("условие: " + b.Condition.Describe());
            else if (!now) _conditionHeld.Remove(b);
        }
    }
}

public sealed class AutoBattleOptions
{
    /// <summary>The longest battle of a run, s (then it stops «не завершён»).</summary>
    public double MaxSeconds { get; init; } = 900;
    /// <summary>The defenders are driven by AI profiles (autotest without a player).</summary>
    public bool AutoPlayer { get; init; } = true;
    /// <summary>«TD с расстановкой»: the preset placement is placed and the preparation confirmed.</summary>
    public bool PlacePreset { get; init; } = true;
    public List<BreakpointDef> StopOn { get; init; } = new();
}

/// <summary>One run of a batch, shortly.</summary>
public sealed record BatchRun(int Index, ulong Seed, string Outcome, string Reason, int Waves, double Seconds, double BaseHealth, int DefendersLost, int Leaks);

/// <summary>The summary of a batch (docs/DECISIONS_4.md §64, TZ §10.6), in the order of the seeds.</summary>
public sealed class BatchReport
{
    public string Scenario { get; set; } = "";
    public ulong BaseSeed { get; set; }
    public int RunsCount { get; set; }
    public List<BatchRun> Runs { get; set; } = new();
    public double WinRate { get; set; }
    public double MeanWaves { get; set; }
    public double MeanBaseHealth { get; set; }
    public double MinBaseHealth { get; set; }
    public double MaxBaseHealth { get; set; }
    public double MeanSeconds { get; set; }
    public double MinSeconds { get; set; }
    public double MaxSeconds { get; set; }
    public double StdSeconds { get; set; }
    public List<(string Reason, int Count)> TopDefeatReasons { get; set; } = new();
    public Dictionary<string, int> AiErrors { get; set; } = new();

    public static BatchReport Of(string scenario, ulong baseSeed, IReadOnlyList<BattleReport> reports)
    {
        var b = new BatchReport { Scenario = scenario, BaseSeed = baseSeed, RunsCount = reports.Count };
        for (int i = 0; i < reports.Count; i++)
        {
            var r = reports[i];
            b.Runs.Add(new BatchRun(i, r.Seed, r.Outcome, r.Reason, r.WavesCompleted, r.Seconds, Math.Round(r.BaseHealth, 4), r.DefendersLost, r.EnemiesReachedGoal));
            foreach (var kv in r.AiErrors) b.AiErrors[kv.Key] = b.AiErrors.GetValueOrDefault(kv.Key) + kv.Value;
        }
        if (reports.Count == 0) return b;
        b.WinRate = reports.Count(r => r.Won) / (double)reports.Count;
        b.MeanWaves = reports.Average(r => r.WavesCompleted);
        b.MeanBaseHealth = reports.Average(r => r.BaseHealth);
        b.MinBaseHealth = reports.Min(r => r.BaseHealth);
        b.MaxBaseHealth = reports.Max(r => r.BaseHealth);
        b.MeanSeconds = reports.Average(r => r.Seconds);
        b.MinSeconds = reports.Min(r => r.Seconds);
        b.MaxSeconds = reports.Max(r => r.Seconds);
        b.StdSeconds = Math.Sqrt(reports.Average(r => (r.Seconds - b.MeanSeconds) * (r.Seconds - b.MeanSeconds)));
        b.TopDefeatReasons = reports.Where(r => !r.Won).Select(r => r.Reason != "" ? r.Reason : r.StopReason).Where(t => t != "")
            .GroupBy(t => t).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(5).Select(g => (g.Key, g.Count())).ToList();
        return b;
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Пакетный прогон «{Scenario}»: {RunsCount} прогонов, seed {BaseSeed}…{BaseSeed + (ulong)Math.Max(0, RunsCount - 1)}");
        sb.AppendLine($"Побед: {WinRate:P0}; волн в среднем {F(MeanWaves)}");
        sb.AppendLine($"Здоровье баз: среднее {MeanBaseHealth:P0}, мин. {MinBaseHealth:P0}, макс. {MaxBaseHealth:P0}");
        sb.AppendLine($"Длительность: среднее {F(MeanSeconds)} с, мин. {F(MinSeconds)}, макс. {F(MaxSeconds)}, разброс (σ) {F(StdSeconds)} с");
        sb.AppendLine("Частые причины поражения: " + (TopDefeatReasons.Count == 0 ? "нет" : string.Join("; ", TopDefeatReasons.Select(t => $"{t.Reason} ×{t.Count}"))));
        if (AiErrors.Count > 0) sb.AppendLine("Ошибки ИИ: " + string.Join(", ", AiErrors.Select(k => $"{BattleReport.AiErrorName(k.Key)} ×{k.Value}")));
        foreach (var r in Runs)
            sb.AppendLine($"  #{r.Index + 1} seed {r.Seed}: {r.Outcome}, волн {r.Waves}, {F(r.Seconds)} с, базы {r.BaseHealth:P0}, потери {r.DefendersLost}, прорывы {r.Leaks}" + (r.Reason != "" ? $" — {r.Reason}" : ""));
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        Scenario, BaseSeed, RunsCount, WinRate, MeanWaves, MeanBaseHealth, MinBaseHealth, MaxBaseHealth, MeanSeconds, MinSeconds, MaxSeconds, StdSeconds,
        TopDefeatReasons = TopDefeatReasons.Select(t => new { t.Reason, t.Count }), AiErrors, Runs,
    }, BattleReport.JsonOptions);
}

/// <summary>
/// The autobattle (docs/DECISIONS_4.md §64): the same simulation as the battle, without rendering («только логика»).
/// Every run works on its own copy of the scenario — the author's data never change; a batch runs seed = base + i,
/// in parallel, and the results are kept in the order of the seeds, so they do not depend on the number of threads.
/// </summary>
public static class AutoBattle
{
    /// <summary>A simulation ready for an autobattle (its own copy of the scenario), with its report attached.</summary>
    public static (Simulation Sim, BattleReport Report, Breakpoints Breaks) Prepare(ContentDatabase db, ScenarioDef scenario, ulong seed, AutoBattleOptions? options = null)
    {
        var o = options ?? new AutoBattleOptions();
        var sc = ContentJson.Clone(scenario);
        var sim = ScenarioSystem.Create(db, sc, seed, admin: false, autoPlayer: o.AutoPlayer);
        var report = BattleReport.Attach(sim, scenario.DisplayName, seed);
        var breaks = Breakpoints.Attach(sim, o.StopOn);
        if (sim.Scenario.Preparing && o.PlacePreset)
        {
            sim.Placement.PlacePreset(free: true);
            sim.Scenario.EndPreparation("автотест: расстановка по умолчанию");
        }
        return (sim, report, breaks);
    }

    /// <summary>Plays a prepared simulation to its end, a breakpoint or the time limit.</summary>
    public static BattleReport Play(Simulation sim, BattleReport report, Breakpoints breaks, AutoBattleOptions? options = null, CancellationToken cancel = default)
    {
        var o = options ?? new AutoBattleOptions();
        long limit = sim.Tick + sim.TicksFor(o.MaxSeconds) + sim.TicksFor(sim.Scenario.Def?.Placement.Duration ?? 0) + 1;
        string stop = "";
        while (sim.Outcome == BattleOutcome.None)
        {
            if (cancel.IsCancellationRequested)
            {
                stop = "прервано";
                break;
            }
            if (sim.Tick >= limit || sim.Scenario.BattleTime >= o.MaxSeconds)
            {
                stop = $"лимит прогона {o.MaxSeconds:0} с";
                break;
            }
            sim.Step();
            sim.ConsumeAutopause();
            if (breaks.Consume() is { } hit)
            {
                stop = "остановка на событии: " + hit;
                break;
            }
        }
        breaks.Detach();
        return report.Finish(stop);
    }

    public static BattleReport Run(ContentDatabase db, ScenarioDef scenario, ulong seed, AutoBattleOptions? options = null, CancellationToken cancel = default)
    {
        var (sim, report, breaks) = Prepare(db, scenario, seed, options);
        return Play(sim, report, breaks, options, cancel);
    }

    /// <summary>N runs with seeds base + i; <paramref name="threads"/> ≤ 0 — as many as the machine has.</summary>
    public static (BatchReport Summary, List<BattleReport> Reports) Batch(ContentDatabase db, ScenarioDef scenario, int runs, ulong baseSeed,
        AutoBattleOptions? options = null, int threads = 0, Action<int>? progress = null, CancellationToken cancel = default)
    {
        runs = Math.Max(1, runs);
        var results = new BattleReport[runs];
        int done = 0;
        var po = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount, CancellationToken = cancel };
        // Breakpoints make no sense in a batch: every run goes to its end.
        var o = options == null ? new AutoBattleOptions() : new AutoBattleOptions { MaxSeconds = options.MaxSeconds, AutoPlayer = options.AutoPlayer, PlacePreset = options.PlacePreset };
        try
        {
            Parallel.For(0, runs, po, i =>
            {
                results[i] = Run(db, scenario, baseSeed + (ulong)i, o, cancel);
                progress?.Invoke(Interlocked.Increment(ref done));
            });
        }
        catch (OperationCanceledException)
        {
        }
        var list = results.Where(r => r != null).ToList();
        return (BatchReport.Of(scenario.DisplayName, baseSeed, list), list);
    }

    /// <summary>Saves a batch to a folder of reports (JSON + text), apart from the content; returns the base path.</summary>
    public static string Save(string folder, BatchReport batch, IReadOnlyList<BattleReport>? reports = null, string? stamp = null)
    {
        Directory.CreateDirectory(folder);
        string name = $"{Safe(batch.Scenario)}_{stamp ?? DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}";
        string path = Path.Combine(folder, name);
        File.WriteAllText(path + ".json", batch.ToJson());
        var sb = new StringBuilder(batch.ToText());
        if (reports != null && reports.Count == 1) sb.AppendLine().Append(reports[0].ToText());
        File.WriteAllText(path + ".txt", sb.ToString());
        return path;
    }

    /// <summary>Saves one report (JSON + text).</summary>
    public static string Save(string folder, BattleReport report, ContentDatabase? db = null, string? stamp = null)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{Safe(report.Scenario)}_seed{report.Seed}_{stamp ?? DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}");
        File.WriteAllText(path + ".json", report.ToJson());
        File.WriteAllText(path + ".txt", report.ToText(db));
        return path;
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.Length == 0 ? "scenario" : sb.ToString();
    }
}
