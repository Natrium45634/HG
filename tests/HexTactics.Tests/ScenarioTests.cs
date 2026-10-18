using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>A corridor map with an enemy spawn, a route to the defended cell and an exit marker.</summary>
internal static class ScenarioSetup
{
    public static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Rules.Autopause = new AutopauseRules { EnemySpotted = false, AllyCriticalDamage = false, UnitDied = false, PathUnavailable = false, WaveStarted = false };
        db.Add(new AiProfileDef { Id = "runner", Name = "Бегун", RouteFocus = true, DecisionInterval = 0.2, HazardAvoidance = 0 });
        db.Add(new UnitDef
        {
            Id = "runner", Name = "Бегун", Faction = "enemy", BasicAttack = "hit", AiProfile = "runner",
            Stats = { [Stats.HpMax] = 30, [Stats.ApMax] = 20, [Stats.ApRegen] = 10, [Stats.MoveSpeed] = 4, [Stats.DetectionRange] = 3 },
        });
        var map = TestUtil.Field(14, 5);
        map.Id = "corridor";
        map.Markers.Add(new MapMarkerDef { Id = "spawn", Type = MarkerType.EnemySpawn, C = new HexCoord(12, 2) });
        map.Markers.Add(new MapMarkerDef { Id = "spawn2", Type = MarkerType.EnemySpawn, C = new HexCoord(12, 0) });
        map.Markers.Add(new MapMarkerDef { Id = "exit", Type = MarkerType.Exit, C = new HexCoord(0, 4) });
        map.Markers.Add(new MapMarkerDef { Id = "zone", Type = MarkerType.Objective, C = new HexCoord(6, 4) });
        map.Markers.Add(new MapMarkerDef { Id = "gate", Type = MarkerType.Trigger, C = new HexCoord(3, 0) });
        map.Routes.Add(new MapRouteDef { Id = "main", Name = "Основной", Points = { new HexCoord(9, 2), new HexCoord(5, 2), new HexCoord(1, 2) } });
        db.Add(map);
        return db;
    }

    public static ScenarioDef Scenario(params ObjectiveDef[] objectives)
    {
        var sc = new ScenarioDef
        {
            Id = "test", Name = "Тест", Map = "corridor", Seed = 5,
            Units = { new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(0, 0), Facing = HexDirection.E, Tag = "hero" } },
            Victory = new(), Limits = new ScenarioLimitsDef { FogOfWar = false },
        };
        sc.Objectives.AddRange(objectives);
        return sc;
    }

    public static Simulation Sim(ContentDatabase db, ScenarioDef sc)
    {
        db.Add(sc);
        return ScenarioSystem.Create(db, sc);
    }

    public static WaveDef Wave(int count, double time = 0, string route = "main", double interval = 0) => new()
    {
        Name = "Волна", Time = time, SpawnMarker = "spawn", Route = route, Interval = interval, Faction = "enemy",
        Units = { new WaveUnitDef { Unit = "runner", Count = count } },
    };

    public static void KillWave(Simulation sim, int wave)
    {
        foreach (var u in sim.Units.Where(u => u.Alive && u.Wave == wave).ToList()) sim.Combat.Kill(u, null, "тест");
    }
}

/// <summary>TZ part 2 §16 test 10: objectives of different scenario types.</summary>
public class ObjectiveTests
{
    [Fact]
    public void Defend_CountsLeaks_OfWaveEnemiesWalkingTheRoute_AndFailsOverTheLimit()
    {
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario(new ObjectiveDef { Id = "hold", Name = "Удержать ворота", Kind = ObjectiveKind.Defend, Cells = { new HexCoord(1, 2) }, AllowedLeaks = 1 });
        sc.Waves.Add(ScenarioSetup.Wave(3, interval: 0.5));
        sc.Sides[1].Controller = SideController.Ai;
        var sim = ScenarioSetup.Sim(db, sc);
        var o = sim.Scenario.Objective("hold")!;
        TestUtil.RunUntil(sim, () => o.State is ObjectiveState.Completed or ObjectiveState.Failed || sim.Outcome != BattleOutcome.None, 40);
        Assert.Equal(ObjectiveState.Failed, o.State);
        Assert.Equal(2, o.Leaks);
        Assert.Equal(BattleOutcome.Defeat, sim.Outcome);
        Assert.All(sim.Units.Where(u => o.LeakedUnits.Contains(u.Id)), u => Assert.False(u.Alive)); // removed at the gate
    }

