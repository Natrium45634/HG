using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 4, stage 1: scenario objects, player and spawn bases, simple routes, the defeat by bases (docs/DECISIONS_4.md §56–58).</summary>
public class TdBaseTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new AiProfileDef { Id = "runner", Name = "Бегущий к базе", RouteFocus = true });
        db.Add(new UnitDef
        {
            Id = "wolf", Name = "Волк", Faction = "enemy", BasicAttack = "hit", AiProfile = "runner",
            Stats = { [Stats.HpMax] = 50, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 3, [Stats.DetectionRange] = 6, [Stats.BaseDamage] = 40 },
        });
        return db;
    }

    /// <summary>A strip 16 × 5: a spawn base on the west, a player base of three hexes on the east, a straight route.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Strip(int wolves = 3)
    {
        var db = Db();
        var map = TestUtil.Field(16, 5);
        map.Id = "strip";
        db.Add(map);
        var sc = new ScenarioDef { Id = "td", Name = "TD", Map = "strip", Seed = 3, Victory = new(), Defeat = new() };
        var spawn = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 2));
        sc.Objects.Add(spawn);
        var b = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(14, 2));
        b.Footprint = new() { new HexCoord(1, 0), new HexCoord(0, 1) };
        sc.Objects.Add(b);
        var cells = Enumerable.Range(1, 13).Select(q => new HexCoord(q, 2)).ToList();
        var route = RouteGraph.FromCells(RouteGraph.NewId(sc), cells, spawn.Id, b.Id, "Основной");
        sc.Routes.Add(route);
        sc.Waves.Add(new WaveDef
        {
            Name = "Волки", Start = WaveStart.Time, Time = 1,
            Groups = { new SpawnGroupDef { Id = "g1", Unit = "wolf", Count = wolves, Interval = 1, SpawnBase = spawn.Id, Route = route.Id } },
        });
        db.Add(sc);
        return (db, sc);
    }

    [Fact]
    public void Base_OccupiesAllItsHexes_IsHitAtTheNearestHex()
    {
        var (db, sc) = Strip(0);
        var sim = ScenarioSystem.Create(db, sc, 1);
        var b = sim.Structures.Of("base_1")!;
        Assert.True(b.IsStructure);
        Assert.Equal(500, b.HpMax);
        Assert.Equal(3, b.Footprint!.Count);
        foreach (var c in b.Footprint) Assert.Contains(b, sim.Map.Get(c)!.Occupants);
        // Nobody walks onto a hex of the base.
        var mc = MoveContext.For(sim, db.Unit("wolf")!, "enemy");
        Assert.False(Occupancy.CanEnter(mc, sim.Map.Get(new HexCoord(15, 2))!));
        // A melee enemy next to the far hex (15,2) — two hexes from the anchor — can still hit the base.
        var wolf = sim.Spawn(db.Unit("wolf")!, "enemy", new HexCoord(15, 1), HexDirection.SW);
        Assert.Equal(1, StructureSystem.DistanceTo(wolf.Pos, b));
        Assert.Null(sim.Enqueue(wolf, Command.Attack(b.Id)));
        sim.RunSeconds(3);
        Assert.True(b.Hp < b.HpMax, "удар по ближайшему гексу базы должен пройти");
        // Structures are not pushed.
        sim.Combat.Displace(b, wolf.Pos, true, 3, wolf, "тест");
        Assert.Equal(new HexCoord(14, 2), b.Pos);
    }

    [Fact]
    public void Group_FollowsTheDrawnRoute_ReachesTheBase_DamagesIt_AndLeaves()
    {
        var (db, sc) = Strip(3);
        var route = sc.Routes[0];
        var allowed = new HashSet<HexCoord>(route.MainPath()) { new HexCoord(0, 2) };
        var sim = ScenarioSystem.Create(db, sc, 1);
        var b = sim.Structures.Of("base_1")!;
        var seen = new HashSet<int>();
        for (long i = 0; i < sim.TicksFor(40) && sim.Structures.Reached.GetValueOrDefault("base_1") < 3; i++)
        {
            sim.Step();
            foreach (var u in sim.Units.Where(u => u.Alive && u.Def.Id == "wolf"))
            {
                seen.Add(u.Id);
                Assert.Equal(route.Id, u.RouteId);
                Assert.Contains(u.Pos, allowed); // never off the drawn line
            }
        }
        Assert.Equal(3, seen.Count);
        Assert.Equal(3, sim.Structures.Reached["base_1"]);
        Assert.Equal(b.HpMax - 3 * 40, b.Hp, 6);
        Assert.All(sim.Units.Where(u => u.Def.Id == "wolf"), u => Assert.Equal(DeathCause.Removed, u.DeathCause));
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.GoalReached && e.Text.Contains("База игрока"));
    }

    [Fact]
    public void DefeatByBases_Any_All_Main_LostCount_AndIndestructible()
    {
        foreach (var mode in new[] { BaseDefeatMode.AnyBase, BaseDefeatMode.AllBases, BaseDefeatMode.MainBase, BaseDefeatMode.LostCount })
        {
            var (db, sc) = Strip(0);
            var second = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(10, 0));
            sc.Objects.Add(second);
            Assert.False(second.Health!.Main); // the first base is the main one
            sc.BaseDefeat = new BaseDefeatDef { Mode = mode, Count = 1 };
            var sim = ScenarioSystem.Create(db, sc, 1);
            sim.Structures.DamageStructure(sim.Structures.Of(second.Id)!, 10_000, null, "тест");
            sim.Step();
            bool lost = sim.Outcome == BattleOutcome.Defeat;
            Assert.Equal(mode is BaseDefeatMode.AnyBase or BaseDefeatMode.LostCount, lost);
            sim.Structures.DamageStructure(sim.Structures.Of("base_1")!, 10_000, null, "тест");
            sim.Step();
            Assert.Equal(BattleOutcome.Defeat, sim.Outcome);
            Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.StructureDestroyed);
        }
        var (db2, sc2) = Strip(0);
        sc2.Objects.First(o => o.Id == "base_1").Health!.Destructible = false;
        var sim2 = ScenarioSystem.Create(db2, sc2, 1);
        sim2.Structures.DamageStructure(sim2.Structures.Of("base_1")!, 10_000, null, "тест");
        sim2.Step();
        Assert.Equal(1, sim2.Structures.Of("base_1")!.Hp);
        Assert.Equal(BattleOutcome.None, sim2.Outcome);
    }

    [Fact]
    public void RouteChecks_Start_Gaps_Passability_Target_AndSeverity()
    {
        var (db, sc) = Strip(1);
        var map = db.Get<MapDef>("strip")!;
        Assert.DoesNotContain(ScenarioObjectsCheck.All(db, sc, map), p => p.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Source == "Scenario:td");

        RouteDef Route(IEnumerable<HexCoord> cells, string target = "base_1") => RouteGraph.FromCells("r", cells.ToList(), "spawn_1", target);
        IEnumerable<ScenarioProblem> Errors(RouteDef r) => ScenarioObjectsCheck.Route(db, sc, map, r).Where(p => p.Severity == IssueSeverity.Error);
        var line = Enumerable.Range(1, 13).Select(q => new HexCoord(q, 2)).ToList();

        Assert.Contains(Errors(Route(line.Skip(4))), p => p.Message.Contains("далеко от базы спавна"));
        Assert.Contains(Errors(Route(line.Where((_, i) => i != 5))), p => p.Message.Contains("разрыв"));
        Assert.Contains(Errors(Route(line.Take(8))), p => p.Message.Contains("не доходит до цели") && p.At == line[7]);
        var incomplete = Route(line.Take(8));
        incomplete.AllowIncomplete = true;
        Assert.Empty(Errors(incomplete));
        Assert.Contains(ScenarioObjectsCheck.Route(db, sc, map, incomplete), p => p.Severity == IssueSeverity.Warning && p.Message.Contains("не доходит"));
        Assert.Contains(Errors(Route(line, "")), p => p.Message.Contains("не завершён"));
        var patrol = Route(line.Take(5), "");
        patrol.End = RouteEndKind.Patrol;
        Assert.Empty(Errors(patrol));
        // A wall on the line — the reason and the hex are named.
        TestUtil.SetCell(map, line[6], "wall");
        var wall = Errors(Route(line)).Single(p => p.Message.Contains("непроходим"));
        Assert.Equal(line[6], wall.At);
        TestUtil.SetCell(map, line[6], "grass");
        // Through another structure.
        sc.Objects.Add(new ScenarioObjectDef { Id = "tower_1", Type = ScenarioObjectType.Defense, Anchor = line[3], Health = new StructureDef() });
        Assert.Contains(Errors(Route(line)), p => p.Message.Contains("проходит через"));
        // Objects: overlap and off-map.
        sc.Objects.Add(new ScenarioObjectDef { Id = "tower_2", Type = ScenarioObjectType.Defense, Anchor = line[3], Health = new StructureDef() });
        sc.Objects.Add(new ScenarioObjectDef { Id = "far", Type = ScenarioObjectType.Goal, Anchor = new HexCoord(40, 40), Health = new StructureDef() });
        var objs = ScenarioObjectsCheck.Objects(db, sc, map);
        Assert.Contains(objs, p => p.Severity == IssueSeverity.Error && p.Message.Contains("занимают один гекс"));
        Assert.Contains(objs, p => p.Severity == IssueSeverity.Error && p.Message.Contains("вне карты"));
        // Groups: missing template, route and base.
        sc.Waves[0].Groups.Add(new SpawnGroupDef { Id = "g2", Unit = "nobody", SpawnBase = "nowhere", Route = "none" });
        var groups = ScenarioObjectsCheck.Groups(db, sc);
        Assert.Contains(groups, p => p.Message.Contains("шаблон «nobody»"));
        Assert.Contains(groups, p => p.Message.Contains("база спавна «nowhere»"));
        Assert.Contains(groups, p => p.Message.Contains("маршрут «none»"));
    }

    [Fact]
    public void TakenSpawnHex_NearestFree_Wait_Cancel()
    {
        foreach (var policy in new[] { SpawnBlockedPolicy.NearestFree, SpawnBlockedPolicy.Wait, SpawnBlockedPolicy.Cancel })
        {
            var (db, sc) = Strip(1);
            sc.Objects.First(o => o.Id == "spawn_1").Spawn!.Blocked = policy;
            var sim = ScenarioSystem.Create(db, sc, 1);
            var blocker = sim.Spawn(db.Unit("soldier")!, "player", new HexCoord(0, 2), HexDirection.E);
            sim.RunSeconds(3);
            int wolves = sim.Units.Count(u => u.Def.Id == "wolf");
            switch (policy)
            {
                case SpawnBlockedPolicy.NearestFree:
                    Assert.Equal(1, wolves);
                    Assert.NotEqual(new HexCoord(0, 2), sim.Units.First(u => u.Def.Id == "wolf").Pos);
                    break;
                case SpawnBlockedPolicy.Wait:
                    Assert.Equal(0, wolves);
                    sim.Combat.Kill(blocker, null, "тест");
                    sim.RunSeconds(1);
                    Assert.Equal(1, sim.Units.Count(u => u.Def.Id == "wolf"));
                    break;
                case SpawnBlockedPolicy.Cancel:
                    Assert.Equal(0, wolves);
                    Assert.Contains(sim.Log.Entries, e => e.Text.Contains("выход занят"));
                    break;
            }
        }
    }

    [Fact]
    public void Duplicate_GetsANewId_KeepsReferences()
    {
        var (_, sc) = Strip(1);
        var spawn = sc.Object("spawn_1")!;
        spawn.Spawn!.MaxAlive = 7;
        var copy = ScenarioObjects.Duplicate(sc, spawn, new HexCoord(0, 4));
        Assert.NotEqual(spawn.Id, copy.Id);
        Assert.Equal(7, copy.Spawn!.MaxAlive);
        Assert.Equal(new HexCoord(0, 4), copy.Anchor);
        // Rotated footprint: the facing turns the offsets.
        var b = sc.Object("base_1")!;
        var east = b.Cells();
        b.Facing = HexDirection.W;
        var west = b.Cells();
        Assert.Equal(east.Count, west.Count);
        Assert.Contains(new HexCoord(13, 2), west);
    }
}
