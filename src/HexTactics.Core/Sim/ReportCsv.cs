using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HexTactics.Core.Sim;

/// <summary>
/// Reports as CSV (ТЗ ч. 5, 19.5; docs/DECISIONS_5.md §86): RFC 4180 — a comma between cells, quotes around a cell with
/// a comma, a quote or a line break, numbers with a dot; written in UTF-8 with a BOM so table editors read Cyrillic.
/// </summary>
public static class ReportCsv
{
    private static string Cell(object? v) => v switch
    {
        null => "",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        float f => f.ToString("0.####", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Quote(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string Row(params object?[] cells) => string.Join(",", cells.Select(c => Quote(Cell(c))));

    private static string Lines(IEnumerable<string> rows) => string.Join("\r\n", rows) + "\r\n";

    /// <summary>Every unit of a run (ТЗ 19.2).</summary>
    public static string Units(BattleReport r) => Lines(new[]
    {
        Row("id", "name", "template", "side", "wave", "group", "damage_dealt", "damage_taken", "dps", "healing", "overheal", "control_s", "buffs",
            "path_hexes", "waiting_ticks", "casts", "misses", "kills", "death_tick"),
    }.Concat(r.UnitStats.Select(u => Row(u.Id, u.Name, u.Template, u.Side, u.Wave, u.Group, u.DamageDealt, u.DamageTaken, u.Dps, u.Healing, u.Overheal,
        u.ControlSeconds, u.Buffs, u.PathHexes, u.WaitingTicks, u.Casts, u.Misses, u.Kills, u.DeathTick))));

    /// <summary>Every wave of the runs (ТЗ 19.3): a run per seed.</summary>
    public static string Waves(IReadOnlyList<BattleReport> reports) => Lines(new[]
    {
        Row("seed", "wave", "name", "planned", "spawned", "failed", "max_alive", "killed", "leaks", "base_damage", "started_s", "ended_s", "completion_ticks", "stuck_units"),
    }.Concat(reports.SelectMany(r => r.WaveStats.Select(w => Row(r.Seed, w.Index + 1, w.Name, w.Planned, w.Spawned, w.Failed, w.MaxAlive, w.Killed, w.ReachedGoal,
        w.BaseDamage, w.Started, w.Ended, w.CompletionTicks, w.StuckUnits)))));

    /// <summary>A line per run (ТЗ 19.4).</summary>
    public static string Runs(IReadOnlyList<BattleReport> reports) => Lines(new[]
    {
        Row("run", "seed", "outcome", "reason", "stop", "seconds", "ticks", "waves_completed", "waves_total", "base_health", "leaks", "defenders_lost",
            "enemies_killed", "path_errors", "schema_version", "sim_version", "validation_errors", "tick_ms_mean", "tick_ms_max"),
    }.Concat(reports.Select((r, i) => Row(i + 1, r.Seed, r.Outcome, r.Reason, r.StopReason, r.Seconds, r.Ticks, r.WavesCompleted, r.WavesTotal,
        Math.Round(r.BaseHealth, 4), r.EnemiesReachedGoal, r.DefendersLost, r.EnemiesKilled, BatchReport.PathErrorKinds.Sum(k => r.AiErrors.GetValueOrDefault(k)),
        r.SchemaVersion, r.SimVersion, r.ValidationErrors, r.TickMsMean, r.TickMsMax))));

    /// <summary>The aggregates of a batch as «metric, value» (ТЗ 19.5).</summary>
    public static string Summary(BatchReport b)
    {
        var rows = new List<string> { Row("metric", "value") };
        void Add(string k, object? v) => rows.Add(Row(k, v));
        Add("scenario", b.Scenario);
        Add("runs", b.RunsCount);
        Add("base_seed", b.BaseSeed);
        Add("win_rate", b.WinRate);
        Add("win_rate_low95", b.WinRateLow);
        Add("win_rate_high95", b.WinRateHigh);
        Add("mean_seconds", b.MeanSeconds);
        Add("median_seconds", b.MedianSeconds);
        Add("min_seconds", b.MinSeconds);
        Add("max_seconds", b.MaxSeconds);
        Add("std_seconds", b.StdSeconds);
        Add("mean_waves", b.MeanWaves);
        Add("mean_base_health", b.MeanBaseHealth);
        Add("min_base_health", b.MinBaseHealth);
        Add("mean_leaks", b.MeanLeaks);
        Add("max_leaks", b.MaxLeaks);
        foreach (var (leaks, runs) in b.LeakDistribution) Add($"runs_with_{leaks}_leaks", runs);
        Add("path_errors", b.PathErrors);
        Add("worst_run", b.WorstRun >= 0 ? b.WorstRun + 1 : null);
        Add("worst_seed", b.WorstRun >= 0 && b.WorstRun < b.Runs.Count ? b.Runs[b.WorstRun].Seed : null);
        return Lines(rows);
    }

    /// <summary>Writes with a BOM (table editors then read UTF-8).</summary>
    public static void Write(string path, string csv) => File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>Reads CSV back into rows of cells (quotes, doubled quotes, line breaks inside quotes).</summary>
    public static List<string[]> Parse(string csv)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        int i = csv.Length > 0 && csv[0] == '﻿' ? 1 : 0;
        for (; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }
}