    [Fact]
    public void Defend_OnlyCountsTheGivenCategories_AndSucceedsWhenWavesAreRepelled()
    {
        var db = ScenarioSetup.Db();
        db.Unit("runner")!.CreatureCategory = "beast";
        var sc = ScenarioSetup.Scenario(new ObjectiveDef { Id = "hold", Kind = ObjectiveKind.Defend, Cells = { new HexCoord(1, 2) }, AllowedLeaks = 0, CountCategories = { "humanoid" } });
        sc.Waves.Add(ScenarioSetup.Wave(1));
        var sim = ScenarioSetup.Sim(db, sc);
        sim.RunSeconds(0.2);
        var runner = sim.Units.First(u => u.Def.Id == "runner");
        sim.Relocate(runner, new HexCoord(1, 2));
        sim.Step();
        Assert.Equal(0, sim.Scenario.Objective("hold")!.Leaks); // a beast does not count
        ScenarioSetup.KillWave(sim, 0);
        sim.RunSeconds(0.2);
        Assert.Equal(ObjectiveState.Completed, sim.Scenario.Objective("hold")!.State);
        Assert.Equal(BattleOutcome.Victory, sim.Outcome);
    }

    [Fact]
    public void Survive_Waves_AndAfterPreviousScheduling()
    {
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario(
            new ObjectiveDef { Id = "live", Kind = ObjectiveKind.Survive, Seconds = 3 },
            new ObjectiveDef { Id = "waves", Kind = ObjectiveKind.Waves });
        sc.Waves.Add(ScenarioSetup.Wave(2, time: 0.5));
        var w2 = ScenarioSetup.Wave(1);
        w2.Start = WaveStart.AfterPrevious;
        w2.Delay = 0.5;
        sc.Waves.Add(w2);
        var sim = ScenarioSetup.Sim(db, sc);
        sim.RunSeconds(1);
        Assert.Equal(WaveState.Active, sim.Scenario.Waves[0].State);
        Assert.Equal(WaveState.Pending, sim.Scenario.Waves[1].State);
        ScenarioSetup.KillWave(sim, 0);
        sim.Step();
        Assert.Equal(WaveState.Cleared, sim.Scenario.Waves[0].State);
        Assert.Equal(WaveState.Scheduled, sim.Scenario.Waves[1].State);
        TestUtil.RunUntil(sim, () => sim.Scenario.Waves[1].State == WaveState.Active, 20);
        ScenarioSetup.KillWave(sim, 1);
        sim.Step();
        Assert.Equal(ObjectiveState.Completed, sim.Scenario.Objective("waves")!.State);
        TestUtil.RunUntil(sim, () => sim.Outcome != BattleOutcome.None, 5);
        Assert.Equal(ObjectiveState.Completed, sim.Scenario.Objective("live")!.State);
        Assert.Equal(BattleOutcome.Victory, sim.Outcome);
    }

