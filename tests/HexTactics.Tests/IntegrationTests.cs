using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;
using Xunit.Abstractions;

namespace HexTactics.Tests;

/// <summary>
/// Integration of part 2 (TZ §17 п. 11): the whole admin path in data — map from a template, terrain,
/// element and damage type, skill with an auto-attack, enemies, scenario with waves and objectives —
/// checked, published to the library, exported, imported into another library and played the same way.
/// </summary>
public class IntegrationTests
{
    /// <summary>Content an administrator builds in the editor on top of the built-in pack.</summary>
    public static ContentDatabase AdminContent()
    {
        var db = TestUtil.Shipped();
        // Map: the standard 10 × 8 template, then terrain painted on it.
        var map = MapTemplates.CreateMap(db.Get<MapTemplateDef>(MapTemplates.StandardId)!, "plasma_yard", db, "Плазменный двор");
        var player = map.Markers.First(m => m.Type == MarkerType.PlayerStart);
        var enemy = map.Markers.First(m => m.Type == MarkerType.EnemySpawn);
        foreach (var c in map.Cells)
        {
            int col = c.C.Q + c.C.R / 2;
            if (col == 5 && c.C.R is 0 or 1) c.T = "crates";
            if (col == 6 && c.C.R == 6) c.T = "lava";
            if (col == 4 && c.C.R == 7) c.T = "platform";
        }
        map.Routes.Add(new MapRouteDef { Id = "lane", Name = "Коридор", Points = { enemy.C, player.C + new HexCoord(2, 0) } });
        db.Add(map);
        // Element and damage type with inheritance.
        db.Add(new ElementDef { Id = "plasma", Name = "Плазма", Parent = "fire", Tier = ElementTier.Advanced });
        db.Add(new DamageTypeDef { Id = "plasma_burn", Name = "Плазменный ожог", Kind = DamageCategory.Elemental, Element = "plasma" });
        // A skill used as an auto-attack.
        db.Add(new SkillDef
        {
            Id = "plasma_bolt", Name = "Плазменный заряд", Kind = SkillKind.Attack, Targeting = TargetingMode.Unit, RangeMin = 1, RangeMax = 3, ApCost = 1, PrepTime = 0.2, RecoveryTime = 0.2,
            Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "plasma_burn", Amount = 4 } } } },
        });
        db.Add(new UnitDef
        {
            Id = "plasma_drone", Name = "Плазменный дрон", Faction = "enemy", BasicAttack = "plasma_bolt", AiProfile = "route_raider",
            Stats = { [Stats.HpMax] = 40, [Stats.ApMax] = 8, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 2.5, [Stats.DetectionRange] = 5 },
            AutoAttacks = { new AutoAttackDef { Skill = "plasma_bolt", Interval = 1.0 } },
            Defense = new DefenseDef { Modifiers = { new DamageModDef { Match = "el:plasma", Percent = -50 } } },
        });
        db.Add(new UnitDef
        {
            Id = "warden", Name = "Страж", Faction = "player", BasicAttack = "strike",
            Stats = { [Stats.HpMax] = 900, [Stats.ApMax] = 10, [Stats.ApRegen] = 1.5, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 6, [Stats.Armor] = 2 },
            AutoAttacks = { new AutoAttackDef { Interval = 1.2 } },
            Defense = new DefenseDef { Modifiers = { new DamageModDef { Match = "el:fire", Percent = -30 } } },
        });
        db.Add(new ScenarioDef
        {
            Id = "plasma_assault", Name = "Плазменный штурм", Map = "plasma_yard", Seed = 7,
            Units =
            {
                new ScenarioUnitDef { Unit = "warden", C = player.C, Facing = HexDirection.E, Tag = "warden" },
                new ScenarioUnitDef { Unit = "knight", C = player.C + new HexCoord(0, 1), Facing = HexDirection.E },
            },
            Waves =
            {
                new WaveDef { Name = "Дроны", Time = 2, SpawnMarker = enemy.Id, Route = "lane", Interval = 0.8, Units = { new WaveUnitDef { Unit = "plasma_drone", Count = 3 } } },
                new WaveDef { Name = "Подкрепление", Start = WaveStart.AfterPrevious, Delay = 3, SpawnMarker = enemy.Id, Route = "lane", Units = { new WaveUnitDef { Unit = "plasma_drone", Count = 2 } } },
            },
            Victory = new(),
            Objectives =
            {
                new ObjectiveDef { Id = "hold", Name = "Продержаться", Kind = ObjectiveKind.Survive, TimeLimit = 40, Required = true },
                new ObjectiveDef { Id = "warden", Name = "Сберечь стража", Kind = ObjectiveKind.Prevent, Required = true,
                    Fail = new ConditionDef { Type = ConditionType.UnitDead, UnitTag = "warden" } },
            },
            Triggers = { new TriggerDef { Name = "Сигнал", Condition = new ConditionDef { Type = ConditionType.WaveSpawned, Wave = 0 },
                Actions = { new TriggerActionDef { Type = TriggerActionType.Message, Text = "Дроны на подходе!" } } } },
            Limits = new ScenarioLimitsDef { FogOfWar = false },
        });
        return db;
    }

    private static (List<string> Log, BattleOutcome Outcome, Simulation Sim) Play(ContentDatabase db, double seconds = 45)
    {
        var sim = ScenarioSystem.Create(db, db.Get<ScenarioDef>("plasma_assault")!, 7);
        foreach (var u in sim.Units) u.AiControlled = true; // the player's side fights by itself
        for (long i = 0, n = sim.TicksFor(seconds); i < n && sim.Outcome == BattleOutcome.None; i++)
        {
            sim.Step();
            foreach (var u in sim.Units.Where(u => u.Alive && sim.IsPlayerFaction(u.FactionId))) u.AiControlled = true;
        }
        return (sim.Log.Entries.Select(e => $"{e.Tick}:{e.Type}:{e.Text}").ToList(), sim.Outcome, sim);
    }

    [Fact]
    public void AdminPath_CreatePublishExportImport_PlaysTheSame()
    {
        var db = AdminContent();
        var issues = ContentValidator.Validate(db);
        var gate = ContentChecks.PublishGate(db, issues);
        Assert.True(gate.CanPublish, string.Join("\n", gate.Blocking));

        // Saved in the library (transactional publication) and loaded back.
        var mem = new MemoryStorage();
        new ContentLibrary(mem).Publish(db, null);
        var loaded = ContentIO.Load(mem);
        Assert.False(loaded.HasErrors, string.Join("\n", loaded.Issues.Where(i => i.Severity == IssueSeverity.Error)));
        Assert.NotNull(loaded.Db.Get<ScenarioDef>("plasma_assault"));
        Assert.Equal("fire", loaded.Db.Get<ElementDef>("plasma")!.Parent);

        // The battle uses everything: waves along the route with warnings, the auto-attacking drones, plasma damage, resistances.
        var (log, outcome, sim) = Play(loaded.Db);
        Assert.Contains(log, l => l.Contains("Дроны на подходе!"));
        Assert.True(sim.Units.Count(u => u.Def.Id == "plasma_drone") == 5,
            $"обе волны должны появиться: дронов {sim.Units.Count(u => u.Def.Id == "plasma_drone")}, итог {outcome} на {sim.Time:0.#} с, волны: " +
            string.Join("; ", sim.Scenario.Waves.Select(w => $"{w.Def.Name} {w.State}")) + "; цели: " + string.Join("; ", sim.Scenario.Objectives.Select(o => $"{o.Def.Id} {o.State}")) + "; страж: " +
            string.Join(" | ", sim.Log.Entries.Where(e => e.TargetUnit == sim.Units.First(u => u.Tag == "warden").Id && e.Type is SimEventType.Damage or SimEventType.Death or SimEventType.Fall).Select(e => e.Text).TakeLast(6)));
        Assert.True(sim.Units.Where(u => u.Def.Id == "plasma_drone").Sum(u => sim.AutoAttacks.StatesOf(u).Sum(s => s.Fired)) > 0, "дроны должны атаковать автоатакой");
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.Damage && e.DamageType == "plasma_burn");
        Assert.NotEqual(BattleOutcome.None, outcome);

        // Exported as a package with all dependencies and imported into another library.
        var package = ContentPackage.Export(loaded.Db, new Def[] { loaded.Db.Get<ScenarioDef>("plasma_assault")! }, new PackageExportOptions
        {
            Name = "Плазменный штурм",
            // Resource files of the dependencies (the crate prop model of the «Ящики» terrain…) travel too.
            ReadAsset = p => System.IO.File.Exists(System.IO.Path.Combine(TestUtil.ContentDir, p)) ? System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestUtil.ContentDir, p)) : null,
        });
        var other = TestUtil.Shipped();
        var pkg = ContentPackage.Read(package);
        Assert.True(pkg.Ok, string.Join("\n", pkg.Issues));
        var plan = ContentPackage.Plan(other, pkg);
        Assert.Empty(plan.Missing);
        Assert.Empty(plan.Conflicts); // built-in objects are identical, the admin's objects are new
        Assert.False(pkg.Manifest.Incomplete, string.Join("; ", pkg.Manifest.MissingDependencies));
        Assert.Contains(pkg.Manifest.Assets, a => a.Path == "assets/models/supply_crate.glb");
        var report = ContentPackage.Apply(other, plan);
        foreach (var id in new[] { "plasma_yard", "plasma_assault" }) Assert.Contains(report.Added, d => d.Id == id);
        Assert.Contains(report.Added, d => d.Id == "plasma_burn");
        Assert.DoesNotContain(report.Issues, i => i.Severity == IssueSeverity.Error);

        // The same battle, the same result: nothing was lost on the way.
        var (log2, outcome2, _) = Play(other);
        Assert.Equal(outcome, outcome2);
        Assert.Equal(log, log2);
    }

    [Fact]
    public void ReloadedLibrary_IsDeterministic_ForEveryShippedScenario()
    {
        var db = TestUtil.Shipped();
        var mem = new MemoryStorage();
        new ContentLibrary(mem).Publish(db.Clone(), null);
        var reloaded = ContentIO.Load(mem).Db;
        foreach (var sc in db.All<ScenarioDef>())
        {
            string Run(ContentDatabase d)
            {
                var sim = ScenarioSystem.Create(d, d.Get<ScenarioDef>(sc.Id)!, 3);
                foreach (var u in sim.Units) u.AiControlled = true;
                sim.RunSeconds(15);
                return string.Join("\n", sim.Log.Entries.Select(e => $"{e.Tick}:{e.Type}:{e.Text}")) + sim.Outcome;
            }
            Assert.True(Run(db) == Run(reloaded), $"сценарий «{sc.Id}» после сохранения и загрузки идёт иначе");
        }
    }
}

