using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage F — the screen «Результаты» (ТЗ 25.7; docs/DECISIONS_5.md §89).</summary>
public class Part5ResultsTests
{
    private static string Folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ht_results_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SavedBatchesAndBattles_AreReadBack_ComparedWithTheInterval_AndExported()
    {
        var db = TestUtil.Shipped();
        var sc = db.All<ScenarioDef>().First(s => s.Waves.Count > 0);
        var dir = Folder();
        try
        {
            var opts = new AutoBattleOptions { MaxSeconds = 120 };
            var (b1, r1) = AutoBattle.Batch(db, sc, 10, 1, opts, 2);
            var (b2, r2) = AutoBattle.Batch(db, sc, 10, 1, opts, 2);
            AutoBattle.Save(dir, b1, r1, "a");
            AutoBattle.Save(dir, b2, r2, "b");
            AutoBattle.Save(dir, r1[0], db, "one");
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "other.json"), "{\"x\": 1}");
            var (entries, problems) = RunResults.Load(dir);
            Assert.Equal(3, entries.Count);
            Assert.Single(problems);
            Assert.Contains("broken.json", problems[0]);
            var batches = entries.Where(e => e.Kind == ResultKind.Batch).OrderBy(e => e.File).ToList();
            Assert.Equal(2, batches.Count);
            var a = batches[0];
            Assert.Equal(sc.DisplayName, a.Scenario);
            Assert.Equal(10, a.Runs);
            Assert.Equal(b1.WinRate, a.Metric("win_rate")!.Value, 9);
            Assert.Equal(b1.WinRateLow, a.Metric("win_rate")!.Low);
            Assert.Equal(b1.MeanWaves, a.Metric("waves")!.Value, 9);
            Assert.Contains(a.Files(), f => f.EndsWith("_runs.csv"));
            Assert.Contains("Доля побед", a.Text() + string.Join(",", a.Metrics.Select(m => m.Label)));
            // The same seeds and content: the same numbers, said so; nothing called better or worse.
            var cmp = RunResults.Compare(a, batches[1]);
            Assert.Contains(cmp.Notes, n => n.Contains("Одинаковые seed"));
            Assert.All(cmp.Rows, r => Assert.Equal("без изменений", r.Verdict));
            // A single battle against a batch: the shared numbers, and the ones of one side only.
            var one = entries.Single(e => e.Kind == ResultKind.Battle);
            Assert.Equal(1, one.Runs);
            var mixed = RunResults.Compare(a, one);
            Assert.Contains(mixed.Rows, r => r.Key == "win_rate" && r.A != null && r.B != null);
            Assert.Contains(mixed.Rows, r => r.Key == "damage_player" && r.A == null && r.Verdict == "только в B");
            Assert.Contains(mixed.Notes, n => n.Contains("без доверительного интервала"));
            // Export: the comparison and the summary as tables.
            var csv = ReportCsv.Parse(cmp.ToCsv());
            Assert.Equal(cmp.Rows.Count + 1, csv.Count);
            var sum = ReportCsv.Parse(RunResults.SummaryCsv(entries));
            Assert.Equal(4, sum.Count);
            Assert.Contains("win_rate", sum[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Verdicts_ByMeaning_AndInsideTheInterval()
    {
        var a = new ResultEntry { Kind = ResultKind.Batch, Scenario = "s", Runs = 100, Seed = 1 };
        var b = new ResultEntry { Kind = ResultKind.Batch, Scenario = "s", Runs = 100, Seed = 1000 };
        a.Metrics.Add(new ResultMetric("win_rate", "Доля побед", 0.50, "P0", +1, 0.40, 0.60));
        b.Metrics.Add(new ResultMetric("win_rate", "Доля побед", 0.55, "P0", +1, 0.45, 0.65));
        a.Metrics.Add(new ResultMetric("leaks", "Прорывы", 2, "0.0", -1));
        b.Metrics.Add(new ResultMetric("leaks", "Прорывы", 3, "0.0", -1));
        a.Metrics.Add(new ResultMetric("seconds", "Длительность", 100, "0"));
        b.Metrics.Add(new ResultMetric("seconds", "Длительность", 90, "0"));
        var c = RunResults.Compare(a, b);
        Assert.Equal("в пределах 95 % интервала", c.Rows.Single(r => r.Key == "win_rate").Verdict);
        Assert.Equal("хуже", c.Rows.Single(r => r.Key == "leaks").Verdict);
        Assert.Equal("меньше", c.Rows.Single(r => r.Key == "seconds").Verdict);
        b.Metrics[0] = new ResultMetric("win_rate", "Доля побед", 0.80, "P0", +1, 0.71, 0.87);
        Assert.Equal("лучше", RunResults.Compare(a, b).Rows.Single(r => r.Key == "win_rate").Verdict);
        Assert.Contains("50 %", c.ToText().Replace("50%", "50 %"));
    }

    [Fact]
    public void Calibrations_AreComparedByPair_TheRegressionReport()
    {
        var dir = Folder();
        try
        {
            var r1 = new CalibrationReport { Runs = 4 };
            r1.Rows.Add(new CalibrationRow { A = "воин", B = "стая", Predicted = 0.3, Actual = 0.9, Runs = 4 });
            r1.Rows.Add(new CalibrationRow { A = "воин", B = "лучник", Predicted = 0.5, Actual = 0.5, Runs = 4 });
            var r2 = new CalibrationReport { Runs = 4 };
            r2.Rows.Add(new CalibrationRow { A = "воин", B = "стая", Predicted = 0.8, Actual = 0.9, Runs = 4 });
            r2.Rows.Add(new CalibrationRow { A = "воин", B = "лучник", Predicted = 0.2, Actual = 0.5, Runs = 4 });
            ReportCsv.Write(Path.Combine(dir, "calibration_1.csv"), r1.ToCsv());
            ReportCsv.Write(Path.Combine(dir, "calibration_2.csv"), r2.ToCsv());
            var (entries, problems) = RunResults.Load(dir);
            Assert.Empty(problems);
            var a = entries.Single(e => e.File.EndsWith("calibration_1.csv"));
            var b = entries.Single(e => e.File.EndsWith("calibration_2.csv"));
            Assert.Equal(ResultKind.Calibration, a.Kind);
            Assert.Equal(2, a.Pairs.Count);
            Assert.Equal(0.3, a.Metric("mean_abs_error")!.Value, 9);
            Assert.Equal(1, a.Metric("bad_pairs")!.Value);
            var c = RunResults.Compare(a, b);
            Assert.Equal("прогноз точнее", c.Rows.Single(r => r.Key == "pair:воин против стая").Verdict);
            Assert.Equal("прогноз хуже", c.Rows.Single(r => r.Key == "pair:воин против лучник").Verdict);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