    [Fact]
    public void Boss_PhasesThroughConditionsAndActions_AndKillBossObjective()
    {
        var db = ScenarioSetup.Db();
        db.Add(new SkillDef { Id = "slam", Name = "Удар оземь", Targeting = TargetingMode.Self, Stages = { new SkillStageDef() } });
        db.Add(new StatusDef
        {
            Id = "enraged", Name = "Ярость", Duration = 0,
            Levels = { new StatusLevelDef { Threshold = 1, Defense = new DefenseDef { Modifiers = { new DamageModDef { Match = "*", Percent = -50 } } } } },
        });
        db.Add(new UnitDef
        {
            Id = "titan", Name = "Титан", Faction = "enemy", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 200, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 1, [Stats.DetectionRange] = 4 },
            Boss = new BossDef
            {
                IsBoss = true, Title = "Повелитель",
                Phases =
                {
                    new BossPhaseDef
                    {
                        Name = "Ярость", When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.5 },
                        Actions =
                        {
                            new TriggerActionDef { Type = TriggerActionType.AddSkill, Skill = "slam" },
                            new TriggerActionDef { Type = TriggerActionType.ApplyStatus, Status = "enraged" },
                            new TriggerActionDef { Type = TriggerActionType.SummonUnits, Unit = "dummy", Count = 2 },
                            new TriggerActionDef { Type = TriggerActionType.ChangeTerrain, Terrain = "mud", Radius = 1 },
                        },
                    },
                },
            },
        });
        var sc = ScenarioSetup.Scenario(new ObjectiveDef { Id = "boss", Kind = ObjectiveKind.KillBoss });
        sc.Units.Add(new ScenarioUnitDef { Unit = "titan", C = new HexCoord(8, 2), Facing = HexDirection.W, Tag = "boss" });
        var sim = ScenarioSetup.Sim(db, sc);
        var boss = sim.Units.First(u => u.Def.Id == "titan");
        sim.Combat.DealDamage(null, boss, 110, "physical", null, "тест");
        sim.Step();
        Assert.Equal("Ярость", sim.Scenario.PhaseOf(boss));
        Assert.True(boss.KnowsSkill("slam"));
        Assert.NotNull(boss.FindStatus("enraged"));
        Assert.Equal(-50, sim.Damage.ModifierPercent(boss, "physical"), 3); // phase resistances
        Assert.Equal(2, sim.Units.Count(u => u.Alive && u.SummonerId == boss.Id));
        Assert.Contains(sim.Map.InRadius(boss.Pos, 1), c => c.Terrain.Id == "mud");
        // The phase fires once.
        sim.Step();
        Assert.Equal(2, sim.Units.Count(u => u.SummonerId == boss.Id));
        Assert.Contains("здоровье босса", sim.Scenario.Objective("boss")!.Progress);
        sim.Combat.Kill(boss, null, "тест");
        sim.Step();
        Assert.Equal(ObjectiveState.Completed, sim.Scenario.Objective("boss")!.State);
    }

    [Fact]
    public void Escort_Capture_Destroy()
    {
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario(
            new ObjectiveDef { Id = "vip", Kind = ObjectiveKind.Escort, UnitTag = "hero", Marker = "exit" },
            new ObjectiveDef { Id = "zone", Kind = ObjectiveKind.Capture, Marker = "zone", Seconds = 2 },
            new ObjectiveDef { Id = "pylons", Kind = ObjectiveKind.Destroy, UnitTag = "pylon" });
        sc.Units.Add(new ScenarioUnitDef { Unit = "dummy", C = new HexCoord(10, 4), Tag = "pylon" });
        sc.Units.Add(new ScenarioUnitDef { Unit = "dummy", C = new HexCoord(11, 4), Tag = "pylon" });
        sc.Units.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(6, 3), Tag = "guard" });
        sc.Sides.RemoveAt(1); // passive targets: no AI moving the pylons
        var sim = ScenarioSetup.Sim(db, sc);
        var hero = sim.Units.First(u => u.Tag == "hero");
        var guard = sim.Units.First(u => u.Tag == "guard");
        // Capture: hold the zone 2 s; an enemy in the zone pauses the progress.
        sim.Relocate(guard, new HexCoord(6, 4));
        sim.RunSeconds(1);
        var pylon = sim.Units.First(u => u.Tag == "pylon");
        sim.Relocate(pylon, new HexCoord(6, 4));
        double held = sim.Scenario.Objective("zone")!.HoldSeconds;
        sim.RunSeconds(1);
        Assert.Equal(held, sim.Scenario.Objective("zone")!.HoldSeconds, 3);
        Assert.Contains("оспаривается", sim.Scenario.Objective("zone")!.Progress);
        sim.Relocate(pylon, new HexCoord(10, 4));
        TestUtil.RunUntil(sim, () => sim.Scenario.Objective("zone")!.State == ObjectiveState.Completed, 3);
        Assert.Equal(ObjectiveState.Completed, sim.Scenario.Objective("zone")!.State);
        // Destroy both pylons.
        foreach (var p in sim.Units.Where(u => u.Tag == "pylon").ToList()) sim.Combat.Kill(p, null, "тест");
        sim.Step();
        Assert.Equal(ObjectiveState.Completed, sim.Scenario.Objective("pylons")!.State);
        // Escort the hero to the exit → victory.
        Assert.Equal(BattleOutcome.None, sim.Outcome);
        sim.Relocate(hero, new HexCoord(0, 4));
        sim.Step();
        Assert.Equal(ObjectiveState.Completed, sim.Scenario.Objective("vip")!.State);
        Assert.Equal(BattleOutcome.Victory, sim.Outcome);
    }

    [Fact]
    public void Escort_FailsWhenTheEscortDies()
    {
        var db = ScenarioSetup.Db();
        var sim = ScenarioSetup.Sim(db, ScenarioSetup.Scenario(new ObjectiveDef { Id = "vip", Kind = ObjectiveKind.Escort, UnitTag = "hero", Marker = "exit" }));
        sim.Combat.Kill(sim.Units.First(u => u.Tag == "hero"), null, "тест");
        sim.Step();
        Assert.Equal(ObjectiveState.Failed, sim.Scenario.Objective("vip")!.State);
    }

    [Fact]
    public void Logic_AnyRequired_InOrder_Dependencies_Optional_Prevent_AndTimers()
    {
        // Any: one completed required objective wins.
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario(
            new ObjectiveDef { Id = "a", Kind = ObjectiveKind.Custom, Complete = new ConditionDef { Type = ConditionType.FlagSet, Flag = "a" } },
            new ObjectiveDef { Id = "b", Kind = ObjectiveKind.Custom, Complete = new ConditionDef { Type = ConditionType.FlagSet, Flag = "b" } });
        sc.ObjectiveLogic = ObjectiveLogic.AnyRequired;
        sc.Triggers.Add(new TriggerDef { Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 1 }, Actions = { new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "b" } } });
        var sim = ScenarioSetup.Sim(db, sc);
        sim.RunSeconds(1.2);
        Assert.Equal(BattleOutcome.Victory, sim.Outcome);

        // In order: the second objective is locked until the first is done; dependencies; an optional objective does not block.
        var db2 = ScenarioSetup.Db();
        var sc2 = ScenarioSetup.Scenario(
            new ObjectiveDef { Id = "first", Kind = ObjectiveKind.Custom, Complete = new ConditionDef { Type = ConditionType.FlagSet, Flag = "x" } },
            new ObjectiveDef { Id = "second", Kind = ObjectiveKind.Custom, Priority = 1, Complete = new ConditionDef { Type = ConditionType.Always } },
            new ObjectiveDef { Id = "bonus", Kind = ObjectiveKind.Custom, Required = false, DependsOn = { "first" }, Complete = new ConditionDef { Type = ConditionType.FlagSet, Flag = "never" } });
        sc2.ObjectiveLogic = ObjectiveLogic.InOrder;
        var sim2 = ScenarioSetup.Sim(db2, sc2);
        sim2.RunSeconds(0.5);
        Assert.Equal(ObjectiveState.Locked, sim2.Scenario.Objective("second")!.State);
        Assert.Equal(ObjectiveState.Locked, sim2.Scenario.Objective("bonus")!.State);
        sim2.Scenario.Execute(new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "x" }, "тест", null);
        sim2.RunSeconds(0.2);
        Assert.Equal(ObjectiveState.Completed, sim2.Scenario.Objective("second")!.State);
        Assert.Equal(ObjectiveState.Active, sim2.Scenario.Objective("bonus")!.State);
        Assert.Equal(BattleOutcome.Victory, sim2.Outcome);

        // Prevent an event within a time limit; a timer on a normal objective fails it.
        var db3 = ScenarioSetup.Db();
        var sc3 = ScenarioSetup.Scenario(
            new ObjectiveDef { Id = "quiet", Kind = ObjectiveKind.Prevent, TimeLimit = 2, Fail = new ConditionDef { Type = ConditionType.FlagSet, Flag = "alarm" } },
            new ObjectiveDef { Id = "late", Kind = ObjectiveKind.Custom, Required = false, TimeLimit = 1, Complete = new ConditionDef { Type = ConditionType.FlagSet, Flag = "never" } });
        var sim3 = ScenarioSetup.Sim(db3, sc3);
        sim3.RunSeconds(1.2);
        Assert.Equal(ObjectiveState.Failed, sim3.Scenario.Objective("late")!.State);
        Assert.Equal(BattleOutcome.None, sim3.Outcome);
        sim3.RunSeconds(1);
        Assert.Equal(ObjectiveState.Completed, sim3.Scenario.Objective("quiet")!.State);
        Assert.Equal(BattleOutcome.Victory, sim3.Outcome);

        var db4 = ScenarioSetup.Db();
        var sim4 = ScenarioSetup.Sim(db4, ScenarioSetup.Scenario(
            new ObjectiveDef { Id = "quiet", Kind = ObjectiveKind.Prevent, TimeLimit = 5, Fail = new ConditionDef { Type = ConditionType.FlagSet, Flag = "alarm" } }));
        sim4.Scenario.Execute(new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "alarm" }, "тест", null);
        sim4.Step();
        Assert.Equal(BattleOutcome.Defeat, sim4.Outcome);
    }

    [Fact]
    public void ConditionTree_And_Or_Not_InOrder_AreaTimer_AndKillsByCategory()
    {
        var db = ScenarioSetup.Db();
        var sim = ScenarioSetup.Sim(db, ScenarioSetup.Scenario());
        var s = sim.Scenario;
        var flagA = new ConditionDef { Type = ConditionType.FlagSet, Flag = "a" };
        var flagB = new ConditionDef { Type = ConditionType.FlagSet, Flag = "b" };
        var and = new ConditionDef { Type = ConditionType.All, Children = { flagA, flagB } };
        var or = new ConditionDef { Type = ConditionType.Any, Children = { flagA, flagB } };
        var not = new ConditionDef { Type = ConditionType.Not, Children = { flagA } };
        var order = new ConditionDef { Type = ConditionType.InOrder, Children = { new ConditionDef { Type = ConditionType.FlagSet, Flag = "b" }, new ConditionDef { Type = ConditionType.FlagSet, Flag = "a" } } };
        Assert.False(s.Evaluate(and));
        Assert.False(s.Evaluate(or));
        Assert.True(s.Evaluate(not));
        s.Execute(new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "a" }, "т", null);
        Assert.True(s.Evaluate(or));
        Assert.False(s.Evaluate(not));
        Assert.False(s.Evaluate(order)); // a came before b: the order is b → a
        s.Execute(new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "b" }, "т", null);
        Assert.True(s.Evaluate(and));
        Assert.True(s.Evaluate(order));

        // A player unit must stay in the area for 1 s.
        var area = new ConditionDef { Type = ConditionType.UnitInArea, Marker = "gate", Side = AreaSide.Player, Seconds = 1 };
        var hero = sim.Units.First(u => u.Tag == "hero");
        sim.Relocate(hero, new HexCoord(3, 0));
        Assert.False(s.Evaluate(area));
        for (int i = 0; i < sim.TicksFor(1); i++)
        {
            sim.Step();
            s.Evaluate(area);
        }
        Assert.True(s.Evaluate(area));

        // Kills by category.
        var beast = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(2, 0), HexDirection.W);
        sim.Combat.Kill(beast, hero, "тест");
        Assert.True(s.Evaluate(new ConditionDef { Type = ConditionType.KillCount, Count = 1, Categories = { "humanoid" } }));
        Assert.False(s.Evaluate(new ConditionDef { Type = ConditionType.KillCount, Count = 1, Categories = { "beast" } }));
    }

    [Fact]
    public void Waves_Interval_MaxAlive_ConditionStart_Cancel_AndActions()
    {
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario();
        var w = ScenarioSetup.Wave(4, time: 1, interval: 1);
        w.MaxAlive = 2;
        w.OnStart.Add(new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "started" });
        w.OnEnd.Add(new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "ended" });
        sc.Waves.Add(w);
        var gate = ScenarioSetup.Wave(1);
        gate.Start = WaveStart.OnCondition;
        gate.StartCondition = new ConditionDef { Type = ConditionType.UnitInArea, Marker = "gate", Side = AreaSide.Player, Seconds = 0 };
        sc.Waves.Add(gate);
        var manual = ScenarioSetup.Wave(1);
        manual.Start = WaveStart.Manual;
        sc.Waves.Add(manual);
        var sim = ScenarioSetup.Sim(db, sc);
        var s = sim.Scenario;
        sim.RunSeconds(1.05);
        Assert.Equal(1, s.Waves[0].Spawned);
        Assert.Contains("started", s.Flags);
        sim.RunSeconds(1);
        Assert.Equal(2, s.Waves[0].Spawned);
        sim.RunSeconds(2);
        Assert.Equal(2, s.Waves[0].Spawned); // the alive limit holds the rest
        ScenarioSetup.KillWave(sim, 0);
        TestUtil.RunUntil(sim, () => s.Waves[0].Spawned == 4, 20);
        ScenarioSetup.KillWave(sim, 0);
        sim.Step();
        Assert.Equal(WaveState.Cleared, s.Waves[0].State);
        Assert.Contains("ended", s.Flags);

        // Start by a condition: a player unit enters the gate area; the spawn waits for the warning line.
        Assert.Equal(WaveState.Pending, s.Waves[1].State);
        sim.Relocate(sim.Units.First(u => u.Tag == "hero"), new HexCoord(3, 0));
        sim.Step();
        Assert.Equal(WaveState.Scheduled, s.Waves[1].State);
        var p = s.Waves[1].Planned[0];
        var warn = s.Warnings.OfSpawn(p.Id)!;
        Assert.True(p.Tick > sim.Tick + sim.TicksFor(db.Rules.SpawnWarnings.MinSeconds), "появление отложено на время предупреждения");
        Assert.True(warn.StartTick <= sim.Tick + 1);

        // A manual wave is cancelled: its planned appearance and line disappear.
        s.StartWave(2);
        Assert.Equal(WaveState.Scheduled, s.Waves[2].State);
        var mp = s.Waves[2].Planned[0];
        Assert.NotNull(s.Warnings.OfSpawn(mp.Id));
        s.CancelWave(2);
        Assert.Null(s.Warnings.OfSpawn(mp.Id));
        sim.RunSeconds(10);
        Assert.Equal(0, sim.Units.Count(u => u.Wave == 2));
    }

    [Fact]
    public void Validator_ChecksObjectivesWavesAndConditions()
    {
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario(
            new ObjectiveDef { Id = "x", Kind = ObjectiveKind.Defend },
            new ObjectiveDef { Id = "x", Kind = ObjectiveKind.Custom, DependsOn = { "nope" }, Complete = new ConditionDef { Type = ConditionType.ObjectiveCompleted, Objective = "ghost" } });
        sc.Waves.Add(ScenarioSetup.Wave(1, route: "nowhere"));
        db.Add(sc);
        var issues = ContentValidator.Validate(db).Where(i => i.Source == "Scenario:test" && i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();
        Assert.Contains(issues, m => m.Contains("повторяется"));
        Assert.Contains(issues, m => m.Contains("'nope'"));
        Assert.Contains(issues, m => m.Contains("'ghost'"));
        Assert.Contains(issues, m => m.Contains("Маршрут 'nowhere'"));
        Assert.Contains(issues, m => m.Contains("гексы цели"));
    }
}