/// <summary>Load tests of part 2: a large library and a large battle with every new system.</summary>
[Collection("Performance")]
public class LoadTests
{
    private readonly ITestOutputHelper _out;

    public LoadTests(ITestOutputHelper output)
    {
        _out = output;
    }

#if DEBUG
    private const double Slack = 2.0;
#else
    private const double Slack = 1.0;
#endif

    [Fact]
    public void LargeLibrary_ValidatePublishLoadExportImport()
    {
        var db = TestUtil.Shipped();
        var baseSkill = db.Skill("fireball")!;
        var baseUnit = db.Unit("goblin")!;
        for (int i = 0; i < 800; i++)
        {
            var s = ContentJson.Clone(baseSkill);
            s.Id = $"gen_skill_{i}";
            s.Name = $"Навык {i}";
            db.Add(s);
            var u = ContentJson.Clone(baseUnit);
            u.Id = $"gen_unit_{i}";
            u.Name = $"Юнит {i}";
            u.Skills = new List<string> { s.Id };
            db.Add(u);
        }
        for (int i = 0; i < 300; i++) db.Add(new StatusDef { Id = $"gen_status_{i}", Name = $"Состояние {i}", Duration = 3, Levels = { new StatusLevelDef { Threshold = 1 } } });
        int total = db.AllDefs().Count();

        var sw = Stopwatch.StartNew();
        var issues = ContentValidator.Validate(db);
        long validate = sw.ElapsedMilliseconds;
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);

