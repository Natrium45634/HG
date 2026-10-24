using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 4 stage 7 (docs/DECISIONS_4.md §69–72): undo and redo with one entry per operation, the log, mass changes,
/// overrides of a group turned into a template, what a change touches, the wave report, the performance budget,
/// «Проверить сценарий» and the comparison of two versions with trial battles.
/// </summary>
public class ProjectToolsTests
{
    [Fact]
    public void History_UndoRedo_OneEntryPerOperation_KeepsOrderAndRules()
    {
        var db = TestUtil.Shipped();
        var h = new EditHistory(db);
        var wolf = db.Unit("wolf")!;
        double hp = wolf.Stats[Stats.HpMax];

        // An edit of one object (the form says which).
        wolf.Stats[Stats.HpMax] = hp * 2;
        h.Touch(wolf);
        var e1 = h.Commit();
        Assert.NotNull(e1);
        Assert.Single(e1!.Changes);
        Assert.Contains("Волк", e1.Describe());
        Assert.Null(h.Commit()); // nothing new

        // A deletion (nothing said — the whole content is compared) keeps the place in the list on undo.
        int index = db.Table(DefCategory.Unit).IndexOf("goblin");
        db.Table(DefCategory.Unit).Remove("goblin");
        h.TouchAll();
        var e2 = h.Commit("Удаление");
        Assert.Equal("goblin", e2!.Changes.Single().Id);
        Assert.Null(e2.Changes[0].After);

        // A mass change and a change of the rules — one entry.
        MassEdit.SetStat(db, db.All<UnitDef>().Where(u => u.Faction == "enemy"), Stats.MoveSpeed, MassStatMode.Multiply, 1.1);
        db.Rules.Performance.MaxAliveUnits = 77;
        h.TouchAll();
        var e3 = h.Commit("Массовое изменение");
        Assert.True(e3!.Changes.Count > 5);
        Assert.NotNull(e3.RulesBefore);

        Assert.Same(e3, h.Undo());
        Assert.Equal(150, db.Rules.Performance.MaxAliveUnits);
        Assert.Equal(2.4, db.Unit("wolf_leader")!.Stats[Stats.MoveSpeed], 6);
        Assert.Same(e2, h.Undo());
        Assert.Equal(index, db.Table(DefCategory.Unit).IndexOf("goblin"));
        Assert.Same(e1, h.Undo());
        Assert.Equal(hp, db.Unit("wolf")!.Stats[Stats.HpMax], 6);
        Assert.False(h.CanUndo);

        // Redo puts the «after» back; a new commit clears the redo.
        h.Redo();
        Assert.Equal(hp * 2, db.Unit("wolf")!.Stats[Stats.HpMax], 6);
        Assert.True(h.CanRedo);
        db.Unit("orc_brute")!.Rank = UnitRank.Boss;
        h.Touch(db.Unit("orc_brute")!);
        h.Commit();
        Assert.False(h.CanRedo);

        // «Вернуть к этой точке»: everything after the entry is undone.
        Assert.Equal(1, h.UndoTo(h.UndoStack[0]));
        Assert.Equal(UnitRank.Elite, db.Unit("orc_brute")!.Rank);
        Assert.Equal(hp * 2, db.Unit("wolf")!.Stats[Stats.HpMax], 6);
    }

    [Fact]
    public void History_CreationIsUndoneAndRedone_PendingEditsAreCommittedByUndo()
    {
        var db = TestUtil.Shipped();
        var h = new EditHistory(db);
        db.Add(new UnitDef { Id = "new_one", Name = "Новый", Faction = "enemy", BasicAttack = "slash" });
        h.TouchAll();
        // Not committed yet: undo commits it first and undoes it.
        Assert.NotNull(h.Undo());
        Assert.Null(db.Unit("new_one"));
        h.Redo();
        Assert.Equal("Новый", db.Unit("new_one")!.Name);
    }

    [Fact]
    public void MassEdit_Stats_Categories_Rank_AndFitAll()
    {
        var db = TestUtil.Shipped();
        var units = new[] { db.Unit("goblin")!, db.Unit("wolf")!, db.Unit("slime")! };
        Assert.Equal(3, MassEdit.SetStat(db, units, Stats.HpMax, MassStatMode.Add, 10));
        Assert.Equal(3, MassEdit.SetRank(units, UnitRank.Elite));
        Assert.Equal(0, MassEdit.SetRank(units, UnitRank.Elite));
        Assert.True(MassEdit.SetCategory(units, "beasts", false) >= 2);
        var fits = MassEdit.FitAll(db, units, 6);
        Assert.Equal(3, fits.Count);
        // The goblin is the reference of level 1: it is not fitted.
        Assert.Contains("эталон", fits[0].Message);
        Assert.Equal(1, PowerModel.Evaluate(db, db.Unit("goblin")!).Level, 6);
        fits = MassEdit.FitAll(db, new[] { db.Unit("goblin_archer")!, db.Unit("wolf")!, db.Unit("orc_brute")! }, 6);
        foreach (var f in fits)
        {
            Assert.Same(f.Unit, db.Unit(f.Unit.Id));
            double lv = PowerModel.Evaluate(db, db.Unit(f.Unit.Id)!).Level;
            // Reached, or the limits of the profile stop it — and the message says what to allow.
            Assert.True(f.Reached ? Math.Abs(lv - 6) <= 0.6 : f.Message.Contains("недостижим") && lv > f.LevelBefore, f.ToString());
        }
        Assert.Contains(fits, f => f.Reached);
    }

