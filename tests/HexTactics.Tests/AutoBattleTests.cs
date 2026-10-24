using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 4, stage 5: AI rules «если — то — иначе» with priority, the encounter of route enemies (pursuit, distraction,
/// return to the nearest point ahead), alternative targets, the preparation of «TD с расстановкой», free battle and
/// autotest, the battle report and batches, breakpoints, scenario events with filters, limits and the cycle check
/// (docs/DECISIONS_4.md §59, §64, §68).
/// </summary>
public class AutoBattleTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new AiProfileDef { Id = "runner", Name = "Бегущий к базе", RouteFocus = true });
        db.Add(new AiProfileDef { Id = "guardp", Name = "Страж", HoldPosition = true, HoldRadius = 2, Pursue = false });
        db.Add(new UnitDef
        {
            Id = "raider", Name = "Налётчик", Faction = "enemy", BasicAttack = "hit", AiProfile = "runner", CreatureCategory = "humanoid",
            Stats = { [Stats.HpMax] = 60, [Stats.ApMax] = 10, [Stats.ApRegen] = 3, [Stats.MoveSpeed] = 3, [Stats.DetectionRange] = 8, [Stats.BaseDamage] = 10 },
        });
        db.Add(new UnitDef
        {
            Id = "rock", Name = "Скала", Faction = "player", BasicAttack = "hit", CreatureCategory = "humanoid",
            Stats = { [Stats.HpMax] = 5000, [Stats.ApMax] = 10, [Stats.ApRegen] = 0.1, [Stats.MoveSpeed] = 1, [Stats.DetectionRange] = 8 },
        });
        db.Add(new UnitDef
        {
            Id = "veteran", Name = "Ветеран", Faction = "player", BasicAttack = "hit", CreatureCategory = "humanoid",
            Stats = { [Stats.HpMax] = 160, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        return db;
    }

    /// <summary>A field 16 × 7: a spawn base at (0,3), the main base at (14,3), a second base at (14,6), a route along row 3.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc, RouteDef Route) Scene(string profile = "runner", bool secondBase = false)
    {
        var db = Db();
        db.Unit("raider")!.AiProfile = profile;
        var map = TestUtil.Field(16, 7);
        map.Id = "ab_field";
        db.Add(map);
        var sc = new ScenarioDef { Id = "ab", Name = "Автобой", Map = map.Id, Seed = 5, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 3)));
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(14, 3)));
        if (secondBase) sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(14, 6)));
        sc.BaseDefeat.Mode = BaseDefeatMode.None;
        var cells = HexCoord.Line(new HexCoord(0, 3), new HexCoord(13, 3)).ToList();
        var r = RouteGraph.FromCells("r1", cells, "spawn_1", "base_1", "Дорога");
        RouteGraph.NodeOn(r, new HexCoord(4, 3));
        RouteGraph.NodeOn(r, new HexCoord(9, 3));
        sc.Routes.Add(r);
        db.Add(sc);
        return (db, sc, r);
    }

    private static WaveDef Wave(string unit = "raider", int count = 1, double time = 1, string group = "g", string spawn = "spawn_1", string route = "r1") => new()
    {
        Name = "Волна " + group, Start = WaveStart.Time, Time = time,
        Groups = { new SpawnGroupDef { Id = group, Unit = unit, Count = count, Interval = 0.5, SpawnBase = spawn, Route = route } },
    };

    private static List<SimEvent> Collect(Simulation sim)
    {
        var list = new List<SimEvent>();
        sim.Observed += list.Add;
        return list;
    }

    // ------------------------------------------------------------------ AI rules

    [Fact]
    public void AiRules_IfThenElse_FromTheHighestPriority()
    {
        var (db, sc, _) = Scene();
        var p = new AiProfileDef { Id = "ruled", Name = "С правилами" };
        var low = new AiRuleDef
        {
            Name = "ранен", Priority = 0, When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.5 }, Directive = AiDirective.Retreat,
            Actions = { new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "low_then" } },
            ElseActions = { new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "low_else" } },
        };
        var top = new AiRuleDef { Name = "главное", Priority = 5, When = new ConditionDef { Type = ConditionType.Always }, Directive = AiDirective.Hold, ElseDirective = AiDirective.Wait, Once = false, Cooldown = 1 };
        // The higher priority is listed second: the order of the list does not matter, the priority does.
        p.Rules.Add(low);
        p.Rules.Add(top);
        db.Add(p);
        sc.Units.Add(new ScenarioUnitDef { Unit = "raider", C = new HexCoord(5, 1), Faction = "enemy", AiProfile = "ruled" });
        var sim = ScenarioSystem.Create(db, sc, 1);
        var u = sim.Units.Single(x => x.Def.Id == "raider");
        sim.RunSeconds(0.5);
        Assert.Equal(AiDirective.Hold, u.Ai.Directive);
        Assert.Equal("главное", u.Ai.DirectiveRule);
        Assert.Contains("low_else", sim.Scenario.Flags);
        Assert.DoesNotContain("low_then", sim.Scenario.Flags);

        // «То» of the lower rule runs its actions, but the behaviour still comes from the higher one.
        u.Hp = u.HpMax * 0.4;
        sim.RunSeconds(0.2);
        Assert.Contains("low_then", sim.Scenario.Flags);
        Assert.Equal(AiDirective.Hold, u.Ai.Directive);

        // The higher rule's «иначе» decides when its condition fails.
        top.When = new ConditionDef { Type = ConditionType.Not, Children = { new ConditionDef { Type = ConditionType.Always } } };
        sim.RunSeconds(1);
        Assert.Equal(AiDirective.Wait, u.Ai.Directive);
        Assert.Equal("главное (иначе)", u.Ai.DirectiveRule);
        Assert.Contains("ждёт", u.Ai.Explanation);

        // Without «иначе» the next rule by priority decides.
        top.ElseDirective = AiDirective.None;
        sim.RunSeconds(0.2);
        Assert.Equal(AiDirective.Retreat, u.Ai.Directive);
    }

    // ------------------------------------------------------------------ encounters

    private static (Simulation Sim, Unit Raider, Unit Rock, List<SimEvent> Log) Encounter(EncounterDef enc)
    {
        var (db, sc, _) = Scene("hunt");
        db.Add(new AiProfileDef { Id = "hunt", Name = "Охотник", Encounter = enc, Pursue = false });
        sc.Units.Add(new ScenarioUnitDef { Unit = "rock", C = new HexCoord(6, 1) });
        sc.Waves.Add(Wave());
        var sim = ScenarioSystem.Create(db, sc, 3);
        var log = Collect(sim);
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Def.Id == "raider"), 5);
        return (sim, sim.Units.Single(x => x.Def.Id == "raider"), sim.Units.Single(x => x.Def.Id == "rock"), log);
    }

    [Fact]
    public void Runner_StaysOnTheRoute_Hunter_LeavesWithinItsRange_AndComesBackAhead()
    {
        // A runner never leaves row 3 for a defender two hexes away.
        var (sim, raider, _, _) = Encounter(new EncounterDef { Mode = EncounterMode.Runner, PursuitRange = 5 });
        var rows = new HashSet<int>();
        TestUtil.RunUntil(sim, () => { rows.Add(raider.Pos.R); return !raider.Alive || raider.Pos.Q >= 12; }, 40);
        Assert.Equal(new HashSet<int> { 3 }, rows);

        // A hunter leaves for it, fights for its distraction time, then returns to the nearest point ahead.
        (sim, raider, var rock, var log) = Encounter(new EncounterDef { Mode = EncounterMode.Hunter, PursuitRange = 3, DistractionSeconds = 4 });
        bool left = false;
        TestUtil.RunUntil(sim, () => { left |= raider.Pos.R != 3; return left && raider.Ai.OffRouteSince >= 0; }, 20);
        Assert.True(left, "охотник должен сойти с маршрута к защитнику");
        TestUtil.RunUntil(sim, () => !raider.Alive || raider.Pos.Q >= 12, 60);
        Assert.Contains(log, e => e.Type == SimEventType.AiDecision && e.Text.Contains("возвращается на маршрут"));
        Assert.True(raider.Pos.Q >= 12 || !raider.Alive, $"налётчик должен пойти дальше по маршруту, сейчас {raider.Pos}");
        // It did not walk back to the node it had passed.
        Assert.Equal(1, raider.Nav!.Passed.Count(n => RouteGraph.NodeAt(sim.Scenario.Def!.Route("r1")!, new HexCoord(4, 3))!.Id == n));
        Assert.True(rock.Hp < rock.HpMax, "охотник должен был атаковать защитника");
    }

    [Fact]
    public void Hunter_IgnoresTargetsThatAreNotAllowed_OrBeyondItsRange()
    {
        var (sim, raider, _, _) = Encounter(new EncounterDef { Mode = EncounterMode.Hunter, PursuitRange = 3, AllowedTargets = { "tag:mage" } });
        var rows = new HashSet<int>();
        TestUtil.RunUntil(sim, () => { rows.Add(raider.Pos.R); return !raider.Alive || raider.Pos.Q >= 12; }, 40);
        Assert.Equal(new HashSet<int> { 3 }, rows);

        // «Приоритет базы» 1 is a runner too.
        (sim, raider, _, _) = Encounter(new EncounterDef { Mode = EncounterMode.Hunter, PursuitRange = 3, BasePriority = 1 });
        rows.Clear();
        TestUtil.RunUntil(sim, () => { rows.Add(raider.Pos.R); return !raider.Alive || raider.Pos.Q >= 12; }, 40);
        Assert.Equal(new HashSet<int> { 3 }, rows);
    }

    // ------------------------------------------------------------------ alternative targets

    [Fact]
    public void DestroyedBase_SendsTheEnemyToAnotherBase_ThenToTheDefenders_OrStops()
    {
        var (db, sc, _) = Scene(secondBase: true);
        sc.Waves.Add(Wave());
        var sim = ScenarioSystem.Create(db, sc, 1);
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Def.Id == "raider"), 5);
        var raider = sim.Units.Single(x => x.Def.Id == "raider");
        var b1 = sim.Structures.Of("base_1")!;
        sim.Structures.DamageStructure(b1, b1.Hp + 1, null, "тест");
        TestUtil.RunUntil(sim, () => sim.Structures.Reached.ContainsKey("base_2"), 40);
        Assert.Equal(AltTargetKind.OtherBase, raider.Ai.Alternative);
        Assert.True(sim.Structures.Reached.GetValueOrDefault("base_2") >= 1, "враг должен дойти до второй базы");

        // One base only: the enemy goes for the defenders.
        (db, sc, _) = Scene();
        sc.Units.Add(new ScenarioUnitDef { Unit = "rock", C = new HexCoord(8, 6) });
        sc.Waves.Add(Wave());
        sim = ScenarioSystem.Create(db, sc, 1);
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Def.Id == "raider"), 5);
        raider = sim.Units.Single(x => x.Def.Id == "raider");
        sim.Structures.DamageStructure(sim.Structures.Of("base_1")!, 99999, null, "тест");
        var rock = sim.Units.Single(x => x.Def.Id == "rock");
        TestUtil.RunUntil(sim, () => HexCoord.Distance(raider.Pos, rock.Pos) <= 1, 40);
        Assert.Equal(AltTargetKind.AttackDefenders, raider.Ai.Alternative);
        Assert.True(HexCoord.Distance(raider.Pos, rock.Pos) <= 1, "враг должен прийти к защитнику");

        // The profile says «остановиться».
        (db, sc, _) = Scene("stopper");
        db.Add(new AiProfileDef { Id = "stopper", Name = "Стоп", RouteFocus = true, AltTargets = { new AltTargetDef { Kind = AltTargetKind.Stop } } });
        sc.Waves.Add(Wave());
        sim = ScenarioSystem.Create(db, sc, 1);
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Def.Id == "raider"), 5);
        raider = sim.Units.Single(x => x.Def.Id == "raider");
        sim.RunSeconds(1);
        sim.Structures.DamageStructure(sim.Structures.Of("base_1")!, 99999, null, "тест");
        sim.RunSeconds(1);
        var at = raider.Pos;
        sim.RunSeconds(5);
        Assert.Equal(AltTargetKind.Stop, raider.Ai.Alternative);
        Assert.Equal(at, raider.Pos);
    }

    [Fact]
    public void ClosedRoute_AfterTheScenarioDelay_IsAnUnreachableBase()
    {
        var (db, sc, _) = Scene(secondBase: true);
        // A wall across the route: no way to the next point even past the units.
        db.Get<MapDef>(sc.Map)!.Cells.First(c => c.C == new HexCoord(5, 3)).T = "wall";
        sc.AltAfterBlocked = 3;
        sc.Waves.Add(Wave());
        var sim = ScenarioSystem.Create(db, sc, 1);
        var log = Collect(sim);
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Def.Id == "raider"), 5);
        var raider = sim.Units.Single(x => x.Def.Id == "raider");
        TestUtil.RunUntil(sim, () => raider.Ai.Alternative != null, 20);
        Assert.Contains(log, e => e.Type == SimEventType.AiProblem && e.DamageType is "no_path" or "route_blocked");
        Assert.Equal(AltTargetKind.OtherBase, raider.Ai.Alternative);
        Assert.Equal("base_2", raider.TargetObject);
    }

    // ------------------------------------------------------------------ TD with placement

    private static (ContentDatabase Db, ScenarioDef Sc) PlacementScene()
    {
        var (db, sc, _) = Scene();
        sc.Mode = BattleMode.TdPlacement;
        sc.Placement = new PlacementDef
        {
            End = PreparationEnd.Confirm, Budget = 20, MaxUnits = 3, ZoneObjects = { "base_1" }, ZoneRadius = 2, RestoreHealth = 0.5,
            Units =
            {
                new PlacementUnitDef { Unit = "soldier", Cost = 8, MaxCount = 2, UpgradeTo = "veteran", UpgradeCost = 3 },
                new PlacementUnitDef { Unit = "rock", Cost = 15 },
            },
        };
        sc.Waves.Add(Wave(time: 2));
        return (db, sc);
    }

    [Fact]
    public void Preparation_HoldsTheWaves_AndPlacingKeepsBudgetLimitsAndZones()
    {
        var (db, sc) = PlacementScene();
        var sim = ScenarioSystem.Create(db, sc, 1);
        var pl = sim.Placement;
        Assert.True(sim.Scenario.Preparing);
        sim.RunSeconds(5);
        Assert.DoesNotContain(sim.Units, u => u.Def.Id == "raider");
        Assert.Equal(0, sim.Scenario.BattleTime);
        Assert.Equal(BattleOutcome.None, sim.Outcome);

        Assert.Contains("вне зоны", pl.CanPlace("soldier", new HexCoord(3, 3)));
        Assert.Contains("недоступен", pl.CanPlace("veteran", new HexCoord(13, 3)));
        Assert.Contains("занят", pl.CanPlace("soldier", new HexCoord(14, 3)));
        var a = pl.Place("soldier", new HexCoord(13, 3), out var why);
        Assert.NotNull(a);
        Assert.Equal(12, pl.Left, 6);
        var b = pl.Place("soldier", new HexCoord(13, 4), out why);
        Assert.NotNull(b);
        Assert.Contains("не более 2", pl.CanPlace("soldier", new HexCoord(12, 4)));
        Assert.Contains("не хватает бюджета", pl.CanPlace("rock", new HexCoord(12, 4)));
        Assert.True(pl.Remove(b!));
        Assert.Equal(12, pl.Left, 6);
        Assert.Null(pl.CanPlace("soldier", new HexCoord(12, 4)));

        // Confirming starts the battle time: the wave «через 2 с» counts from now.
        long confirmed = sim.Tick;
        Assert.True(pl.Confirm());
        Assert.False(sim.Scenario.Preparing);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.PreparationDone);
        var w = sim.Scenario.Waves[0];
        Assert.Equal(confirmed + sim.TicksFor(2), w.Planned[0].Tick);
        Assert.Null(pl.CanPlace("soldier", new HexCoord(12, 4), free: true));
        Assert.Contains("подготовка завершена", pl.CanPlace("soldier", new HexCoord(12, 4)));
        Assert.False(pl.Remove(a!));
    }

    [Fact]
    public void BetweenWaves_DefendersRecover_MayBeMoved_AndUpgraded()
    {
        var (db, sc) = PlacementScene();
        var sim = ScenarioSystem.Create(db, sc, 1);
        var pl = sim.Placement;
        var a = pl.Place("soldier", new HexCoord(13, 3), out _)!;
        pl.Confirm();
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Def.Id == "raider"), 5);
        Assert.Contains("идёт волна", pl.CanMove(a, new HexCoord(12, 4)));
        a.Hp = 40;
        var raider = sim.Units.Single(x => x.Def.Id == "raider");
        sim.Combat.Kill(raider, a, "тест");
        sim.RunSeconds(0.2);
        Assert.True(pl.BetweenWaves);
        Assert.Equal(70, a.Hp, 3);
        Assert.True(pl.Move(a, new HexCoord(12, 4), out var why), why);
        Assert.Equal(new HexCoord(12, 4), a.Pos);
        double left = pl.Left;
        var v = pl.Upgrade(a, out why);
        Assert.NotNull(v);
        Assert.Equal("veteran", v!.Def.Id);
        Assert.Equal(new HexCoord(12, 4), v.Pos);
        Assert.Equal(left - 3, pl.Left, 6);
        Assert.False(a.Alive && sim.Units.Contains(a));
    }

    // ------------------------------------------------------------------ free battle, autotest

    [Fact]
    public void FreeBattle_HasNoRoutes_EnemiesLookForTheDefenders()
    {
        var (db, sc, _) = Scene("aggressive");
        sc.Mode = BattleMode.FreeBattle;
        sc.Units.Add(new ScenarioUnitDef { Unit = "rock", C = new HexCoord(10, 6) });
        sc.Waves.Add(Wave());
        sc.Limits.FogOfWar = false;
        var sim = ScenarioSystem.Create(db, sc, 1);
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Def.Id == "raider"), 5);
        var raider = sim.Units.Single(x => x.Def.Id == "raider");
        var rock = sim.Units.Single(x => x.Def.Id == "rock");
        Assert.Null(raider.Route);
        Assert.Equal("", raider.RouteId);
        TestUtil.RunUntil(sim, () => HexCoord.Distance(raider.Pos, rock.Pos) <= 1, 30);
        Assert.True(HexCoord.Distance(raider.Pos, rock.Pos) <= 1, $"враг должен найти защитника, сейчас {raider.Pos}");
    }

    [Fact]
    public void AutoTest_PlacesThePreset_DefendersFollowTheirProfiles()
    {
        var (db, sc, _) = Scene();
        sc.Mode = BattleMode.AutoTest;
        sc.Placement.Preset.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(12, 3) });
        sc.Placement.DefenderProfile = "guardp";
        sc.Waves.Add(Wave(count: 2));
        sc.Victory = new() { new ConditionDef { Type = ConditionType.AllEnemiesDead } };
        var sim = ScenarioSystem.Create(db, sc, 1);
        var d = sim.Units.Single(x => x.Def.Id == "soldier");
        Assert.True(d.AiControlled);
        Assert.Equal("guardp", d.AiProfile!.Id);
        Assert.False(sim.Scenario.Preparing);
    }

    // ------------------------------------------------------------------ report, batches, breakpoints

    private static (ContentDatabase Db, ScenarioDef Sc) Fight()
    {
        var (db, sc) = PlacementScene();
        sc.Placement.Preset.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(12, 3), Tag = "hero" });
        sc.Placement.Preset.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(12, 4) });
        sc.Placement.DefenderProfile = "aggressive";
        sc.Waves.Add(Wave(count: 3, time: 6, group: "g2"));
        sc.Victory = new() { new ConditionDef { Type = ConditionType.AllEnemiesDead } };
        sc.BaseDefeat.Mode = BaseDefeatMode.MainBase;
        return (db, sc);
    }

    [Fact]
    public void Report_CollectsTheBattle_TheSameSeedGivesTheSameReport()
    {
        var (db, sc) = Fight();
        var r1 = AutoBattle.Run(db, sc, 11, new AutoBattleOptions { MaxSeconds = 120 });
        var r2 = AutoBattle.Run(db, sc, 11, new AutoBattleOptions { MaxSeconds = 120 });
        Assert.Equal(r1.ToJson(), r2.ToJson());
        Assert.NotEqual("не завершён", r1.Outcome);
        Assert.Equal(2, r1.WavesTotal);
        Assert.True(r1.Skills.TryGetValue("hit", out var hit) && hit.Casts > 0 && hit.Hits > 0, "удары должны быть посчитаны");
        Assert.True(r1.DamageDealt.GetValueOrDefault(BattleReport.PlayerSide) > 0);
        Assert.True(r1.DamageByType.ContainsKey("physical"));
        Assert.Equal(4, r1.EnemiesKilled + r1.EnemiesReachedGoal);
        Assert.True(r1.Seconds > 6);
        Assert.Single(r1.Bases);
        var text = r1.ToText(db);
        Assert.Contains("Результат:", text);
        Assert.Contains("Навыки (запуски/попадания)", text);
    }

    [Fact]
    public void Batch_SeedsInOrder_DoesNotDependOnThreads_AndLeavesTheScenarioAsItWas()
    {
        var (db, sc) = Fight();
        string before = ContentJson.Serialize(sc);
        var (one, reports) = AutoBattle.Batch(db, sc, 6, 100, new AutoBattleOptions { MaxSeconds = 90 }, threads: 1);
        var (four, _) = AutoBattle.Batch(db, sc, 6, 100, new AutoBattleOptions { MaxSeconds = 90 }, threads: 4);
        Assert.Equal(one.ToJson(), four.ToJson());
        Assert.Equal(new ulong[] { 100, 101, 102, 103, 104, 105 }, one.Runs.Select(r => r.Seed).ToArray());
        Assert.Equal(before, ContentJson.Serialize(sc));
        Assert.InRange(one.WinRate, 0, 1);
        Assert.True(one.MinSeconds <= one.MeanSeconds + 1e-9 && one.MeanSeconds <= one.MaxSeconds + 1e-9);
        Assert.Contains("Побед:", one.ToText());

        var dir = Path.Combine(Path.GetTempPath(), "ht_reports_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = AutoBattle.Save(dir, one, reports, "t");
            Assert.True(File.Exists(path + ".json") && File.Exists(path + ".txt"));
            Assert.Contains("\"winRate\"", File.ReadAllText(path + ".json"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Breakpoints_StopTheRunOnTheirEvent()
    {
        var (db, sc) = Fight();
        var r = AutoBattle.Run(db, sc, 11, new AutoBattleOptions { MaxSeconds = 120, StopOn = { new BreakpointDef { Kind = BreakpointKind.WaveStarted } } });
        Assert.StartsWith("остановка на событии", r.StopReason);
        Assert.Equal("не завершён", r.Outcome);
        Assert.InRange(r.Seconds, 1.5, 3);

        r = AutoBattle.Run(db, sc, 11, new AutoBattleOptions
        {
            MaxSeconds = 120,
            StopOn = { new BreakpointDef { Kind = BreakpointKind.Condition, Condition = new ConditionDef { Type = ConditionType.KillCount, Count = 1 } } },
        });
        Assert.True(r.StopReason.Contains("условие"), r.ToText());
        Assert.Equal(1, r.EnemiesKilledByDefenders);
        Assert.Equal("не завершён", r.Outcome);
    }

    [Fact]
    public void LogicOnly_MatchesTheBattleDrivenByFrames_AtAnySpeed()
    {
        var (db, sc) = Fight();
        var opts = new AutoBattleOptions { MaxSeconds = 120 };
        var logic = AutoBattle.Run(db, sc, 21, opts).ToJson();
        foreach (var speed in new[] { 1, 16 })
        {
            var (sim, report, breaks) = AutoBattle.Prepare(db, sc, 21, opts);
            var driver = new SimDriver(sim) { Paused = false };
            driver.SetSpeedIndex(driver.Speeds.IndexOf(speed));
            for (int f = 0; f < 60 * 400 && sim.Outcome == BattleOutcome.None && sim.Scenario.BattleTime < opts.MaxSeconds; f++)
            {
                driver.Advance(1 / 60.0);
                driver.Paused = false;
            }
            breaks.Detach();
            Assert.Equal(logic, report.Finish().ToJson());
        }
    }

    // ------------------------------------------------------------------ scenario events

    [Fact]
    public void WaveEnded_ActivatesASecondBase_ItsWaveWaitsUntilThen()
    {
        var (db, sc, _) = Scene();
        var s2 = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 6));
        s2.Spawn!.Active = false;
        sc.Objects.Add(s2);
        sc.Routes.Add(RouteGraph.FromCells("r2", HexCoord.Line(new HexCoord(0, 6), new HexCoord(13, 4)).ToList(), s2.Id, "base_1", "Юг"));
        sc.Waves.Add(Wave());
        sc.Waves.Add(Wave(time: 1, group: "south", spawn: s2.Id, route: "r2"));
        sc.Triggers.Add(new TriggerDef
        {
            Name = "Юг открыт", Event = ScenarioEventType.WaveEnded, EventWave = 1, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.ActivateBase, Object = s2.Id } },
        });
        sc.Triggers.Add(new TriggerDef
        {
            Name = "Активация", Event = ScenarioEventType.ObjectActivated, EventObject = s2.Id, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "south_open" } },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Group == "g"), 5);
        sim.RunSeconds(3);
        Assert.DoesNotContain(sim.Units, x => x.Group == "south");
        sim.Combat.Kill(sim.Units.Single(x => x.Group == "g"), null, "тест");
        TestUtil.RunUntil(sim, () => sim.Units.Any(x => x.Group == "south"), 5);
        Assert.Contains(sim.Units, x => x.Group == "south");
        Assert.Contains("south_open", sim.Scenario.Flags);
        Assert.Equal(1, sim.Scenario.FiresOf(0));
    }

    [Fact]
    public void Events_Filters_RateLimit_MaxFires_Timer_AndConditionEdge()
    {
        var (db, sc, _) = Scene("guardp");
        sc.Waves.Add(Wave(count: 4, group: "pack"));
        sc.Waves.Add(Wave(count: 2, time: 1, group: "other"));
        var always = new ConditionDef { Type = ConditionType.Always };
        sc.Triggers.Add(new TriggerDef { Name = "стая", Event = ScenarioEventType.UnitSpawned, EventGroup = "pack", Once = false, MaxFires = 3, Condition = always });
        sc.Triggers.Add(new TriggerDef { Name = "смерть", Event = ScenarioEventType.UnitDied, EventUnit = "raider", Once = false, Cooldown = 5, Condition = always });
        sc.Triggers.Add(new TriggerDef { Name = "таймер", Event = ScenarioEventType.Timer, Interval = 2, Once = false, MaxFires = 3, Condition = always });
        sc.Triggers.Add(new TriggerDef
        {
            Name = "фронт", Event = ScenarioEventType.ConditionMet, Once = false, Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "x" },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(4);
        Assert.Equal(6, sim.Units.Count(u => u.Wave >= 0));
        Assert.Equal(3, sim.Scenario.FiresOf(0));
        foreach (var u in sim.Units.Where(u => u.Wave >= 0).ToList()) sim.Combat.Kill(u, null, "тест");
        sim.RunSeconds(0.2);
        Assert.Equal(1, sim.Scenario.FiresOf(1));
        sim.RunSeconds(8);
        Assert.Equal(3, sim.Scenario.FiresOf(2));
        Assert.Equal(0, sim.Scenario.FiresOf(3));
        sim.Scenario.SetFlag("x");
        sim.RunSeconds(1);
        Assert.Equal(1, sim.Scenario.FiresOf(3));
    }

    [Fact]
    public void EventCycles_StaticCheck_ErrorWithoutLimit_WarningWithIt_AndTheChainIsCutInBattle()
    {
        var (db, sc, _) = Scene("guardp");
        // Units of the scenario stand from the start; the first appearance is the wave's.
        sc.Waves.Add(Wave());
        var spawnMore = new TriggerDef
        {
            Name = "размножение", Event = ScenarioEventType.UnitSpawned, Once = false, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.SummonUnits, Unit = "raider", Count = 1 } },
        };
        sc.Triggers.Add(spawnMore);
        var cycles = ScenarioEvents.Cycles(sc);
        Assert.Single(cycles);
        Assert.False(cycles[0].Limited);
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("Бесконечный цикл событий"));

        // In battle the chain ends at its depth limit instead of hanging.
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(2);
        Assert.True(sim.Scenario.EventChainCuts >= 1);
        Assert.InRange(sim.Units.Count(u => u.Def.Id == "raider"), 2, ScenarioSystem.MaxTriggerDepth + 3);

        // An explicit limit makes it an intended cycle: a warning, and exactly that many fires.
        spawnMore.MaxFires = 3;
        Assert.True(ScenarioEvents.Cycles(sc)[0].Limited);
        issues = ContentValidator.Validate(db);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("цикл событий"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("Намеренный цикл событий"));
        sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(2);
        Assert.Equal(4, sim.Units.Count(u => u.Def.Id == "raider"));
    }

    [Fact]
    public void NewActions_GroupRoute_Target_ObjectState_End_Diagnostics_AndBattleEnded()
    {
        var (db, sc, _) = Scene(secondBase: true);
        sc.Routes.Add(RouteGraph.FromCells("r2", HexCoord.Line(new HexCoord(0, 3), new HexCoord(13, 6)).ToList(), "spawn_1", "base_2", "К второй"));
        sc.Waves.Add(Wave(count: 2));
        Func<TriggerActionDef[], double, TriggerDef> timer = (acts, t) =>
        {
            var tr = new TriggerDef { Name = "t" + t, Event = ScenarioEventType.Timer, Interval = t, Condition = new ConditionDef { Type = ConditionType.Always } };
            tr.Actions.AddRange(acts);
            return tr;
        };
        sc.Triggers.Add(timer(new[] { new TriggerActionDef { Type = TriggerActionType.SetGroupRoute, Group = "g", Route = "r2" } }, 3));
        sc.Triggers.Add(timer(new[] { new TriggerActionDef { Type = TriggerActionType.SetObjectState, Object = "base_2", ObjectState = ObjectStateChange.Destroy } }, 4));
        sc.Triggers.Add(timer(new[] { new TriggerActionDef { Type = TriggerActionType.SetTarget, Target = ActionTarget.Enemies, Object = "base_1" } }, 5));
        sc.Triggers.Add(timer(new[] { new TriggerActionDef { Type = TriggerActionType.EndScenario, Win = true, Text = "держались" } }, 7));
        sc.Triggers.Add(new TriggerDef
        {
            Name = "конец", Event = ScenarioEventType.BattleEnded, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.LogDiagnostic, Text = "бой окончен" } },
        });
        sc.Triggers.Add(new TriggerDef
        {
            Name = "база пала", Event = ScenarioEventType.BaseDestroyed, EventObject = "base_2", Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "b2" } },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(3.2);
        Assert.All(sim.Units.Where(u => u.Group == "g"), u => Assert.Equal("r2", u.RouteId));
        Assert.Equal("r2", sim.Scenario.GroupRoute("g"));
        sim.RunSeconds(1);
        Assert.False(sim.Structures.Of("base_2")!.Alive);
        sim.RunSeconds(0.2);
        Assert.Contains("b2", sim.Scenario.Flags);
        sim.RunSeconds(1);
        Assert.All(sim.Units.Where(u => u.Group == "g" && u.Alive), u => Assert.Equal("base_1", u.TargetObject));
        Assert.All(sim.Units.Where(u => u.Group == "g" && u.Alive), u => Assert.Null(u.Route));
        TestUtil.RunUntil(sim, () => sim.Outcome != BattleOutcome.None, 5);
        Assert.Equal(BattleOutcome.Victory, sim.Outcome);
        Assert.Equal("держались", sim.OutcomeText);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.Diagnostic && e.Text.Contains("бой окончен"));

        // Checks: a missing base, group and route are reported.
        sc.Triggers.Add(timer(new[]
        {
            new TriggerActionDef { Type = TriggerActionType.ActivateBase, Object = "nope" },
            new TriggerActionDef { Type = TriggerActionType.SetGroupRoute, Group = "ghost", Route = "r9" },
        }, 1));
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("базы спавна «nope» нет"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("маршрута 'r9'"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("группы «ghost» нет"));
    }

    // ------------------------------------------------------------------ shipped content

    [Fact]
    public void ShippedContent_TenProfiles_TdWithPlacement_AndFreeBattle()
    {
        var db = TestUtil.Shipped();
        var names = new[] { "Страж", "Защитник базы", "Стрелок", "Убийца", "Поддержка (автобой)", "Контролёр", "Охотник (по типам)", "Вожак", "Прорыватель", "Патрульный" };
        var profiles = db.All<AiProfileDef>().Where(p => p.Tags.Contains("autobattle")).ToList();
        Assert.Equal(names.OrderBy(n => n), profiles.Select(p => p.Name).OrderBy(n => n));
        Assert.Contains(profiles, p => p.Rules.Any(r => r.ElseDirective != AiDirective.None));
        Assert.Contains(profiles, p => p.Encounter != null);

        var td = db.Get<ScenarioDef>("td_pass_defense")!;
        Assert.Equal(BattleMode.TdPlacement, td.Mode);
        var sim = ScenarioSystem.Create(db, td, td.Seed);
        Assert.True(sim.Scenario.Preparing);
        Assert.Null(sim.Placement.CanPlace("knight", new HexCoord(11, 2)));
        var report = AutoBattle.Run(db, td, td.Seed, new AutoBattleOptions { MaxSeconds = 300 });
        Assert.NotEqual("не завершён", report.Outcome);
        Assert.Equal(4, report.WavesTotal);
        Assert.True(report.EnemiesKilledByDefenders > 0);

        var fb = db.Get<ScenarioDef>("free_skirmish")!;
        Assert.Equal(BattleMode.FreeBattle, fb.Mode);
        var r2 = AutoBattle.Run(db, fb, fb.Seed, new AutoBattleOptions { MaxSeconds = 120 });
        Assert.NotEqual("не завершён", r2.Outcome);
    }
}