        sw.Restart();
        var mem = new MemoryStorage();
        var lib = new ContentLibrary(mem);
        lib.Publish(db, null);
        long publish = sw.ElapsedMilliseconds;

        sw.Restart();
        var loaded = ContentIO.Load(mem);
        long load = sw.ElapsedMilliseconds;
        Assert.False(loaded.HasErrors);
        Assert.Equal(total, loaded.Db.AllDefs().Count());

        // A second publication with 100 changed objects (versions, history, backups).
        var draft = loaded.Db;
        for (int i = 0; i < 100; i++) draft.Skill($"gen_skill_{i}")!.Description = "v2";
        sw.Restart();
        var result = lib.Publish(draft, ContentIO.Load(mem).Db);
        long republish = sw.ElapsedMilliseconds;
        Assert.Equal(100, result.Changes.Count);
        Assert.True(lib.CheckIntegrity().Ok);

        sw.Restart();
        var roots = Enumerable.Range(0, 200).Select(i => (Def)draft.Unit($"gen_unit_{i}")!).ToList();
        var bytes = ContentPackage.Export(draft, roots, new PackageExportOptions { Name = "200 юнитов" });
        long export = sw.ElapsedMilliseconds;
        sw.Restart();
        var target = TestUtil.Shipped();
        var report = ContentPackage.Apply(target, ContentPackage.Plan(target, ContentPackage.Read(bytes)));
        long import = sw.ElapsedMilliseconds;
        Assert.Equal(400, report.Added.Count);

