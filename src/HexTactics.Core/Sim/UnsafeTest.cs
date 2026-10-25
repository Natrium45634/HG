using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>What an unsafe test ignored and what happened.</summary>
public sealed class UnsafeTestResult
{
    /// <summary>The errors of the check that would have blocked the start.</summary>
    public List<ScenarioProblem> Ignored { get; } = new();
    /// <summary>Exceptions caught on the way (the creation of the battle or a tick).</summary>
    public List<string> Exceptions { get; } = new();
    public bool Started { get; set; }
    public BattleReport? Report { get; set; }

    public string Text
    {
        get
        {
            var lines = new List<string> { $"НЕБЕЗОПАСНЫЙ ТЕСТ — проигнорировано ошибок проверки: {Ignored.Count}" };
            lines.AddRange(Ignored.Take(12).Select(p => "  ⛔ " + p.Message));
            if (Ignored.Count > 12) lines.Add($"  … и ещё {Ignored.Count - 12}");
            lines.Add(Started ? "Бой создан" : "Бой не удалось создать");
            if (Exceptions.Count > 0) lines.AddRange(Exceptions.Select(e => "  ✗ " + e));
            if (Report != null) lines.Add($"Итог: {Report.Outcome}{(Report.Reason != "" ? " — " + Report.Reason : "")}, {Report.Seconds:0.#} с, тиков {Report.Ticks}");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>
/// A scenario with errors started anyway (ТЗ ч. 5, 26.4; docs/DECISIONS_5.md §86): only explicitly, only as a test; the
/// report lists the ignored errors first; nothing inside throws out — an exception ends the run and is written down.
/// </summary>
public static class UnsafeTest
{
    /// <summary>The errors that block an ordinary start (the same check the editor's launch uses).</summary>
    public static List<ScenarioProblem> Errors(ContentDatabase db, ScenarioDef sc)
    {
        var map = db.Get<MapDef>(sc.Map);
        if (map == null) return new List<ScenarioProblem> { new(IssueSeverity.Error, "Map", $"Карта «{sc.Map}» не найдена") };
        try
        {
            return ScenarioObjectsCheck.All(db, sc, map).Where(p => p.Severity == IssueSeverity.Error).ToList();
        }
        catch (Exception e)
        {
            return new List<ScenarioProblem> { new(IssueSeverity.Error, "Check", $"проверка упала: {e.GetType().Name}: {e.Message}") };
        }
    }

    /// <summary>Runs the battle without the player for <paramref name="seconds"/> (or to its end).</summary>
    public static UnsafeTestResult Run(ContentDatabase db, ScenarioDef sc, double seconds, ulong? seed = null)
    {
        var res = new UnsafeTestResult();
        res.Ignored.AddRange(Errors(db, sc));
        Simulation sim;
        try
        {
            sim = ScenarioSystem.Create(db, sc, seed, admin: true, autoPlayer: true);
        }
        catch (Exception e)
        {
            res.Exceptions.Add($"создание боя: {e.GetType().Name}: {e.Message}");
            return res;
        }
        res.Started = true;
        var report = BattleReport.Attach(sim, sc.Id, seed ?? 0);
        var driver = new SimDriver(sim) { CatchErrors = true };
        long n = sim.TicksFor(seconds);
        for (long i = 0; i < n && sim.Outcome == BattleOutcome.None; i++)
        {
            driver.StepOnce();
            if (driver.Error != null)
            {
                res.Exceptions.Add(driver.Error);
                break;
            }
        }
        try
        {
            res.Report = report.Finish(res.Exceptions.Count > 0 ? "остановлен ошибкой симуляции" : "");
            res.Report.ValidationErrors = res.Ignored.Count;
        }
        catch (Exception e)
        {
            res.Exceptions.Add($"отчёт: {e.GetType().Name}: {e.Message}");
        }
        return res;
    }
}