/// <summary>TZ part 2 §16 tests 11–12: spawn warning lines.</summary>
public class SpawnWarningTests
{
    private static (Simulation Sim, PlannedSpawn Spawn, SpawnWarning Warn) Planned(ContentDatabase db, double time = 20, System.Action<ScenarioDef>? edit = null)
    {
        var sc = ScenarioSetup.Scenario();
        sc.Waves.Add(ScenarioSetup.Wave(1, time: time));
        edit?.Invoke(sc);
        var sim = ScenarioSetup.Sim(db, sc);
        var p = sim.Scenario.Waves[0].Planned[0];
        return (sim, p, sim.Scenario.Warnings.OfSpawn(p.Id)!);
    }

    [Fact]
    public void Head_FollowsThePath_WithTheEnemyStepTiming_AndVanishesOneSecondBeforeTheSpawn()
    {
        var db = ScenarioSetup.Db();
        var (sim, p, w) = Planned(db);
        var ws = sim.Scenario.Warnings;
        // The path follows the route; step times are cost / move speed of this enemy.
        Assert.Equal(new HexCoord(12, 2), w.Path[0]);
        Assert.Equal(new HexCoord(1, 2), w.Path[^1]);
        Assert.Contains(new HexCoord(5, 2), w.Path);
        double step = sim.TicksFor(1.0 / 4) * sim.Dt; // grass cost 1, speed 4
        Assert.Equal(step * (w.Path.Count - 1), w.Duration, 3);
        // Timing: start = spawn − max(T, min) − 1 s, vanish = spawn − 1 s.
        Assert.Equal(p.Tick - sim.TicksFor(1), w.VanishTick);
        Assert.Equal(w.VanishTick - sim.TicksFor(System.Math.Max(w.Duration, db.Rules.SpawnWarnings.MinSeconds)), w.StartTick);
        // The head reaches cell k at Times[k].
        for (int k = 1; k < w.Path.Count; k += 3)
            Assert.Equal(k, ws.HeadCells(w, w.StartTick + w.Times[k] / sim.Dt), 2);
        Assert.False(ws.IsActive(w, w.StartTick - 1));
        Assert.True(ws.IsActive(w, w.StartTick));
        Assert.True(ws.IsActive(w, w.VanishTick - 1));
        Assert.False(ws.IsActive(w, w.VanishTick)); // gone 1 s before the enemy appears
        // The enemy appears at the planned tick, the line is removed.
        TestUtil.RunUntil(sim, () => sim.Units.Any(u => u.Def.Id == "runner"), 30);
        Assert.Equal(p.Tick, sim.Tick); // appeared exactly when the simulation reached the planned tick
        Assert.Null(ws.OfSpawn(p.Id));
    }