        _out.WriteLine($"объектов {total}: проверка {validate} мс, публикация {publish} мс, загрузка {load} мс, повторная публикация (100 изменений) {republish} мс, " +
                       $"экспорт 200 юнитов {export} мс ({bytes.Length / 1024} КБ), импорт {import} мс");
        Assert.True(validate < 8000 * Slack, $"проверка слишком долгая: {validate} мс");
        Assert.True(publish < 8000 * Slack && load < 8000 * Slack && republish < 8000 * Slack, $"публикация/загрузка слишком долгие: {publish}/{load}/{republish} мс");
        Assert.True(export < 8000 * Slack && import < 15000 * Slack, $"экспорт/импорт слишком долгие: {export}/{import} мс");
    }

    [Fact]
    public void BigBattle_WavesWarningsAutoAttacksBlockingStacking_WithinBudget()
    {
        var db = TestUtil.Shipped();
        db.Rules.Autopause = new AutopauseRules { EnemySpotted = false, AllyCriticalDamage = false, UnitDied = false, PathUnavailable = false, WaveStarted = false };
        var map = MapDef.CreateRectangle("big_defense", 40, 24, "floor");
        map.Markers.Add(new MapMarkerDef { Id = "spawn_a", Type = MarkerType.EnemySpawn, C = new HexCoord(36 - 3, 4) });
        map.Markers.Add(new MapMarkerDef { Id = "spawn_b", Type = MarkerType.EnemySpawn, C = new HexCoord(36 - 10, 18) });
        map.Markers.Add(new MapMarkerDef { Id = "core", Type = MarkerType.Objective, C = new HexCoord(-2, 12), Radius = 1 });
        map.Routes.Add(new MapRouteDef { Id = "north", Points = { new HexCoord(20, 6), new HexCoord(6, 10), new HexCoord(-2, 12) } });
        map.Routes.Add(new MapRouteDef { Id = "south", Points = { new HexCoord(14, 18), new HexCoord(2, 14), new HexCoord(-2, 12) } });
        db.Add(map);
        var sc = new ScenarioDef
        {
            Id = "load_defense", Name = "Нагрузка", Map = map.Id, Seed = 5, Victory = new(), Defeat = new(),
            Limits = new ScenarioLimitsDef { FogOfWar = true, Stacking = StackingMode.ByUnitType },
            Objectives = { new ObjectiveDef { Id = "core", Name = "Ядро", Kind = ObjectiveKind.Defend, Marker = "core", AllowedLeaks = 1000, Required = true } },
        };
        string[] defenders = { "knight", "archer", "mage", "priestess", "sentinel" };
        int n = 0;
        foreach (var c in map.Cells.Where(c => c.C.Q + c.C.R / 2 is >= 2 and <= 8 && c.C.R % 2 == 0 && c.C.R is >= 2 and <= 22))
        {
            if (n >= 60) break;
            sc.Units.Add(new ScenarioUnitDef { Unit = defenders[n % defenders.Length], C = c.C, Facing = HexDirection.E });
            n++;
        }
        for (int w = 0; w < 6; w++)
            sc.Waves.Add(new WaveDef
            {
                Name = $"Волна {w + 1}", Time = 1 + w * 6, SpawnMarkers = { "spawn_a", "spawn_b" }, Route = w % 2 == 0 ? "north" : "south", Interval = 0.25, MaxAlive = 150,
                Units = { new WaveUnitDef { Unit = "goblin", Count = 12 }, new WaveUnitDef { Unit = "goblin_archer", Count = 6 }, new WaveUnitDef { Unit = "orc_brute", Count = 2 } },
            });
        db.Add(sc);
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Source == "Scenario:load_defense");

        var sim = ScenarioSystem.Create(db, sc, 5);
        foreach (var u in sim.Units) u.AiControlled = true;
        long ticks = sim.TicksFor(45);
        var sw = Stopwatch.StartNew();
        int maxAlive = 0;
        int warnings = 0;
        for (int i = 0; i < ticks; i++)
        {
            sim.Step();
            foreach (var u in sim.Units.Where(u => u.Alive && sim.IsPlayerFaction(u.FactionId))) u.AiControlled = true;
            maxAlive = Math.Max(maxAlive, sim.AliveUnits.Count());
            warnings = Math.Max(warnings, sim.Scenario.Warnings.All.Count());
        }
        sw.Stop();
        double msPerTick = sw.Elapsed.TotalMilliseconds / ticks;
        int fired = sim.Units.Sum(u => sim.AutoAttacks.StatesOf(u).Sum(s => s.Fired));
        _out.WriteLine($"юнитов всего {sim.Units.Count}, одновременно до {maxAlive}, маркеров появления до {warnings}, автоатак {fired}, прорывов {sim.Scenario.Objectives[0].Leaks}, " +
                       $"событий {sim.Log.TotalCount}, мс/тик {msPerTick:0.00}");
        foreach (var (sec, _, per) in sim.Profiler.Totals()) _out.WriteLine($"  {sec}: {per:0.000} мс/тик");
        Assert.True(sim.Units.Count >= 60 + 120, "все волны должны появиться");
        Assert.True(warnings > 0 && fired > 0, "маркеры появления и автоатаки должны работать");
        // 20 ticks per second at 4× speed = 80 ticks per real second → 12.5 ms per tick (Release).
        Assert.True(msPerTick < 12.5 * Slack, $"слишком медленно: {msPerTick:0.00} мс/тик");
    }
}
