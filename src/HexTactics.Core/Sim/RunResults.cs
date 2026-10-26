using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HexTactics.Core.Sim;

public enum ResultKind
{
    /// <summary>A batch of autobattles (BatchReport JSON).</summary>
    Batch,
    /// <summary>One battle (BattleReport JSON).</summary>
    Battle,
    /// <summary>The calibration of the power estimate (calibration_*.csv).</summary>
    Calibration,
}

/// <summary>A number of a saved result; <see cref="Better"/>: +1 — more is better, −1 — less is better, 0 — neither.</summary>
public sealed record ResultMetric(string Key, string Label, double Value, string Format = "0.##", int Better = 0, double? Low = null, double? High = null)
{
    public string Text => Format == "P0" ? Value.ToString("P0", CultureInfo.InvariantCulture) + (Low is { } l && High is { } h ? $" (95 %: {l:P0}–{h:P0})" : "")
        : Value.ToString(Format, CultureInfo.InvariantCulture);
}

/// <summary>A saved result in the folder of reports: what, when, its numbers and the files it consists of.</summary>
public sealed class ResultEntry
{
    /// <summary>The path without an extension: the files are &lt;Base&gt;.json/.txt/_*.csv.</summary>
    public string Base { get; init; } = "";
    public string File { get; init; } = "";
    public ResultKind Kind { get; init; }
    public string Scenario { get; init; } = "";
    public DateTime When { get; init; }
    public ulong Seed { get; init; }
    public int Runs { get; init; }
    public List<ResultMetric> Metrics { get; } = new();
    /// <summary>Calibration: a pair → (forecast, fact).</summary>
    public Dictionary<string, (double Predicted, double Actual)> Pairs { get; } = new();

    public ResultMetric? Metric(string key) => Metrics.FirstOrDefault(m => m.Key == key);

    public string KindName => Kind switch { ResultKind.Batch => "Пакет", ResultKind.Battle => "Бой", _ => "Калибровка" };

    public string Title => $"{When:yyyy-MM-dd HH:mm} · {KindName} · {Scenario}" + (Kind == ResultKind.Calibration ? $" · пар {Pairs.Count}" : $" · seed {Seed}") +
                           (Runs > 1 ? $" · прогонов {Runs}" : "");

    /// <summary>Every file of the result (the JSON/CSV it was read from, its text and tables).</summary>
    public IEnumerable<string> Files()
    {
        var dir = Path.GetDirectoryName(Base) ?? "";
        var name = Path.GetFileName(Base);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir).Where(f =>
        {
            var n = Path.GetFileName(f);
            return n.StartsWith(name + ".", StringComparison.Ordinal) || n.StartsWith(name + "_", StringComparison.Ordinal) && !n.EndsWith(".json", StringComparison.Ordinal);
        }).OrderBy(f => f, StringComparer.Ordinal);
    }

    /// <summary>The text of the report as it was saved, else its numbers.</summary>
    public string Text()
    {
        var txt = Base + ".txt";
        if (System.IO.File.Exists(txt)) return System.IO.File.ReadAllText(txt);
        var sb = new StringBuilder(Title).AppendLine();
        foreach (var m in Metrics) sb.AppendLine($"{m.Label}: {m.Text}");
        return sb.ToString();
    }
}

/// <summary>A line of a comparison: the value of A, of B, the difference and what it means.</summary>
public sealed record ComparisonRow(string Key, string Label, double? A, double? B, string AText, string BText, string Verdict)
{
    public double? Delta => A is { } a && B is { } b ? b - a : null;
}

public sealed class ResultComparison
{
    public ResultEntry A { get; init; } = null!;
    public ResultEntry B { get; init; } = null!;
    public List<ComparisonRow> Rows { get; } = new();
    public List<string> Notes { get; } = new();

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("A: " + A.Title);
        sb.AppendLine("B: " + B.Title);
        foreach (var n in Notes) sb.AppendLine(n);
        foreach (var r in Rows)
            sb.AppendLine($"{r.Label}: {r.AText} → {r.BText}" + (r.Delta is { } d && Math.Abs(d) > 1e-9 ? $" ({(d > 0 ? "+" : "−")}{Math.Abs(d).ToString("0.###", CultureInfo.InvariantCulture)})" : "") +
                          (r.Verdict != "" ? " — " + r.Verdict : ""));
        return sb.ToString();
    }