    [Fact]
    public void LongPath_StartsEarlier_ShortPath_StaysUntilTheVanish()
    {
        var db = ScenarioSetup.Db();
        db.Unit("runner")!.Stats[Stats.MoveSpeed] = 0.5; // slow: T = 22 s > min 3 s
        var (sim, p, w) = Planned(db, time: 40);
        Assert.True(w.Duration > db.Rules.SpawnWarnings.MinSeconds);
        Assert.Equal(w.VanishTick - sim.TicksFor(w.Duration), w.StartTick);
        Assert.Equal(w.Path.Count - 1, sim.Scenario.Warnings.HeadCells(w, w.VanishTick - 1), 1); // arrives just before vanishing

        var db2 = ScenarioSetup.Db();
        db2.Rules.SpawnWarnings.MinSeconds = 10; // fast enemy, long minimum
        var (sim2, _, w2) = Planned(db2);
        Assert.True(w2.Duration < 10);
        Assert.Equal(w2.Path.Count - 1, sim2.Scenario.Warnings.HeadCells(w2, w2.VanishTick - 1), 3); // fully drawn and waiting
    }

    [Fact]
    public void RouteChange_UpdatesTheLine_AndCancelRemovesIt()
    {
        var db = ScenarioSetup.Db();
        var (sim, p, w) = Planned(db);
        int before = w.Path.Count;
        // A wall across row 2 between the route points: the path must go around.
        sim.CellEffects.ChangeTerrain(sim.Map.Get(new HexCoord(7, 2))!, db.Terrain("wall")!, "тест");
        sim.Step();
        var w2 = sim.Scenario.Warnings.OfSpawn(p.Id)!;
        Assert.DoesNotContain(new HexCoord(7, 2), w2.Path);
        Assert.True(w2.Path.Count >= before);
        sim.Scenario.CancelWave(0);
        Assert.Null(sim.Scenario.Warnings.OfSpawn(p.Id));
        Assert.Empty(sim.Scenario.Warnings.All);
    }

