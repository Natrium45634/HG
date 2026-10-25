using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 5, stage B — TD (docs/DECISIONS_5.md §85): the acceptance tests T06, T07, T09, T10, T13 and «появление окончено»
/// apart from «волна завершена».
/// </summary>
public class Part5TdTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new AiProfileDef { Id = "runner", Name = "Бегущий к базе", RouteFocus = true });
        foreach (var (id, name, hp) in new[] { ("wolf_basic", "Волк", 30.0), ("wolf_leader", "Волк-вожак", 80.0), ("goblin", "Гоблин", 40.0) })
            db.Add(new UnitDef
            {
                Id = id, Name = name, Faction = "enemy", BasicAttack = "hit", AiProfile = "runner",
                Stats = { [Stats.HpMax] = hp, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 4, [Stats.BaseDamage] = 5 },
            });
        return db;
    }

    private static List<HexCoord> Line(params (int Q, int R)[] pts)
    {
        var list = new List<HexCoord> { new(pts[0].Q, pts[0].R) };
        for (int i = 1; i < pts.Length; i++)
            list.AddRange(HexCoord.Line(new HexCoord(pts[i - 1].Q, pts[i - 1].R), new HexCoord(pts[i].Q, pts[i].R)).Skip(1));
        return list;
    }

    /// <summary>A strip 20 × 9 with one spawn base at (0,4) and a base at (18,4), a straight route between them.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc, RouteDef Route) Strip()
    {
        var db = Db();
        var map = TestUtil.Field(20, 9);
        map.Id = "p5_strip";
        db.Add(map);
        var sc = new ScenarioDef { Id = "p5", Name = "Ч5", Map = map.Id, Seed = 5, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 4)));
        var b = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(18, 4));
        b.Health!.MaxHp = 100000;
        sc.Objects.Add(b);
        var r = RouteGraph.FromCells("r1", Line((0, 4), (17, 4)), "spawn_1", "base_1", "Дорога");
        sc.Routes.Add(r);
        db.Add(sc);
        return (db, sc, r);
    }

    [Fact]
    public void T07_TwelveWolvesByTwoEvery40Ticks_PlusTheLeaderAfter180Ticks()
    {
        var (db, sc, _) = Strip();
        // ТЗ ticks → seconds (20 ticks/s): 40 ticks = 2 s, 180 ticks = 9 s.
        sc.Waves.Add(new WaveDef
        {
            Name = "Волна 2", Start = WaveStart.Time, Time = 1,
            Groups =
            {
                new SpawnGroupDef { Id = "wolves", Unit = "wolf_basic", Count = 12, SubgroupSize = 2, Interval = 0, SubgroupPause = 2, SpawnBase = "spawn_1", Route = "r1" },
                new SpawnGroupDef { Id = "leader", Unit = "wolf_leader", Count = 1, Delay = 9, SpawnBase = "spawn_1", Route = "r1" },
            },
        });
        sc.Objects[0].Spawn!.AreaRadius = 2;
        var sim = ScenarioSystem.Create(db, sc, 1);
        var wave = sim.Scenario.Waves[0];
        sim.RunSeconds(15);
        var wolves = wave.Planned.Where(p => p.Group == "wolves").OrderBy(p => p.SpawnedTick).ToList();
        Assert.Equal(12, wolves.Count);
        Assert.All(wolves, p => Assert.True(p.SpawnedTick >= 0, "волк не появился"));
        // Each one exactly when planned.
        Assert.All(wave.Planned, p => Assert.Equal(p.Tick, p.SpawnedTick));
        long t0 = wolves[0].SpawnedTick;
        Assert.Equal(sim.TicksFor(1), t0);
        // Six portions of two, 40 ticks apart, both wolves of a portion in the same tick.
        for (int b = 0; b < 6; b++)
        {
            Assert.Equal(t0 + 40 * b, wolves[2 * b].SpawnedTick);
            Assert.Equal(t0 + 40 * b, wolves[2 * b + 1].SpawnedTick);
        }
        var leader = wave.Planned.Single(p => p.Group == "leader");
        Assert.Equal(t0 + 180, leader.SpawnedTick);
        Assert.Equal(13, sim.Units.Count(u => u.Wave == 0));
        // The plan of the editor says the same.
        var plan = WavePlan.Of(db, sc)[0];
        Assert.Equal(13, plan.Spawns.Count);
        Assert.Equal(10, plan.Spawns.Where(s => s.Group == "wolves").Max(s => s.At), 6);
        Assert.Equal(9, plan.Spawns.Single(s => s.Group == "leader").At, 6);
    }

    [Fact]
    public void SpawnFinished_IsNotWaveEnded_AndAWaveCanFollowIt()
    {
        var (db, sc, _) = Strip();
        sc.Waves.Add(new WaveDef
        {
            Name = "Первая", Start = WaveStart.Time, Time = 0,
            Groups = { new SpawnGroupDef { Id = "a", Unit = "goblin", Count = 3, Interval = 1, SpawnBase = "spawn_1", Route = "r1" } },
        });
        sc.Waves.Add(new WaveDef
        {
            Name = "Вторая", Start = WaveStart.AfterPreviousSpawned, Delay = 1,
            Groups = { new SpawnGroupDef { Id = "b", Unit = "wolf_basic", Count = 1, SpawnBase = "spawn_1", Route = "r1" } },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        var first = sim.Scenario.Waves[0];
        var second = sim.Scenario.Waves[1];
        TestUtil.RunUntil(sim, () => first.SpawnFinishedTick >= 0, 10);
        Assert.True(first.SpawnFinishedTick >= 0);
        // All three are out and alive: the spawn is over, the wave is not.
        Assert.Equal(3, first.UnitIds.Count(id => sim.GetUnit(id)!.Alive));
        Assert.Equal(WaveState.Active, first.State);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("появление окончено"));
        // The second wave starts on «появление окончено» of the first (+1 s and the warning time), not on its clearing.
        TestUtil.RunUntil(sim, () => second.StartedTick >= 0, 20);
        Assert.True(second.StartedTick >= 0);
        Assert.True(second.StartedTick - first.SpawnFinishedTick >= sim.TicksFor(1));
        Assert.Equal(WaveState.Active, first.State);
        Assert.Contains("после появления предыдущей", WavePlan.Of(db, sc)[1].StartText);
    }

    private static (ContentDatabase Db, ScenarioDef Sc, Simulation Sim) DestroyedSpawner(SpawnerDestroyedPolicy policy)
    {
        var (db, sc, _) = Strip();
        var spawn = sc.Objects[0];
        spawn.Health = new StructureDef { MaxHp = 100, Destructible = true };
        spawn.Spawn!.OnDestroyed = policy;
        spawn.Spawn.AreaRadius = 2;
        // Portions of 3 every 3 s; the base dies in the middle of the second portion.
        sc.Waves.Add(new WaveDef
        {
            Name = "Поток", Start = WaveStart.Time, Time = 0,
            Groups = { new SpawnGroupDef { Id = "g", Unit = "goblin", Count = 9, SubgroupSize = 3, Interval = 0.5, SubgroupPause = 2, SpawnBase = "spawn_1", Route = "r1" } },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        var wave = sim.Scenario.Waves[0];
        TestUtil.RunUntil(sim, () => wave.Planned.Count(p => p.SpawnedTick >= 0) == 4, 20);
        var su = sim.Structures.Of(spawn.Id)!;
        sim.Structures.DamageStructure(su, 1000, null, "тест", force: true);
        Assert.False(su.Alive);
        sim.RunSeconds(20);
        return (db, sc, sim);
    }

    [Fact]
    public void T13_DestroyedSpawner_StopCompleteBatchOrContinue()
    {
        int Spawned(Simulation s) => s.Scenario.Waves[0].Planned.Count(p => p.SpawnedTick >= 0);
        // Stop: nobody else appears.
        var (_, _, stop) = DestroyedSpawner(SpawnerDestroyedPolicy.Stop);
        Assert.Equal(4, Spawned(stop));
        Assert.Contains(stop.Log.Entries, e => e.Text.Contains("разрушена — появление") && e.Text.Contains("остановить"));
        Assert.True(stop.Scenario.Waves[0].SpawnFinishedTick >= 0);
        // Complete the portion: the second portion (begun before the destruction) comes out whole, the third does not.
        var (_, _, batch) = DestroyedSpawner(SpawnerDestroyedPolicy.CompleteBatch);
        Assert.Equal(6, Spawned(batch));
        // Continue (as in 1.0): all nine.
        var (_, _, go) = DestroyedSpawner(SpawnerDestroyedPolicy.ContinueQueue);
        Assert.Equal(9, Spawned(go));
        // The check reminds that a destructible base continues by default.
        var (db, sc, _) = Strip();
        sc.Objects[0].Health = new StructureDef { MaxHp = 100, Destructible = true };
        Assert.Contains(ScenarioObjectsCheck.Objects(db, sc, db.Get<MapDef>(sc.Map)!), p => p.Message.Contains("продолжат появляться"));
    }

    private static (ContentDatabase Db, ScenarioDef Sc, RouteDef Route, RouteEdgeDef Back) Looped(int count, double interval)
    {
        var (db, sc, r) = Strip();
        // A ring walked one way (out by column 4 and row 1, back by the diagonal): no head-on meetings on it.
        var a = RouteGraph.NodeOn(r, new HexCoord(4, 4))!;
        var b = new RouteNodeDef { Id = RouteGraph.NewNodeId(r), C = new HexCoord(7, 1) };
        r.Nodes.Add(b);
        r.Edges.Add(new RouteEdgeDef { Id = RouteGraph.NewEdgeId(r), From = a.Id, To = b.Id, Cells = Line((4, 4), (4, 1), (7, 1)).Skip(1).ToList(), Priority = 10 });
        var back = RouteGraph.AddBranch(r, b, Line((7, 1), (4, 4)));
        sc.Waves.Add(new WaveDef
        {
            Name = "Петля", Start = WaveStart.Time, Time = 0,
            Groups = { new SpawnGroupDef { Id = "g", Unit = "goblin", Count = count, Interval = interval, SpawnBase = "spawn_1", Route = "r1" } },
        });
        sc.Objects[0].Spawn!.AreaRadius = 2;
        return (db, sc, r, back);
    }

    [Fact]
    public void T09_LoopTwoPasses_ThenTheBase_AndAChanceWithAGuaranteedCap()
    {
        // One enemy: exactly two passes, then the base.
        var (db, sc, r, back) = Looped(1, 1);
        back.Loop!.MaxRepeats = 2;
        var sim = ScenarioSystem.Create(db, sc, 1);
        var b = sim.Structures.Of("base_1")!;
        TestUtil.RunUntil(sim, () => b.Hp < b.HpMax, 120);
        Assert.Equal(2, sim.Units.Single(u => u.Group == "g").Nav!.Uses[back.Id]);
        Assert.True(b.Hp < b.HpMax);

        // A stream of six: every one finishes the route, none passes the loop more than twice.
        var (db1, sc1, _, back1) = Looped(6, 1);
        back1.Loop!.MaxRepeats = 2;
        var s1 = ScenarioSystem.Create(db1, sc1, 1);
        s1.RunSeconds(150);
        var gobs = s1.Units.Where(u => u.Group == "g").ToList();
        Assert.Equal(6, gobs.Count);
        Assert.All(gobs, u => Assert.True(u.Nav!.Finished || !u.Alive, "враг не завершил маршрут"));
        Assert.All(gobs, u => Assert.InRange(u.Nav!.Uses.GetValueOrDefault(back1.Id), 0, 2));

        // A chance to leave after each pass: some leave earlier, nobody passes more than the cap; the same seed repeats.
        int[] Passes(ulong seed)
        {
            var (db2, sc2, _, back2) = Looped(6, 8);
            back2.Loop!.MaxRepeats = 4;
            back2.Loop.ExitChance = 0.5;
            var s = ScenarioSystem.Create(db2, sc2, seed);
            s.RunSeconds(200);
            return s.Units.Where(u => u.Group == "g").OrderBy(u => u.Id).Select(u => u.Nav!.Uses.GetValueOrDefault(back2.Id)).ToArray();
        }
        var p1 = Passes(7);
        Assert.Equal(6, p1.Length);
        Assert.All(p1, n => Assert.InRange(n, 1, 4));
        Assert.Contains(p1, n => n < 4);
        Assert.Equal(p1, Passes(7));

        // A chance without a cap on a route that must reach the base is an error; with a time limit it is fine.
        var map = db.Get<MapDef>(sc.Map)!;
        back.Loop.MaxRepeats = 0;
        back.Loop.ExitChance = 0.3;
        Assert.Contains(ScenarioObjectsCheck.Route(db, sc, map, r), p => p.Severity == IssueSeverity.Error && p.Message.Contains("только по шансу"));
        back.Loop.MaxSeconds = 20;
        Assert.DoesNotContain(ScenarioObjectsCheck.Route(db, sc, map, r), p => p.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void T10_WithoutTheExitToTheBase_TheRunIsBlocked_AndTheRouteAndPlaceAreNamed()
    {
        var (db, sc, r) = Strip();
        sc.Waves.Add(new WaveDef { Name = "В", Start = WaveStart.Time, Time = 0, Groups = { new SpawnGroupDef { Id = "g", Unit = "goblin", Count = 1, SpawnBase = "spawn_1", Route = "r1" } } });
        Assert.DoesNotContain(ScenarioChecks.Run(db, sc), p => p.Severity == IssueSeverity.Error);
        // Cut the last edge: the route no longer reaches the base.
        var last = r.Edges.Single(e => r.Node(e.To)!.Kind == RouteNodeKind.End);
        r.Edges.Remove(last);
        var problems = ScenarioChecks.Run(db, sc);
        var err = problems.FirstOrDefault(p => p.Severity == IssueSeverity.Error && p.Object == "r1");
        Assert.NotNull(err);
        Assert.NotNull(err!.At);
    }

    [Fact]
    public void T06_TwoBases_ThreeSpawners_OneSharedLine_AllReachTheirOwnBase()
    {
        var db = Db();
        var map = TestUtil.Field(20, 11);
        map.Id = "p5_shared";
        db.Add(map);
        var sc = new ScenarioDef { Id = "p5s", Name = "Две базы", Map = map.Id, Seed = 5, Victory = new(), Defeat = new() };
        var s1 = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 2));
        var s2 = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 5));
        var s3 = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 8));
        var north = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(17, 2));
        var south = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(16, 8));
        foreach (var o in new[] { s1, s2, s3, north, south }) sc.Objects.Add(o);
        foreach (var b in new[] { north, south }) b.Health!.MaxHp = 100000;
        // Three spawners join one shared line in the middle (row 5), which forks to the two bases at the end.
        var main = RouteGraph.FromCells("shared", Line((0, 5), (12, 5), (16, 2)), s2.Id, north.Id, "Общая линия");
        var fork = RouteGraph.NodeOn(main, new HexCoord(12, 5))!;
        var toSouth = RouteGraph.AddBranch(main, fork, Line((12, 5), (15, 8)));
        sc.Routes.Add(main);
        var r1 = RouteGraph.FromCells("from_north", Line((0, 2), (4, 5)), s1.Id, north.Id, "Север → общая");
        var r3 = RouteGraph.FromCells("from_south", Line((0, 8), (3, 5)), s3.Id, south.Id, "Юг → общая");
        sc.Routes.Add(r1);
        sc.Routes.Add(r3);
        // Different schedules per spawner; the north line goes to the north base, the south one — to the south base.
        sc.Waves.Add(new WaveDef
        {
            Name = "Три спавнера", Start = WaveStart.Time, Time = 0,
            Groups =
            {
                new SpawnGroupDef { Id = "n", Unit = "goblin", Count = 2, Interval = 2, SpawnBase = s1.Id, Route = "shared", Target = north.Id },
                new SpawnGroupDef { Id = "m", Unit = "wolf_basic", Count = 2, Delay = 1, Interval = 3, SpawnBase = s2.Id, Route = "shared", Target = north.Id },
                new SpawnGroupDef { Id = "s", Unit = "goblin", Count = 2, Delay = 2, Interval = 2.5, SpawnBase = s3.Id, Route = "shared", Target = south.Id },
            },
        });
        // The fork sends by the target: the south group takes the southern exit.
        fork.Rule = RouteForkRule.Tags;
        toSouth.Tags.Add("s");
        db.Add(sc);
        var sim = ScenarioSystem.Create(db, sc, 3);
        var nb = sim.Structures.Of(north.Id)!;
        var sb = sim.Structures.Of(south.Id)!;
        sim.RunSeconds(60);
        var units = sim.Units.Where(u => u.Wave == 0).ToList();
        Assert.Equal(6, units.Count);
        Assert.True(nb.Hp < nb.HpMax, "до северной базы никто не дошёл");
        Assert.True(sb.Hp < sb.HpMax, "до южной базы никто не дошёл");
        Assert.All(units.Where(u => u.Group == "s"), u => Assert.Contains(toSouth.Id, u.Nav!.Taken));
        Assert.All(units.Where(u => u.Group != "s"), u => Assert.DoesNotContain(toSouth.Id, u.Nav!.Taken));
    }
}
