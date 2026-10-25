using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage C — telemetry (docs/DECISIONS_5.md §86): units and waves in the report, N = 100, CSV/JSON (T23).</summary>
public class Part5TelemetryTests
{
    /// <summary>Two defenders at a base, a wave of wolves on a road.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Scene()
    {
        var db = TestUtil.Minimal();
        db.Rules.Time.SimVersion = 2;
        db.Add(new UnitDef
        {
            Id = "medic", Name = "Медик", Faction = "player", BasicAttack = "hit", AiProfile = "aggressive",
            Stats = { [Stats.HpMax] = 40, [Stats.ApMax] = 10, [Stats.ApRegen] = 3, [Stats.MoveSpeed] = 2, [Stats.BaseDamage] = 2 },
        });
        db.Add(new UnitDef
        {
            Id = "wolf", Name = "Волк", Faction = "enemy", BasicAttack = "hit", AiProfile = "aggressive",
            Stats = { [Stats.HpMax] = 25, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 3, [Stats.BaseDamage] = 4 },
        });
        var map = TestUtil.Field(14, 7);
        map.Id = "p5tel";
        db.Add(map);
        var sc = new ScenarioDef { Id = "p5tel", Name = "Телеметрия", Map = map.Id, Seed = 1, Mode = BattleMode.TowerDefense };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 3)));
        var b = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(13, 3));
        b.Health!.MaxHp = 60;
        sc.Objects.Add(b);
        sc.BaseDefeat.Mode = BaseDefeatMode.AnyBase;
        sc.Routes.Add(RouteGraph.FromCells("r1", HexCoord.Line(new HexCoord(0, 3), new HexCoord(12, 3)).ToList(), "spawn_1", b.Id, "Дорога"));
        sc.Units.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(10, 3) });
        sc.Units.Add(new ScenarioUnitDef { Unit = "medic", C = new HexCoord(11, 4) });
        sc.Waves.Add(new WaveDef
        {
            Name = "Стая", Start = WaveStart.Time, Time = 0,
            Groups = { new SpawnGroupDef { Id = "g", Unit = "wolf", Count = 6, Interval = 1, SpawnBase = "spawn_1", Route = "r1", RouteSelect = GroupRouteSelect.Random } },
        });
        sc.Victory = new() { new ConditionDef { Type = ConditionType.WavesCleared, Count = 1 } };
        sc.Defeat = new() { new ConditionDef { Type = ConditionType.AllPlayerUnitsDead } };
        db.Add(sc);
        return (db, sc);
    }

    [Fact]
    public void TheReport_HasEveryUnitAndWave_WithTheirMetrics()
    {
        var (db, sc) = Scene();
        var r = AutoBattle.Run(db, sc, 3, new AutoBattleOptions { MaxSeconds = 120 });
        Assert.NotEqual("не завершён", r.Outcome);
        Assert.Equal(8, r.UnitStats.Count); // two defenders and six wolves
        var wolves = r.UnitStats.Where(u => u.Template == "wolf").ToList();
        Assert.All(wolves, w => Assert.True(w.PathHexes > 0 && w.Wave == 0 && w.Group == "g" && w.Side == BattleReport.EnemySide));
        Assert.Equal(r.DamageDealt.Values.Sum(), r.UnitStats.Sum(u => u.DamageDealt), 0);
        Assert.Contains(r.UnitStats, u => u.Kills > 0 && u.Side == BattleReport.PlayerSide);
        Assert.All(r.UnitStats.Where(u => u.DeathTick >= 0), u => Assert.True(u.DeathTick <= r.Ticks));
        Assert.Contains(r.UnitStats, u => u.Casts > 0);
        var w0 = r.WaveStats[0];
        Assert.Equal(6, w0.Planned);
        Assert.Equal(6, w0.Spawned);
        Assert.Equal(0, w0.Failed);
        Assert.InRange(w0.MaxAlive, 1, 6);
        Assert.Equal(5, r.SchemaVersion);
        Assert.Equal(2, r.SimVersion);
        Assert.True(r.TickMsMean > 0);
        Assert.Contains("Лучшие юниты", r.ToText(db));
        // The measured times stay out of the JSON: the same seed gives the same JSON.
        Assert.Equal(r.ToJson(), AutoBattle.Run(db, sc, 3, new AutoBattleOptions { MaxSeconds = 120 }).ToJson());
    }

    [Fact]
    public void Healing_CountsTheEffectivePartAndTheOverheal()
    {
        var (db, sc) = Scene();
        var sim = ScenarioSystem.Create(db, sc, 1);
        var report = BattleReport.Attach(sim, sc.Id, 1);
        var medic = sim.Units.First(u => u.Def.Id == "medic");
        var soldier = sim.Units.First(u => u.Def.Id == "soldier");
        soldier.Hp = soldier.HpMax - 10;
        sim.Combat.Heal(medic, soldier, 25, "тест", overhealShield: false);
        sim.Step();
        report.Finish();
        var m = report.UnitStats.Single(u => u.Id == medic.Id);
        Assert.Equal(10, m.Healing, 3);
        Assert.Equal(15, m.Overheal, 3);
    }

    [Fact]
    public void Wilson_IsTheKnownInterval()
    {
        var (lo, hi) = BatchReport.Wilson(50, 100);
        Assert.Equal(0.4038, lo, 3);
        Assert.Equal(0.5962, hi, 3);
        var (l0, h0) = BatchReport.Wilson(0, 10);
        Assert.Equal(0, l0, 6);
        Assert.Equal(0.2775, h0, 3);
        var (l1, h1) = BatchReport.Wilson(100, 100);
        Assert.Equal(0.963, l1, 3);
        Assert.Equal(1, h1, 6);
    }

    [Fact]
    public void T23_Run100_TheSummaryIsValid_AndTheCsvAndJsonOpenAfterExport()
    {
        var (db, sc) = Scene();
        var (batch, reports) = AutoBattle.Batch(db, sc, 100, 1000, new AutoBattleOptions { MaxSeconds = 120 });
        Assert.Equal(100, batch.RunsCount);
        Assert.Equal(100, reports.Count);
        Assert.InRange(batch.WinRate, 0, 1);
        Assert.NotNull(batch.WinRateLow);
        Assert.InRange(batch.WinRate, batch.WinRateLow!.Value, batch.WinRateHigh!.Value);
        Assert.Equal(100, batch.LeakDistribution.Values.Sum());
        Assert.Equal(reports.Sum(r => r.EnemiesReachedGoal) / 100.0, batch.MeanLeaks, 6);
        Assert.Equal(reports.Sum(r => BatchReport.PathErrorKinds.Sum(k => r.AiErrors.GetValueOrDefault(k))), batch.PathErrors);
        Assert.InRange(batch.MedianSeconds, batch.MinSeconds, batch.MaxSeconds);
        Assert.InRange(batch.WorstRun, 0, 99);
        Assert.Equal(batch.Runs.Min(r => r.BaseHealth), batch.Runs[batch.WorstRun].BaseHealth);
        var text = batch.ToText();
        Assert.Contains("95 % интервал", text);
        Assert.Contains("Прорывы:", text);
        Assert.Contains("Ошибки пути", text);
        Assert.Contains("Худший случай", text);

        var dir = Path.Combine(Path.GetTempPath(), "hex_t23_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = AutoBattle.Save(dir, batch, reports, "t23");
            using (var doc = JsonDocument.Parse(File.ReadAllText(path + ".json")))
            {
                Assert.Equal(100, doc.RootElement.GetProperty("runsCount").GetInt32());
                Assert.Equal(100, doc.RootElement.GetProperty("runs").GetArrayLength());
                Assert.True(doc.RootElement.GetProperty("winRateLow").GetDouble() <= doc.RootElement.GetProperty("winRate").GetDouble());
            }
            var runs = ReportCsv.Parse(File.ReadAllText(path + "_runs.csv"));
            Assert.Equal(101, runs.Count);
            Assert.All(runs, row => Assert.Equal(runs[0].Length, row.Length));
            int outcome = Array.IndexOf(runs[0], "outcome"), leaks = Array.IndexOf(runs[0], "leaks");
            Assert.Equal(batch.WinRate, runs.Skip(1).Count(r => r[outcome] == "победа") / 100.0, 6);
            Assert.Equal(reports.Sum(r => r.EnemiesReachedGoal), runs.Skip(1).Sum(r => int.Parse(r[leaks])));
            var summary = ReportCsv.Parse(File.ReadAllText(path + "_summary.csv")).ToDictionary(r => r[0], r => r[1]);
            Assert.Equal("100", summary["runs"]);
            Assert.Equal(batch.WinRate, double.Parse(summary["win_rate"], System.Globalization.CultureInfo.InvariantCulture), 4);
            var waves = ReportCsv.Parse(File.ReadAllText(path + "_waves.csv"));
            Assert.Equal(101, waves.Count);
            Assert.StartsWith("﻿", File.ReadAllText(path + "_runs.csv", System.Text.Encoding.Latin1).Length > 0 ? "﻿" : "");
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path + "_runs.csv").Take(3).ToArray());
            // One report: its units.
            var one = AutoBattle.Save(dir, reports[0], db, "one");
            var units = ReportCsv.Parse(File.ReadAllText(one + "_units.csv"));
            Assert.Equal(reports[0].UnitStats.Count + 1, units.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Csv_QuotesCommasQuotesAndLineBreaks()
    {
        string csv = ReportCsv.Row("a,b", "он сказал \"да\"", "две\nстроки", 1.5, null) + "\r\n" + ReportCsv.Row("x", "y", "z", 2, "") + "\r\n";
        var rows = ReportCsv.Parse(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a,b", "он сказал \"да\"", "две\nстроки", "1.5", "" }, rows[0]);
        Assert.Equal(new[] { "x", "y", "z", "2", "" }, rows[1]);
    }
}