    [Fact]
    public void Pause_StopsTheLine_SpeedUpMovesItFaster()
    {
        var db = ScenarioSetup.Db();
        var (sim, _, w) = Planned(db, time: 6);
        var ws = sim.Scenario.Warnings;
        var driver = new SimDriver(sim);
        driver.Paused = false;
        driver.SetSpeedIndex(db.Rules.Time.SpeedPresets.IndexOf(1));
        while (sim.Tick < w.StartTick) driver.Advance(0.05);
        double h0 = ws.HeadCells(w, sim.Tick);
        driver.Paused = true;
        for (int i = 0; i < 20; i++) driver.Advance(0.1);
        Assert.Equal(h0, ws.HeadCells(w, sim.Tick), 6); // paused: the line does not move
        driver.Paused = false;
        long t0 = sim.Tick;
        driver.Advance(0.5);
        long normal = sim.Tick - t0;
        driver.SetSpeedIndex(db.Rules.Time.SpeedPresets.IndexOf(4));
        long t1 = sim.Tick;
        for (int i = 0; i < 10; i++) driver.Advance(0.05);
        long fast = sim.Tick - t1;
        Assert.Equal(normal * 4, fast); // the same real time — four times the simulation time (and line progress)
        Assert.True(ws.HeadCells(w, sim.Tick) > h0);
    }

