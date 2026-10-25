using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage C — «только маршрут» (ТЗ 17.7) and the choice of the final base by a group (ТЗ 8.3), docs/DECISIONS_5.md §86.</summary>
public class Part5RouteOnlyTests
{
    private static ContentDatabase Db(bool routeOnly)
    {
        var db = TestUtil.Minimal();
        db.Rules.Time.SimVersion = 2;
        db.Add(new AiProfileDef
        {
            Id = "wolf_ai", Name = "Волчий", RouteOnly = routeOnly,
            AltTargets = { new AltTargetDef { Kind = AltTargetKind.AttackDefenders }, new AltTargetDef { Kind = AltTargetKind.Stop } },
        });
        db.Add(new UnitDef
        {
            Id = "wolf", Name = "Волк", Faction = "enemy", BasicAttack = "hit", AiProfile = "wolf_ai",
            Stats = { [Stats.HpMax] = 30, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 4, [Stats.BaseDamage] = 3 },
        });
        return db;
    }

    /// <summary>A road along row 2 to a base; a soldier three rows below it, in plain sight.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Road(bool routeOnly, BattleMode mode = BattleMode.Normal, bool hunt = false)
    {
        var db = Db(routeOnly);
        var map = TestUtil.Field(20, 9);
        map.Id = "p5ro";
        db.Add(map);
        var sc = new ScenarioDef { Id = "p5ro", Name = "Только маршрут", Map = map.Id, Seed = 4, Mode = mode, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 2)));
        var b = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(18, 2));
        b.Health!.MaxHp = 300;
        sc.Objects.Add(b);
        sc.Routes.Add(RouteGraph.FromCells("r1", HexCoord.Line(new HexCoord(0, 2), new HexCoord(17, 2)).ToList(), "spawn_1", b.Id, "Дорога"));
        sc.Units.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(5, 5), Tag = "hero" });
        sc.Waves.Add(new WaveDef
        {
            Name = "Волна", Start = WaveStart.Time, Time = 0,
            Groups = { new SpawnGroupDef { Id = "g", Unit = "wolf", Count = 1, SpawnBase = "spawn_1", Route = "r1", Hunt = hunt } },
        });
        db.Add(sc);
        return (db, sc);
    }

    /// <summary>The farthest the wolf ever was from the road (row 2).</summary>
    private static int OffRoad(Simulation sim, double seconds)
    {
        int worst = 0;
        for (long i = 0; i < sim.TicksFor(seconds); i++)
        {
            sim.Step();
            if (sim.Units.FirstOrDefault(u => u.Group == "g" && u.Alive) is { } w) worst = System.Math.Max(worst, System.Math.Abs(w.Pos.R - 2));
        }
        return worst;
    }

    [Fact]
    public void RouteOnly_NeverLeavesTheRoadForAVisibleDefender_AnOrdinaryProfileDoes()
    {
        var (db, sc) = Road(routeOnly: false);
        Assert.True(OffRoad(ScenarioSystem.Create(db, sc, 1), 8) >= 2, "обычный профиль должен сойти с дороги к солдату");

        var (db2, sc2) = Road(routeOnly: true);
        var sim = ScenarioSystem.Create(db2, sc2, 1);
        Assert.Equal(0, OffRoad(sim, 12));
        // It reached its own base and attacks it.
        var b = sim.Structures.Of(sc2.Objects[1].Id)!;
        Assert.True(b.Hp < b.HpMax, "«только маршрут» должен дойти до своей цели и атаковать её");
    }

    [Fact]
    public void RouteOnly_IsStrongerThanTheModeAndTheGroup_AndSaysSo()
    {
        var (db, sc) = Road(routeOnly: true, BattleMode.Mixed, hunt: true);
        Assert.Contains(ScenarioModes.Check(db, sc), p => p.Severity == IssueSeverity.Warning && p.Message.Contains("только маршрут"));
        var sim = ScenarioSystem.Create(db, sc, 1);
        Assert.Equal(0, OffRoad(sim, 10));
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("«только маршрут» — на охоту не переключается"));

        var (db2, sc2) = Road(routeOnly: false, BattleMode.Mixed, hunt: true);
        Assert.True(OffRoad(ScenarioSystem.Create(db2, sc2, 1), 8) >= 2, "без «только маршрут» охотник идёт к солдату");
    }

    [Fact]
    public void RouteOnly_SkipsTheAlternative_AttackTheDefenders_WithAnEntry()
    {
        var (db, sc) = Road(routeOnly: true);
        sc.BaseDefeat.Mode = BaseDefeatMode.None; // the battle goes on without the base
        Assert.Contains(ContentValidator.Validate(db), i => i.Message.Contains("«Только маршрут»: альтернатива «атаковать защитников» пропускается"));
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(2);
        // The base falls: the alternatives of the profile are tried in order.
        var b = sim.Structures.Of(sc.Objects[1].Id)!;
        sim.Combat.Kill(b, null, "тест");
        Assert.False(b.Alive);
        sim.RunSeconds(4);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("«атаковать защитников» пропущено"));
        var w = sim.Units.First(u => u.Group == "g");
        Assert.Equal(AltTargetKind.Stop, w.Ai.Alternative);
    }

    // ------------------------------------------------------------------ the final base of a group (ТЗ 8.3)

    private static (ContentDatabase Db, ScenarioDef Sc, string North, string South) TwoBases(GroupRouteSelect select, int count = 6)
    {
        var db = Db(false);
        db.Get<AiProfileDef>("wolf_ai")!.RouteFocus = true;
        var map = TestUtil.Field(20, 11);
        map.Id = "p5bases";
        db.Add(map);
        var sc = new ScenarioDef { Id = "p5bases", Name = "Две базы", Map = map.Id, Seed = 9, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 5)));
        var north = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(18, 1));
        sc.Objects.Add(north);
        var south = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(18, 9));
        sc.Objects.Add(south);
        north.Health!.MaxHp = south.Health!.MaxHp = 1000;
        sc.Routes.Add(RouteGraph.FromCells("north", HexCoord.Line(new HexCoord(0, 5), new HexCoord(17, 1)).ToList(), "spawn_1", north.Id, "Север"));
        sc.Routes.Add(RouteGraph.FromCells("south", HexCoord.Line(new HexCoord(0, 5), new HexCoord(17, 9)).ToList(), "spawn_1", south.Id, "Юг"));
        sc.Waves.Add(new WaveDef
        {
            Name = "Волна", Start = WaveStart.Time, Time = 0,
            Groups = { new SpawnGroupDef { Id = "g", Unit = "wolf", Count = count, Interval = 0.5, SpawnBase = "spawn_1", Route = "north", AltRoutes = { "south" }, RouteSelect = select } },
        });
        db.Add(sc);
        return (db, sc, north.Id, south.Id);
    }

    private static List<string> Chosen(ContentDatabase db, ScenarioDef sc, System.Action<Simulation>? before = null)
    {
        var sim = ScenarioSystem.Create(db, sc, 1);
        before?.Invoke(sim);
        sim.RunSeconds(4);
        return sim.Units.Where(u => u.Group == "g").OrderBy(u => u.Id).Select(u => u.RouteId).ToList();
    }

    [Fact]
    public void TheFinalBase_Nearest_Shortest_Weakest_ByWeights()
    {
        var (db, sc, north, south) = TwoBases(GroupRouteSelect.NearestBase);
        Assert.All(Chosen(db, sc), r => Assert.Equal("north", r));
        // The north closed by a wall: the nearest reachable is the south.
        Assert.All(Chosen(db, sc, sim =>
        {
            var wall = db.Terrain("wall")!;
            foreach (var c in HexCoord.Ring(new HexCoord(17, 1), 1).Append(new HexCoord(17, 1)))
                if (sim.Map.Get(c) is { } cell) sim.CellEffects.ChangeTerrain(cell, wall, "тест");
        }), r => Assert.Equal("south", r));

        var (db2, sc2, _, _) = TwoBases(GroupRouteSelect.ShortestRoute);
        Assert.All(Chosen(db2, sc2), r => Assert.Equal("north", r));

        var (db3, sc3, _, s3) = TwoBases(GroupRouteSelect.LowestHp);
        var lowest = Chosen(db3, sc3, sim =>
        {
            var b = sim.Structures.Of(s3)!;
            sim.Damage.Apply(new DamageRequest { Target = b, Amount = 300, Cause = "тест" });
            Assert.True(b.Hp < b.HpMax);
        });
        Assert.All(lowest, r => Assert.Equal("south", r));

        var (db4, sc4, _, _) = TwoBases(GroupRouteSelect.Weighted, 12);
        sc4.Route("north")!.Weight = 0;
        Assert.All(Chosen(db4, sc4), r => Assert.Equal("south", r));
        sc4.Route("north")!.Weight = 1;
        var a = Chosen(db4, sc4);
        Assert.Equal(a, Chosen(db4, sc4));          // the same seed — the same choices
        Assert.Contains("north", a);
        Assert.Contains("south", a);
        sc4.Route("north")!.Weight = sc4.Route("south")!.Weight = 0;
        Assert.Contains(ScenarioObjectsCheck.All(db4, sc4, db4.Get<MapDef>(sc4.Map)!), p => p.Severity == IssueSeverity.Error && p.Message.Contains("вес 0"));
    }
}
