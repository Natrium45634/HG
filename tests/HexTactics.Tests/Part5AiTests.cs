using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 5, stage C — AI on routes (docs/DECISIONS_5.md §86): strictness, «нет продвижения», the reactions to a blocked
/// way in order, nobody passes through another (T11).
/// </summary>
public class Part5AiTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Rules.Time.SimVersion = 2;
        db.Add(new AiProfileDef { Id = "runner", Name = "Бегущий к базе", RouteFocus = true });
        db.Add(new UnitDef
        {
            Id = "goblin", Name = "Гоблин", Faction = "enemy", BasicAttack = "hit", AiProfile = "runner",
            Stats = { [Stats.HpMax] = 40, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 4, [Stats.BaseDamage] = 5 },
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

    private static (ContentDatabase Db, ScenarioDef Sc, RouteDef Route) Strip(int count, double interval, int height = 9)
    {
        var db = Db();
        var map = TestUtil.Field(20, height);
        map.Id = "p5ai";
        db.Add(map);
        var sc = new ScenarioDef { Id = "p5ai", Name = "ИИ", Map = map.Id, Seed = 5, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 4)));
        var b = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(18, 4));
        b.Health!.MaxHp = 100000;
        sc.Objects.Add(b);
        var r = RouteGraph.FromCells("r1", Line((0, 4), (17, 4)), "spawn_1", "base_1", "Дорога");
        sc.Routes.Add(r);
        sc.Objects[0].Spawn!.AreaRadius = 2;
        sc.Waves.Add(new WaveDef
        {
            Name = "Поток", Start = WaveStart.Time, Time = 0,
            Groups = { new SpawnGroupDef { Id = "g", Unit = "goblin", Count = count, Interval = interval, SpawnBase = "spawn_1", Route = "r1" } },
        });
        db.Add(sc);
        return (db, sc, r);
    }

    /// <summary>Every tick: nobody moves more than one hex (no teleports, no passing through each other).</summary>
    private static void RunWatchingSteps(Simulation sim, double seconds)
    {
        var last = new Dictionary<int, HexCoord>();
        long n = sim.TicksFor(seconds);
        for (long i = 0; i < n; i++)
        {
            sim.Step();
            foreach (var u in sim.Units.Where(u => u.Alive && !u.IsStructure))
            {
                if (last.TryGetValue(u.Id, out var was)) Assert.True(HexCoord.Distance(was, u.Pos) <= 1, $"{u.Name} переместился с {was} на {u.Pos} за тик");
                last[u.Id] = u.Pos;
            }
            var taken = sim.Units.Where(u => u.Alive && !u.IsStructure).GroupBy(u => (u.Pos, u.Layer)).Where(g => g.Count() > 1).ToList();
            Assert.True(taken.Count == 0, "два юнита в одном гексе: " + string.Join(", ", taken.Select(g => g.Key.Pos)));
        }
    }

    [Fact]
    public void T11_HeadOnOnATwoWayLoop_TheLargerIdYields_AndEveryoneFinishes()
    {
        // The out-and-back loop over the same hexes that stalled forever before part 5 (docs/DECISIONS_5.md §85–86).
        var (db, sc, r) = Strip(6, 1);
        var a = RouteGraph.NodeOn(r, new HexCoord(4, 4))!;
        var b = new RouteNodeDef { Id = RouteGraph.NewNodeId(r), C = new HexCoord(6, 1) };
        r.Nodes.Add(b);
        r.Edges.Add(new RouteEdgeDef { Id = RouteGraph.NewEdgeId(r), From = a.Id, To = b.Id, Cells = HexCoord.Line(a.C, b.C).Skip(1).ToList(), Priority = 10 });
        var back = RouteGraph.AddBranch(r, b, HexCoord.Line(b.C, a.C));
        back.Loop!.MaxRepeats = 2;
        var sim = ScenarioSystem.Create(db, sc, 1);
        RunWatchingSteps(sim, 240);
        var gobs = sim.Units.Where(u => u.Group == "g").ToList();
        Assert.Equal(6, gobs.Count);
        Assert.All(gobs, u => Assert.True(u.Nav!.Finished || !u.Alive, $"{u.Name}#{u.Id} не завершил маршрут, стоит в {u.Pos}"));
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("уступает дорогу"));
        // Rules version 1 (packs of 1.0) keep the old behaviour: no reactions.
        db.Rules.Time.SimVersion = 1;
        var old = ScenarioSystem.Create(db, sc, 1);
        old.RunSeconds(60);
        Assert.DoesNotContain(old.Log.Entries, e => e.Text.Contains("уступает дорогу"));
    }

    [Fact]
    public void T11_APassageClosedInBattle_TheReactionsInOrder_LeadToTheFallbackRoute()
    {
        var (db, sc, r) = Strip(4, 1.5);
        // A detour line through row 7 to the same base.
        var alt = RouteGraph.FromCells("r2", Line((0, 4), (3, 7), (14, 7), (17, 4)), "spawn_1", "base_1", "Объезд");
        sc.Routes.Add(alt);
        r.Fallback = "r2";
        r.Strictness = RouteStrictness.StrictHexes;
        // The profile of the route: wait, then the fallback route.
        r.OnBlocked = new() { BlockedReaction.Wait, BlockedReaction.SwitchRoute, BlockedReaction.Abort };
        var sim = ClosedInBattle(db, sc);
        var gobs = sim.Units.Where(u => u.Group == "g").ToList();
        Assert.Equal(4, gobs.Count);
        Assert.True(sim.Log.Entries.Any(e => e.Text.Contains("запасной маршрут «Объезд»")), "никто не перешёл на запасной маршрут");
        // The order of the list: waiting is reported before the switch; nobody left the strict route on their own.
        int wait = sim.Log.Entries.ToList().FindIndex(e => e.Text.Contains("нет продвижения") && e.Text.Contains("ждёт"));
        int sw = sim.Log.Entries.ToList().FindIndex(e => e.Text.Contains("нет продвижения") && e.Text.Contains("запасной маршрут"));
        Assert.True(wait >= 0 && wait < sw, "реакции не по порядку");
        Assert.DoesNotContain(sim.Log.Entries, e => e.Text.Contains("обходит"));
        // Who switched walks the fallback; who saw the base on the way may go for it by the profile (no route then).
        Assert.Contains(gobs, u => u.Nav?.Route == "r2" && u.Ai.LastReaction == "SwitchRoute");
        var b = sim.Structures.Of("base_1")!;
        Assert.True(b.Hp < b.HpMax, "по объезду никто не дошёл");
    }

    [Fact]
    public void T11_TheDefaultList_WaitsThenStepsAside_AndSaysItLeavesTheStrictRoute()
    {
        var (db, sc, r) = Strip(4, 1.5);
        r.Strictness = RouteStrictness.StrictHexes;
        Assert.Equal(RouteDef.DefaultReactions, r.Reactions);
        var sim = ClosedInBattle(db, sc);
        var log = sim.Log.Entries.ToList();
        int wait = log.FindIndex(e => e.Text.Contains("нет продвижения") && e.Text.Contains("ждёт"));
        int detour = log.FindIndex(e => e.Text.Contains("нет продвижения") && e.Text.Contains("обходит"));
        Assert.True(wait >= 0 && wait < detour, "сначала ждёт, затем обходит");
        Assert.Contains("сходит со строгого маршрута", log[detour].Text);
        var b = sim.Structures.Of("base_1")!;
        Assert.True(b.Hp < b.HpMax, "обойдя стену, никто не дошёл");
    }

    /// <summary>Three seconds of the wave, then a wall across rows 0–6 at column 10 closes the road (row 7 stays open).</summary>
    private static Simulation ClosedInBattle(ContentDatabase db, ScenarioDef sc)
    {
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(3);
        var wall = db.Terrain("wall")!;
        for (int row = 0; row <= 6; row++)
            if (sim.Map.Get(HexLayout.FromOffset(10, row)) is { } cell) sim.CellEffects.ChangeTerrain(cell, wall, "тест");
        RunWatchingSteps(sim, 120);
        return sim;
    }

    [Fact]
    public void T11_NoAlternative_TheLastReactionStopsWithADiagnostic()
    {
        var (db, sc, r) = Strip(1, 1);
        r.OnBlocked = new() { BlockedReaction.Wait, BlockedReaction.Abort };
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(2);
        var wall = db.Terrain("wall")!;
        for (int row = 0; row < 9; row++)
            if (sim.Map.Get(HexLayout.FromOffset(10, row)) is { } cell) sim.CellEffects.ChangeTerrain(cell, wall, "тест");
        RunWatchingSteps(sim, 40);
        var g = sim.Units.Single(u => u.Group == "g");
        Assert.True(g.Ai.Aborted);
        Assert.Equal("Abort", g.Ai.LastReaction);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("маршрут непроходим") && e.Text.Contains("остановился"));
    }

    [Fact]
    public void Strictness_StrictKeepsToTheHexes_WaypointsGoStraight()
    {
        // A zigzag route: strict walks every drawn hex, «только узлы» cuts across.
        int Walked(RouteStrictness s, out bool offRoute)
        {
            var db = Db();
            var map = TestUtil.Field(20, 9);
            map.Id = "zig";
            db.Add(map);
            var sc = new ScenarioDef { Id = "zig", Name = "Зигзаг", Map = map.Id, Seed = 5, Victory = new(), Defeat = new() };
            sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 4)));
            sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(16, 4)));
            sc.Objects[1].Health!.MaxHp = 100000;
            var cells = Line((0, 4), (3, 1), (6, 4), (9, 1), (12, 4), (15, 4));
            var r = RouteGraph.FromCells("z", cells, "spawn_1", "base_1", "Зигзаг");
            r.Strictness = s;
            sc.Routes.Add(r);
            sc.Waves.Add(new WaveDef { Name = "В", Start = WaveStart.Time, Time = 0, Groups = { new SpawnGroupDef { Id = "g", Unit = "goblin", Count = 1, SpawnBase = "spawn_1", Route = "z" } } });
            db.Add(sc);
            var sim = ScenarioSystem.Create(db, sc, 1);
            var visited = new HashSet<HexCoord>();
            var allowed = cells.ToHashSet();
            offRoute = false;
            for (int i = 0; i < sim.TicksFor(40); i++)
            {
                sim.Step();
                var g = sim.Units.FirstOrDefault(u => u.Group == "g");
                if (g == null || !g.Alive) continue;
                visited.Add(g.Pos);
                offRoute |= !allowed.Contains(g.Pos);
            }
            return visited.Count;
        }
        int strict = Walked(RouteStrictness.StrictHexes, out bool strictOff);
        int nodes = Walked(RouteStrictness.Waypoints, out bool nodesOff);
        Assert.False(strictOff, "строгий режим сошёл с нарисованных гексов");
        Assert.True(nodes < strict, $"«только узлы» должен срезать зигзаг ({nodes} против {strict} гексов)");
    }
}