    [Fact]
    public void FogModes_AndAllSwitches_NeverAffectTheSpawn()
    {
        var db = ScenarioSetup.Db();
        void Fog(ScenarioDef sc, WarningFog mode)
        {
            sc.Limits.FogOfWar = true;
            sc.SpawnWarnings.OverrideFog = true;
            sc.SpawnWarnings.Fog = mode;
        }
        var (sim, p, w) = Planned(db, 5, sc => Fog(sc, WarningFog.Hide));
        Assert.False(sim.Scenario.Warnings.VisibleToPlayer(w));
        TestUtil.RunUntil(sim, () => sim.Units.Any(u => u.Def.Id == "runner"), 10);
        Assert.Contains(sim.Units, u => u.Def.Id == "runner"); // hidden line, the enemy still appears

        var (sim2, _, w2) = Planned(ScenarioSetup.Db(), 5, sc => Fog(sc, WarningFog.AfterScouting));
        Assert.False(sim2.Scenario.Warnings.VisibleToPlayer(w2));
        sim2.Relocate(sim2.Units.First(u => u.Tag == "hero"), new HexCoord(10, 2)); // the spawn point gets scouted
        sim2.Vision.Recompute();
        Assert.True(sim2.Scenario.Warnings.VisibleToPlayer(w2));

        var (sim3, _, w3) = Planned(ScenarioSetup.Db(), 5, sc => Fog(sc, WarningFog.Partial));
        sim3.Vision.Recompute();
        Assert.True(sim3.Scenario.Warnings.VisibleToPlayer(w3));
        Assert.False(sim3.Scenario.Warnings.CellVisible(w3, new HexCoord(12, 2))); // not seen by the hero
        Assert.True(sim3.Scenario.Warnings.CellVisible(w3, new HexCoord(1, 2)));

        // Every switch hides the line: global, scenario, wave, unit type, spawn point.
        bool Enabled(System.Action<ContentDatabase, ScenarioDef> off)
        {
            var d = ScenarioSetup.Db();
            var (_, _, ww) = Planned(d, 5, sc => off(d, sc));
            return ww.Enabled;
        }
        Assert.True(Enabled((_, _) => { }));
        Assert.False(Enabled((d, _) => d.Rules.SpawnWarnings.Enabled = false));
        Assert.False(Enabled((_, sc) => sc.SpawnWarnings.Enabled = false));
        Assert.False(Enabled((_, sc) => sc.Waves[0].Warning = false));
        Assert.False(Enabled((d, _) => d.Unit("runner")!.SpawnWarning = false));
        Assert.False(Enabled((d, _) => d.Get<MapDef>("corridor")!.FindMarker("spawn")!.SpawnWarning = false));
    }
}