    [Fact]
    public void GroupOverrides_BecomeATemplate_TheGroupSwitchesToIt_TheBattleIsTheSame()
    {
        var db = TestUtil.Shipped();
        var sc = db.Get<ScenarioDef>("td_pass")!;
        var g = sc.Waves[1].Groups[0];
        g.StatMods.Add(new StatModDef { Stat = Stats.MoveSpeed, Mul = 0.1 });
        Assert.True(GroupOverrides.Has(g));
        Assert.Contains(GroupOverrides.Describe(db, g), d => d.Contains("+10%"));
        double before = SpeedOfFirstWolf(db, sc);

        var t = GroupOverrides.ToTemplate(db, g);
        Assert.Equal(t.Id, g.Unit);
        Assert.False(GroupOverrides.Has(g));
        Assert.Equal(db.Unit("wolf")!.Stats[Stats.MoveSpeed] * 1.1, t.Stats[Stats.MoveSpeed], 6);
        Assert.Equal(before, SpeedOfFirstWolf(db, sc), 6);
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error);
    }

    private static double SpeedOfFirstWolf(ContentDatabase db, ScenarioDef sc)
    {
        var (sim, _, _) = AutoBattle.Prepare(db, sc, 3);
        TestUtil.RunUntil(sim, () => sim.Units.Any(u => u.Wave == 1), 60);
        return sim.Units.First(u => u.Wave == 1).Stat(Stats.MoveSpeed);
    }

    [Fact]
    public void ChangeImpact_Category_Route_Template()
    {
        var db = TestUtil.Shipped();
        var wolfCat = db.Unit("wolf")!.MainCategory;
        var (children, templates) = ChangeImpact.OfCategory(db, wolfCat);
        Assert.Contains(children, c => c.Id == db.Unit("wolf_leader")!.MainCategory);
        Assert.Contains(templates, u => u.Id == "wolf");
        Assert.Contains(templates, u => u.Id == "wolf_leader");
        Assert.DoesNotContain(templates, u => u.Id == "goblin");

        var sc = db.Get<ScenarioDef>("td_pass")!;
        Assert.Equal(3, ChangeImpact.OfRoute(sc, "route_1").Count);
        var uses = ChangeImpact.OfTemplate(db, "wolf");
        Assert.Contains(uses, p => p.Scenario.Id == "td_pass" && p.Wave == 1);
    }

    [Fact]
    public void WaveReport_PlanCostsLevelsDuration_JumpsAndRuns()
    {
        var db = TestUtil.Shipped();
        var sc = ContentJson.Clone(db.Get<ScenarioDef>("td_pass")!);
        var plan = WaveReports.Build(db, sc);
        Assert.Equal(3, plan.Rows.Count);
        Assert.Equal(new[] { 4, 4, 2 }, plan.Rows.Select(r => r.Count));
        Assert.All(plan.Rows, r => Assert.True(r.Cost > 0 && r.ExpectedSeconds > 0 && r.MaxLevel >= r.AvgLevel && r.Runs == 0));
        Assert.True(plan.Rows[2].MaxLevel > plan.Rows[0].MaxLevel);

        // A wave six times larger is a jump.
        sc.Waves[1].Groups[0].Count = 24;
        var jump = WaveReports.Build(db, sc);
        Assert.True(jump.Rows[1].Jump && jump.Rows[1].Relative >= 1.5);
        Assert.Contains("СКАЧОК", jump.ToText());

        // With runs: what happened per wave.
        var orig = db.Get<ScenarioDef>("td_pass")!;
        var runs = Enumerable.Range(0, 2).Select(i => AutoBattle.Run(db, orig, (ulong)(5 + i), new AutoBattleOptions { MaxSeconds = 150 })).ToList();
        Assert.All(runs, r => Assert.Equal(4, r.WaveStats[0].Spawned));
        var withRuns = WaveReports.Build(db, orig, runs);
        Assert.Equal(2, withRuns.Rows[0].Runs);
        Assert.NotNull(withRuns.Rows[0].BaseDamage);
        Assert.Contains("в прогонах", withRuns.ToText());
    }

    [Fact]
    public void PerformanceBudget_EstimatesAndWarns_TheProfileMeasures()
    {
        var db = TestUtil.Shipped();
        var sc = ContentJson.Clone(db.Get<ScenarioDef>("td_pass")!);
        var ok = PerformanceBudget.Estimate(db, sc);
        Assert.False(ok.OverBudget, ok.ToText());
        Assert.Equal(10, ok.Lines.First(l => l.Key == "created").Value);
        Assert.True(ok.Lines.First(l => l.Key == "alive").Value >= 3 + 1);

        sc.Waves[0].Groups[0].Count = 400;
        sc.Waves[0].Groups[0].Interval = 0.05;
        db.Rules.Performance.MaxAliveUnits = 100;
        var over = PerformanceBudget.Estimate(db, sc);
        Assert.True(over.OverBudget);
        Assert.True(over.Lines.First(l => l.Key == "alive").Over);
        Assert.NotEmpty(over.Expensive);
        Assert.Contains("превышен", over.ToText());

        var prof = PerformanceBudget.Profile(db, db.Get<ScenarioDef>("td_pass")!, 20);
        Assert.NotEmpty(prof.Profile);
        Assert.InRange(prof.Profile.Sum(p => p.Share), 0.99, 1.01);
        Assert.Contains(prof.Profile, p => p.Section == "ИИ");
    }

    [Fact]
    public void ScenarioCheck_FindsEveryKindOfProblem_WithAPlaceToGo()
    {
        var db = TestUtil.Shipped();
        var sc = db.Get<ScenarioDef>("td_pass")!;
        Assert.DoesNotContain(ScenarioChecks.Run(db, sc), p => p.Severity == IssueSeverity.Error);

        var bad = ContentJson.Clone(sc);
        bad.Id = "td_pass_bad";
        bad.Waves[2].Groups[0].Unit = "no_such";
        bad.Waves[1].Groups[0].Interval = 0;
        bad.Waves[1].Groups[0].Count = 40;
        bad.Waves[2].Groups[0].Repeats = 2;
        bad.Waves[2].Groups[0].RepeatEvery = 1;
        db.Add(bad);
        db.Unit("wolf")!.Stats[Stats.MoveSpeed] = 30; // «скорость» — a balance warning of a template
        var problems = ScenarioChecks.Run(db, bad);
        Assert.Contains(problems, p => p.Severity == IssueSeverity.Error && p.Message.Contains("no_such"));
        Assert.Contains(problems, p => p.Message.Contains("интервал 0") && p.Object == "wave:1");
        Assert.Contains(problems, p => p.Message.Contains("выходы наложатся"));
        Assert.Contains(problems, p => p.Message.StartsWith("Шаблон «Волк»") && p.Object == "unit:wolf");
        Assert.Contains(problems, p => p.Message.StartsWith("Скачок сложности"));
        Assert.Contains(ScenarioChecks.UsedTemplates(db, bad), u => u.Id == "knight");
    }

    [Fact]
    public void VersionCompare_ShowsEveryDifference_AndTheTrialBattlesDecide()
    {
        var db = TestUtil.Shipped();
        var a = db.Unit("orc_brute")!;
        var b = ContentJson.Clone(a);
        b.Stats[Stats.HpMax] = a.Stats[Stats.HpMax] * 2;
        b.Passives.Add(db.All<PassiveDef>().First().Id);
        var c = VersionCompare.Compare(db, a, b, "v1", "v2", duelRuns: 3);
        Assert.Contains(c.Lines, l => l.Section == "Характеристики" && l.Changed && l.Label.Contains("здоров", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(c.Lines, l => l.Label == "Уровень" && l.Changed);
        Assert.Contains(c.Lines, l => l.Section == "Пассивы" && l.Changed);
        Assert.Contains(c.Lines, l => l.Section == "Сопротивления");
        Assert.Contains(c.Lines, l => l.Label == "Стоимость угрозы");
        Assert.NotNull(c.DuelA);
        Assert.True(c.DuelB!.Wins >= c.DuelA!.Wins && (c.DuelB.Wins > c.DuelA.Wins || c.DuelB.HpLeft >= c.DuelA.HpLeft - 0.05), c.ToText());
        Assert.NotEqual("", c.Verdict);
        // Reproducible: the same seeds give the same battles.
        var again = VersionCompare.Duel(db, a, c.DuelA.Opponents, 3);
        Assert.Equal(c.DuelA.Wins, again.Wins);
        Assert.Equal(c.DuelA.Seconds, again.Seconds);
    }

    [Fact]
    public void ShippedRules_HavePerformanceThresholds()
    {
        var db = TestUtil.Shipped();
        var p = db.Rules.Performance;
        Assert.True(p.MaxAliveUnits > 0 && p.MaxGroupsPerWave > 0 && p.MaxRouteNodes > 0 && p.MaxRouteEdges > 0 && p.MaxPassiveRules > 0 && p.MaxEffects > 0);
        Assert.True(p.MaxAiDecisionsPerSecond > 0 && p.WaveJump > 1);
    }
}