    public string ToCsv() => string.Join("\r\n", new[] { ReportCsv.Row("metric", "label", "a", "b", "delta", "verdict") }
        .Concat(Rows.Select(r => ReportCsv.Row(r.Key, r.Label, r.A, r.B, r.Delta, r.Verdict)))) + "\r\n";
}

/// <summary>
/// The screen «Результаты» (ТЗ ч. 5, 25.7; docs/DECISIONS_5.md §89): the saved results of the folder of reports — batches,
/// single battles, calibrations — read back with their numbers, compared two by two (with the 95 % interval of the win
/// rate where there is one: a difference inside it is not called a change) and exported as a table.
/// </summary>
public static class RunResults
{
    /// <summary>Every result of the folder, the newest first; the files that could not be read are listed apart.</summary>
    public static (List<ResultEntry> Entries, List<string> Problems) Load(string folder)
    {
        var entries = new List<ResultEntry>();
        var problems = new List<string>();
        if (!Directory.Exists(folder)) return (entries, problems);
        foreach (var f in Directory.GetFiles(folder, "*.json").Concat(Directory.GetFiles(folder, "calibration_*.csv")))
        {
            try
            {
                if (Read(f) is { } e) entries.Add(e);
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }
        return (entries.OrderByDescending(e => e.When).ThenBy(e => e.File, StringComparer.Ordinal).ToList(), problems);
    }

    /// <summary>A result from its file; null — a file of another kind (not a report).</summary>
    public static ResultEntry? Read(string file)
    {
        var when = System.IO.File.GetLastWriteTime(file);
        var bas = Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file));
        if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return ReadCalibration(file, bas, when);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (r.TryGetProperty("runsCount", out _)) return ReadBatch(r, file, bas, when);
        if (r.TryGetProperty("outcome", out _) && r.TryGetProperty("seed", out _)) return ReadBattle(r, file, bas, when);
        return null;
    }

    private static double Num(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static double? NumOrNull(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static string Str(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static ResultEntry ReadBatch(JsonElement r, string file, string bas, DateTime when)
    {
        int runs = (int)Num(r, "runsCount");
        var e = new ResultEntry { Base = bas, File = file, Kind = ResultKind.Batch, Scenario = Str(r, "scenario"), When = when, Seed = (ulong)Num(r, "baseSeed"), Runs = runs };
        double lost = 0;
        if (r.TryGetProperty("runs", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            lost = list.EnumerateArray().Average(x => Num(x, "defendersLost"));
        e.Metrics.Add(new ResultMetric("win_rate", "Доля побед", Num(r, "winRate"), "P0", +1, NumOrNull(r, "winRateLow"), NumOrNull(r, "winRateHigh")));
        e.Metrics.Add(new ResultMetric("waves", "Волн пройдено (среднее)", Num(r, "meanWaves"), "0.0", +1));
        e.Metrics.Add(new ResultMetric("base_health", "Здоровье баз (среднее)", Num(r, "meanBaseHealth"), "P0", +1));
        e.Metrics.Add(new ResultMetric("base_health_min", "Здоровье баз (худший прогон)", Num(r, "minBaseHealth"), "P0", +1));
        e.Metrics.Add(new ResultMetric("leaks", "Прорывы (среднее)", Num(r, "meanLeaks"), "0.0", -1));
        e.Metrics.Add(new ResultMetric("defenders_lost", "Потери защитников (среднее)", lost, "0.0", -1));
        e.Metrics.Add(new ResultMetric("seconds", "Длительность, с (среднее)", Num(r, "meanSeconds"), "0"));
        e.Metrics.Add(new ResultMetric("seconds_median", "Длительность, с (медиана)", Num(r, "medianSeconds"), "0"));
        e.Metrics.Add(new ResultMetric("path_errors", "Ошибки пути", Num(r, "pathErrors"), "0", -1));
        return e;
    }

    private static ResultEntry ReadBattle(JsonElement r, string file, string bas, DateTime when)
    {
        var e = new ResultEntry { Base = bas, File = file, Kind = ResultKind.Battle, Scenario = Str(r, "scenario"), When = when, Seed = (ulong)Num(r, "seed"), Runs = 1 };
        double hp = 0, max = 0;
        if (r.TryGetProperty("bases", out var bases) && bases.ValueKind == JsonValueKind.Array)
            foreach (var b in bases.EnumerateArray())
            {
                hp += Num(b, "hp");
                max += Num(b, "maxHp");
            }
        double Side(string prop, string side) => r.TryGetProperty(prop, out var d) && d.ValueKind == JsonValueKind.Object ? Num(d, side) : 0;
        e.Metrics.Add(new ResultMetric("win_rate", "Доля побед", Str(r, "outcome") == "победа" ? 1 : 0, "P0", +1));
        e.Metrics.Add(new ResultMetric("waves", "Волн пройдено (среднее)", Num(r, "wavesCompleted"), "0.0", +1));
        e.Metrics.Add(new ResultMetric("base_health", "Здоровье баз (среднее)", max > 0 ? hp / max : 1, "P0", +1));
        e.Metrics.Add(new ResultMetric("leaks", "Прорывы (среднее)", Num(r, "enemiesReachedGoal"), "0.0", -1));
        e.Metrics.Add(new ResultMetric("defenders_lost", "Потери защитников (среднее)", Num(r, "defendersLost"), "0.0", -1));
        e.Metrics.Add(new ResultMetric("seconds", "Длительность, с (среднее)", Num(r, "seconds"), "0"));
        e.Metrics.Add(new ResultMetric("enemies_killed", "Убито врагов", Num(r, "enemiesKilled"), "0", +1));
        e.Metrics.Add(new ResultMetric("damage_player", "Урон игрока", Side("damageDealt", BattleReport.PlayerSide), "0"));
        e.Metrics.Add(new ResultMetric("damage_enemy", "Урон врагов", Side("damageDealt", BattleReport.EnemySide), "0"));
        e.Metrics.Add(new ResultMetric("healing", "Лечение", Num(r, "healing"), "0"));
        e.Metrics.Add(new ResultMetric("route_blocked", "Маршрут перекрыт, с", Num(r, "routeBlockedSeconds"), "0.#", -1));
        return e;
    }

    private static ResultEntry? ReadCalibration(string file, string bas, DateTime when)
    {
        var rows = ReportCsv.Parse(System.IO.File.ReadAllText(file));
        if (rows.Count == 0 || rows[0].Length < 8 || rows[0][0] != "a" || rows[0][5] != "actual") return null;
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        var e = new ResultEntry { Base = bas, File = file, Kind = ResultKind.Calibration, Scenario = "оценка силы", When = when, Runs = rows.Skip(1).Select(x => (int)D(x[7])).DefaultIfEmpty(0).Max() };
        foreach (var x in rows.Skip(1).Where(x => x.Length >= 8))
            e.Pairs[$"{x[0]} против {x[1]}"] = (D(x[4]), D(x[5]));
        var errors = e.Pairs.Values.Select(p => Math.Abs(p.Actual - p.Predicted)).ToList();
        e.Metrics.Add(new ResultMetric("mean_abs_error", "Средняя ошибка прогноза", errors.DefaultIfEmpty(0).Average(), "P0", -1));
        e.Metrics.Add(new ResultMetric("bad_pairs", "Плохо предсказанных пар", errors.Count(x => x > Calibration.BadError), "0", -1));
        e.Metrics.Add(new ResultMetric("pairs", "Пар", e.Pairs.Count, "0"));
        return e;
    }

    /// <summary>B against A: every number they share, the better or the worse by its meaning, the notes on what they compare.</summary>
    public static ResultComparison Compare(ResultEntry a, ResultEntry b)
    {
        var c = new ResultComparison { A = a, B = b };
        if (a.Kind == ResultKind.Calibration != (b.Kind == ResultKind.Calibration))
            c.Notes.Add("⚠ Калибровка и прогоны сценария не сравниваются по числам.");
        else if (a.Scenario != b.Scenario)
            c.Notes.Add($"⚠ Разные сценарии («{a.Scenario}» и «{b.Scenario}») — сравнение условное.");
        else if (a.Kind != ResultKind.Calibration && a.Seed == b.Seed && a.Runs == b.Runs)
            c.Notes.Add("Одинаковые seed и число прогонов: разница — от изменений контента или правил, не от случая.");
        if (a.Kind != ResultKind.Calibration && (a.Runs < BatchReport.MinRunsForInterval || b.Runs < BatchReport.MinRunsForInterval))
            c.Notes.Add($"Меньше {BatchReport.MinRunsForInterval} прогонов: доля побед без доверительного интервала — выводы предварительные.");
        foreach (var ma in a.Metrics)
        {
            var mb = b.Metric(ma.Key);
            if (mb == null) continue;
            c.Rows.Add(new ComparisonRow(ma.Key, ma.Label, ma.Value, mb.Value, ma.Text, mb.Text, Verdict(ma, mb)));
        }
        foreach (var mb in b.Metrics.Where(m => a.Metric(m.Key) == null))
            c.Rows.Add(new ComparisonRow(mb.Key, mb.Label, null, mb.Value, "—", mb.Text, "только в B"));
        foreach (var ma in a.Metrics.Where(m => b.Metric(m.Key) == null))
            c.Rows.Add(new ComparisonRow(ma.Key, ma.Label, ma.Value, null, ma.Text, "—", "только в A"));
        // Calibrations: the regression by pair — where the estimate got better or worse.
        foreach (var (pair, pa) in a.Pairs)
        {
            if (!b.Pairs.TryGetValue(pair, out var pb)) continue;
            double ea = Math.Abs(pa.Actual - pa.Predicted), eb = Math.Abs(pb.Actual - pb.Predicted);
            string v = Math.Abs(eb - ea) < 0.05 ? "без изменений" : eb < ea ? "прогноз точнее" : "прогноз хуже";
            c.Rows.Add(new ComparisonRow("pair:" + pair, pair + ", ошибка", ea, eb, ea.ToString("P0", CultureInfo.InvariantCulture), eb.ToString("P0", CultureInfo.InvariantCulture), v));
        }
        return c;
    }

    private static string Verdict(ResultMetric a, ResultMetric b)
    {
        double d = b.Value - a.Value;
        if (Math.Abs(d) < 1e-9) return "без изменений";
        // The win rate: a change inside both intervals is the spread of the seeds, not a change of the balance.
        if (a.Low is { } al && a.High is { } ah && b.Low is { } bl && b.High is { } bh && bl <= ah && al <= bh)
            return "в пределах 95 % интервала";
        if (a.Better == 0) return d > 0 ? "больше" : "меньше";
        return d * a.Better > 0 ? "лучше" : "хуже";
    }

    /// <summary>A table of the results: a line per result, a column per number.</summary>
    public static string SummaryCsv(IEnumerable<ResultEntry> entries)
    {
        var list = entries.ToList();
        var keys = list.SelectMany(e => e.Metrics.Select(m => m.Key)).Distinct().ToList();
        var head = new object?[] { "when", "kind", "scenario", "seed", "runs", "file" }.Concat(keys).ToArray();
        return string.Join("\r\n", new[] { ReportCsv.Row(head) }.Concat(list.Select(e =>
            ReportCsv.Row(new object?[] { e.When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), e.Kind.ToString(), e.Scenario, e.Seed, e.Runs, Path.GetFileName(e.File) }
                .Concat(keys.Select(k => (object?)e.Metric(k)?.Value)).ToArray())))) + "\r\n";
    }
}
