using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;
using Xunit.Abstractions;

namespace HexTactics.Tests;

/// <summary>
/// Part 4, stage 7 (TZ §21, §22 этап 7): the whole workflow of the TZ example — a map with two player bases, two spawn
/// bases, a main and a bypass route joined into a common section, wolves, a pack leader and slime mimics in four
/// waves (the last after a checkpoint), a TD battle with the preset placement, a report, a change and a batch, the
/// fit of the leader to a higher level as a new template; then a large project (hundreds of templates, dozens of
/// scenarios and categories) stays manageable; and the content survives a broken file.
/// </summary>
public class Part4IntegrationTests
{
    private readonly ITestOutputHelper _out;

    public Part4IntegrationTests(ITestOutputHelper output)
    {
        _out = output;
    }

#if DEBUG
    private static readonly double Slack = 2.5 * TestUtil.MachineSlack;
#else
    private static readonly double Slack = 1.0 * TestUtil.MachineSlack;
#endif

    /// <summary>TZ §21.1–21.3 on the shipped content: the map, the bases, the routes, the waves.</summary>
    private static ScenarioDef BuildTz21(ContentDatabase db)
    {
        var map = TestUtil.Field(20, 9);
        map.Id = "tz21_map";
        map.Name = "ТЗ §21";
        // A ridge between the lanes.
        foreach (var c in map.Cells.Where(c => c.C.R == 5 && c.C.Q is >= 3 and <= 12)) c.T = "wall";
        db.Add(map);

        var sc = ContentJson.Clone(db.Get<ScenarioDef>("td_pass_defense")!);
        sc.Id = "tz21";
        sc.Name = "Сквозной процесс ТЗ §21";
        sc.Map = map.Id;
        sc.Objects.Clear();
        sc.Routes.Clear();
        sc.Waves.Clear();
        sc.Triggers.Clear();
        sc.Units.Clear();
        sc.Placement.Preset.Clear();
        sc.Placement.ZoneCells.Clear();

        // 21.1: the main and the auxiliary base, two spawn bases, defensive positions, the main and the bypass route.
        var main = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(18, 3));
        main.Name = "Главная база";
        main.Footprint = new() { new HexCoord(0, 1) };
        sc.Objects.Add(main);
        var aux = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(18, 7));
        aux.Name = "Вспомогательная база";
        sc.Objects.Add(aux);
        var spawnA = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 3));
        sc.Objects.Add(spawnA);
        var spawnB = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 7));
        sc.Objects.Add(spawnB);
        sc.BaseDefeat = new BaseDefeatDef { Mode = BaseDefeatMode.MainBase, MainBase = main.Id };
        sc.Placement.ZoneObjects = new() { main.Id, aux.Id };
        sc.Placement.Preset.AddRange(new[]
        {
            new ScenarioUnitDef { Unit = "knight", C = new HexCoord(15, 2), Facing = HexDirection.W },
            new ScenarioUnitDef { Unit = "archer", C = new HexCoord(16, 2), Facing = HexDirection.W },
            new ScenarioUnitDef { Unit = "mage", C = new HexCoord(16, 6), Facing = HexDirection.W },
            new ScenarioUnitDef { Unit = "priestess", C = new HexCoord(17, 2), Facing = HexDirection.W },
        });
        var mainRoute = RouteGraph.FromCells("route_main", Enumerable.Range(1, 17).Select(q => new HexCoord(q, 3)).ToList(), spawnA.Id, main.Id, "Основной");
        // The bypass joins the main route at (15,3): the last hexes are the common section.
        var bypassCells = Enumerable.Range(1, 12).Select(q => new HexCoord(q, 7))
            .Concat(new[] { new HexCoord(13, 6), new HexCoord(14, 5), new HexCoord(15, 4), new HexCoord(15, 3), new HexCoord(16, 3), new HexCoord(17, 3) }).ToList();
        var bypass = RouteGraph.FromCells("route_bypass", bypassCells, spawnB.Id, main.Id, "Обходной");
        // A checkpoint early on the bypass: when an enemy passes it, the flag starts the fourth wave.
        var edge = bypass.Edges[0];
        var cp = RouteGraph.Split(bypass, edge, edge.Cells.IndexOf(new HexCoord(3, 7)));
        cp.Kind = RouteNodeKind.Checkpoint;
        cp.Event = "cp_bypass";
        cp.Name = "Контрольная точка обхода";
        sc.Routes.Add(mainRoute);
        sc.Routes.Add(bypass);

        // 21.3: four waves.
        SpawnGroupDef G(string id, string unit, int count, double interval, string spawn, string route, double delay = 0, string ai = "") =>
            new() { Id = id, Unit = unit, Count = count, Interval = interval, Delay = delay, SpawnBase = spawn, Route = route, Target = main.Id, AiProfile = ai };
        sc.Waves.Add(new WaveDef { Id = "w1", Name = "Волки", Start = WaveStart.Time, Time = 2, Groups = { G("wolves", "wolf", 12, 0.8, spawnA.Id, mainRoute.Id, ai: "route_raider") } });
        sc.Waves.Add(new WaveDef { Id = "w2", Name = "Разведчики", Start = WaveStart.Time, Time = 12, Groups = { G("scouts", "wolf_scout", 6, 1.2, spawnB.Id, bypass.Id, ai: "route_raider") } });
        sc.Waves.Add(new WaveDef
        {
            Id = "w3", Name = "Стая с вожаком", Start = WaveStart.Time, Time = 30,
            Groups = { G("pack", "wolf", 8, 0.8, spawnA.Id, mainRoute.Id, ai: "route_raider"), G("leader", "wolf_leader", 1, 1, spawnA.Id, mainRoute.Id, delay: 3, ai: "ai_leader") },
        });
        sc.Waves.Add(new WaveDef
        {
            Id = "w4", Name = "Мимики", Start = WaveStart.OnCondition, StartCondition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "cp_bypass" },
            Groups = { G("mimics", "slime_mimic", 3, 1, spawnB.Id, bypass.Id) },
        });
        db.Add(sc);
        return sc;
    }

    [Fact]
    public void Workflow_TZ21_MapEnemiesWavesTestChangeBatchAndBalance()
    {
        var db = TestUtil.Shipped();
        var history = new EditHistory(db);
        string wolfBefore = ContentJson.Serialize(db.Unit("wolf")!);
        var sc = BuildTz21(db);
        history.TouchAll();
        history.Commit("ТЗ §21: карта, базы, маршруты, волны");

        // The scene is consistent: no errors in the whole check; both routes reach the main base through the common section.
        var problems = ScenarioChecks.Run(db, sc);
        Assert.DoesNotContain(problems, p => p.Severity == IssueSeverity.Error);
        var common = sc.Routes[0].MainPath().Intersect(sc.Routes[1].MainPath()).ToList();
        Assert.Contains(new HexCoord(16, 3), common);
        Assert.Equal(2, sc.Objects.Count(o => o.Type == ScenarioObjectType.PlayerBase));

        // 21.2: the templates by categories, the leader's capped pack bonus, the mimic's inherited prohibition — levels by themselves.
        Assert.Contains("Волк", string.Join(" → ", CategoryTree.Chain(db, db.Get<OriginDef>(db.Unit("wolf")!.MainCategory)!).Select(c => c.DisplayName)));
        var leader = CategoryTree.Resolve(db, db.Unit("wolf_leader")!);
        Assert.Contains(leader.Of(CategoryRuleKind.Passive), r => db.Get<PassiveDef>(r.Rule.Passive)?.Modifiers.Any(m => m.PerEach != null && (m.PerEach.MaxValue > 0 || m.PerEach.MaxCount > 0)) == true);
        Assert.Contains(CategoryTree.Resolve(db, db.Unit("slime_mimic")!).Rules, r => r.Active && r.Rule.Kind == CategoryRuleKind.Forbid);
        foreach (var id in new[] { "wolf", "wolf_scout", "wolf_leader", "slime_mimic" }) Assert.True(PowerModel.Evaluate(db, db.Unit(id)!).Level > 0);

        // 21.4: TD with the preset placement, an autobattle and its report.
        var r = AutoBattle.Run(db, sc, 1, new AutoBattleOptions { MaxSeconds = 300 });
        _out.WriteLine(r.ToText(db));
        Assert.Equal(4, r.WaveStats.Count);
        Assert.Equal(12, r.WaveStats[0].Spawned);
        Assert.Equal(6, r.WaveStats[1].Spawned);
        Assert.Equal(9, r.WaveStats[2].Spawned);
        Assert.True(r.WaveStats[3].Started >= 0, "четвёртая волна начинается после контрольной точки обхода");
        Assert.NotEmpty(r.DamageByType);
        Assert.NotEmpty(r.DefenderDamage);
        Assert.All(r.RouteBlocked.Keys, k => Assert.Contains(sc.Routes, x => x.Id == k));
        Assert.Contains("волна 4 «Мимики»", r.ToText(db));
        var waves = WaveReports.Build(db, sc, new[] { r });
        Assert.Equal(4, waves.Rows.Count);
        Assert.Equal(1, waves.Rows[0].Runs);
        Assert.False(PerformanceBudget.Estimate(db, sc).OverBudget);

        // A change (stronger wolves) is one operation; then a reproducible batch.
        MassEdit.SetStat(db, new[] { db.Unit("wolf")! }, Stats.HpMax, MassStatMode.Multiply, 1.2);
        history.Touch(db.Unit("wolf")!);
        history.Commit("Волки крепче");
        var (b1, _) = AutoBattle.Batch(db, sc, 8, 100, new AutoBattleOptions { MaxSeconds = 300 }, threads: 1);
        var (b4, _) = AutoBattle.Batch(db, sc, 8, 100, new AutoBattleOptions { MaxSeconds = 300 }, threads: 4);
        Assert.Equal(b1.ToJson(), b4.ToJson());
        _out.WriteLine(b1.ToText());

        // 21.5: the leader fitted to a higher level by an «elite leader» profile — a new template, the source unchanged,
        // the synergy with the pack stays capped (no «множитель от союзников» warning).
        db.Add(new ScalingProfileDef
        {
            Id = "scale_elite_leader", Name = "Элитный лидер", Role = PowerRole.Tank,
            Params =
            {
                new ScaleParamDef { Target = ScaleTarget.Stat, Stat = Stats.HpMax, Weight = 1, MaxChange = 20 },
                new ScaleParamDef { Target = ScaleTarget.Stat, Stat = Stats.Power, Weight = 0.8, Step = 0.5, MaxChange = 20 },
                new ScaleParamDef { Target = ScaleTarget.Resistances, Weight = 0.4, MaxChange = 3 },
                new ScaleParamDef { Target = ScaleTarget.SkillDamage, Weight = 0.6, MaxChange = 10 },
            },
        });
        var src = db.Unit("wolf_leader")!;
        string srcJson = ContentJson.Serialize(src);
        double lv0 = PowerModel.Evaluate(db, src).Level;
        var fits = LevelFit.Variants(db, src, Math.Round(lv0) + 6, db.Get<ScalingProfileDef>("scale_elite_leader"));
        var best = fits.Last();
        Assert.True(best.Reached, best.ToString());
        var elite = best.Unit;
        elite.Id = ContentIO.UniqueId(db, DefCategory.Unit, "wolf_leader_elite");
        elite.Name = "Волк-вожак (элитный)";
        db.Add(elite);
        history.TouchAll();
        history.Commit("Элитный вожак");
        Assert.Equal(srcJson, ContentJson.Serialize(db.Unit("wolf_leader")!));
        Assert.Equal(PowerModel.Evaluate(db, src).Role, PowerModel.Evaluate(db, elite).Role);
        Assert.DoesNotContain(BalanceChecks.Check(db, elite), c => c.Rule == "множитель от союзников");
        var cmp = VersionCompare.Compare(db, src, elite, "вожак", "элитный");
        Assert.Contains(cmp.Lines, l => l.Label == "Уровень" && l.Changed);

        // Everything is undone step by step: the content is as before the workflow.
        while (history.CanUndo) history.Undo();
        Assert.Null(db.Get<ScenarioDef>("tz21"));
        Assert.Null(db.Unit(elite.Id));
        Assert.Equal(wolfBefore, ContentJson.Serialize(db.Unit("wolf")!));
    }

    [Fact]
    public void LargeProject_StaysManageable()
    {
        var db = TestUtil.Shipped();
        var rng = new Random(7);
        // 60 categories in a tree of depth 4 under the wolf.
        var parents = new List<string> { db.Unit("wolf")!.MainCategory };
        for (int i = 0; i < 60; i++)
        {
            var c = new OriginDef { Id = $"cat_{i}", Name = $"Категория {i}", Parent = parents[rng.Next(parents.Count)] };
            db.Add(c);
            if (i % 3 == 0) parents.Add(c.Id);
        }
        // 300 templates.
        var enemies = new[] { "goblin", "wolf", "wolf_scout", "orc_brute", "slime", "drone", "burrower" };
        for (int i = 0; i < 300; i++)
        {
            var u = ContentJson.Clone(db.Unit(enemies[i % enemies.Length])!);
            u.Id = $"tpl_{i}";
            u.Name = $"Шаблон {i}";
            u.MainCategory = $"cat_{rng.Next(60)}";
            u.Stats[Stats.HpMax] = 40 + rng.Next(200);
            db.Add(u);
        }
        // 30 scenarios × 8 waves × 6 groups on the TD pass.
        var basis = db.Get<ScenarioDef>("td_pass")!;
        for (int s = 0; s < 30; s++)
        {
            var sc = ContentJson.Clone(basis);
            sc.Id = $"big_{s}";
            sc.Name = $"Большой {s}";
            sc.Waves.Clear();
            for (int w = 0; w < 8; w++)
            {
                var wave = new WaveDef { Id = $"w{w}", Name = $"Волна {w + 1}", Start = WaveStart.Time, Time = 5 + 40 * w };
                for (int g = 0; g < 6; g++)
                    wave.Groups.Add(new SpawnGroupDef { Id = $"g{g}", Unit = $"tpl_{rng.Next(300)}", Count = 1 + rng.Next(6), Interval = 1, SpawnBase = "spawn_1", Route = "route_1" });
                sc.Waves.Add(wave);
            }
            db.Add(sc);
        }
        double T(Action a)
        {
            var sw = Stopwatch.StartNew();
            a();
            return sw.Elapsed.TotalSeconds;
        }
        List<ContentIssue> issues = new();
        double validate = T(() => issues = ContentValidator.Validate(db));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error && i.Source.StartsWith("Scenario:big_"));
        var big = db.Get<ScenarioDef>("big_0")!;
        double check = T(() => ScenarioChecks.Run(db, big, issues));
        double perf = T(() => PerformanceBudget.Estimate(db, big));
        double report = T(() => WaveReports.Build(db, big));
        double impact = T(() => Assert.True(ChangeImpact.OfTemplate(db, "tpl_1").Count >= 0 && ChangeImpact.OfCategory(db, "cat_0").Templates.Count >= 0));
        double power = T(() =>
        {
            foreach (var u in db.All<UnitDef>()) PowerModel.Evaluate(db, u);
        });
        var history = new EditHistory(db);
        double mass = T(() =>
        {
            MassEdit.SetStat(db, db.All<UnitDef>().Where(u => u.Id.StartsWith("tpl_")), Stats.MoveSpeed, MassStatMode.Multiply, 1.05);
            history.TouchAll();
            Assert.Equal(300, history.Commit("массово")!.Changes.Count);
        });
        double undo = T(() => history.Undo());
        _out.WriteLine($"проверка контента {validate:0.00} с, «Проверить сценарий» {check:0.00} с, бюджет {perf:0.000} с, отчёт по волнам {report:0.00} с, " +
                       $"затронутое {impact:0.000} с, оценка силы {db.All<UnitDef>().Count()} шаблонов {power:0.00} с, массовое изменение 300 + запись {mass:0.00} с, отмена {undo:0.00} с");
        Assert.True(validate < 20 * Slack, $"проверка контента {validate:0.0} с");
        Assert.True(check < 3 * Slack, $"«Проверить сценарий» {check:0.0} с");
        Assert.True(perf < 1 * Slack && report < 3 * Slack && impact < 2 * Slack, $"бюджет {perf:0.00} с, отчёт {report:0.00} с, затронутое {impact:0.00} с");
        Assert.True(power < 10 * Slack, $"оценка силы {power:0.0} с");
        Assert.True(mass < 5 * Slack && undo < 5 * Slack, $"массовое изменение {mass:0.0} с, отмена {undo:0.0} с");
    }

    [Fact]
    public void BrokenFile_IsReported_TheRestLoads_AndASavedPointRestoresEverything()
    {
        var db = TestUtil.Shipped();
        string dir = Path.Combine(Path.GetTempPath(), "hex_p4_" + Guid.NewGuid().ToString("N"));
        try
        {
            ContentIO.Save(db, new FileSystemStorage(dir));
            // A saved point restores every object as it was.
            var back = ContentIO.Load(new FileSystemStorage(dir, readOnly: true));
            Assert.False(back.HasErrors, string.Join("\n", back.Issues.Where(i => i.Severity == IssueSeverity.Error)));
            foreach (var d in db.AllDefs())
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(d, d.GetType(), ContentJson.Options),
                    System.Text.Json.JsonSerializer.Serialize(back.Db.Table(d.Category).Get(d.Id)!, d.GetType(), ContentJson.Options));
            // A broken file of scaling profiles: reported, everything else still loads.
            File.WriteAllText(Path.Combine(dir, "scaling_profiles.json"), "{ \"formatVersion\": 5, \"items\": [ { broken");
            var broken = ContentIO.Load(new FileSystemStorage(dir, readOnly: true));
            Assert.Contains(broken.Issues, i => i.Severity == IssueSeverity.Error && i.Source.Contains("scaling_profiles"));
            Assert.Equal(db.Table(DefCategory.Unit).Count, broken.Db.Table(DefCategory.Unit).Count);
            // The fit falls back to the built-in uniform profile.
            var fit = LevelFit.Fit(broken.Db, broken.Db.Unit("wolf")!, 5);
            Assert.True(fit.LevelAfter > fit.LevelBefore);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
