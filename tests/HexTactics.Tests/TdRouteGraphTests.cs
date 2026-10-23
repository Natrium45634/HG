using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 4, stage 3: the route graph — forks with rules, merges, crossings, loops with limits and exits, dwell and
/// node flags, fallback routes and switching, several routes of a group, the checks of the graph and after a map
/// change, the editor's operations on the graph (docs/DECISIONS_4.md §57).
/// </summary>
public class TdRouteGraphTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new AiProfileDef { Id = "runner", Name = "Бегущий к базе", RouteFocus = true });
        db.Add(new UnitDef
        {
            Id = "wolf", Name = "Волк", Faction = "enemy", BasicAttack = "hit", AiProfile = "runner", CreatureCategory = "beast", Tags = { "pack" },
            Stats = { [Stats.HpMax] = 50, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 4, [Stats.BaseDamage] = 10 },
        });
        db.Add(new UnitDef
        {
            Id = "goblin", Name = "Гоблин", Faction = "enemy", BasicAttack = "hit", AiProfile = "runner", CreatureCategory = "humanoid",
            Stats = { [Stats.HpMax] = 40, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 4, [Stats.BaseDamage] = 10 },
        });
        return db;
    }

    private static List<HexCoord> Line(params (int Q, int R)[] pts)
    {
        // Hex by hex between the given points (straight hex lines).
        var list = new List<HexCoord> { new(pts[0].Q, pts[0].R) };
        for (int i = 1; i < pts.Length; i++)
            list.AddRange(HexCoord.Line(new HexCoord(pts[i - 1].Q, pts[i - 1].R), new HexCoord(pts[i].Q, pts[i].R)).Skip(1));
        return list;
    }

    /// <summary>
    /// A field 16 × 9: a spawn base at (0,4), a base at (14,4). The main line goes along row 4 with a fork at (3,4)
    /// into a northern detour that merges back at (11,4).
    /// </summary>
    private static (ContentDatabase Db, ScenarioDef Sc, RouteDef Route) Forked()
    {
        var db = Db();
        var map = TestUtil.Field(16, 9);
        map.Id = "graph_field";
        db.Add(map);
        var sc = new ScenarioDef { Id = "graph", Name = "Граф", Map = map.Id, Seed = 3, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 4)));
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(14, 4)));
        var main = Line((0, 4), (13, 4));
        var r = RouteGraph.FromCells("r1", main, "spawn_1", "base_1", "Дорога");
        var fork = RouteGraph.NodeOn(r, new HexCoord(3, 4))!;
        var north = Line((3, 4), (5, 2), (9, 2), (11, 4));
        RouteGraph.AddBranch(r, fork, north);
        sc.Routes.Add(r);
        db.Add(sc);
        return (db, sc, r);
    }

    private static WaveDef Wave(string unit, int count, string route = "r1", double interval = 0.6) => new()
    {
        Name = "Волна", Start = WaveStart.Time, Time = 1,
        Groups = { new SpawnGroupDef { Id = "g", Unit = unit, Count = count, Interval = interval, SpawnBase = "spawn_1", Route = route } },
    };

    [Fact]
    public void Graph_Operations_SplitBranchMergeAndKinds()
    {
        var (_, _, r) = Forked();
        // The fork split the main edge; the branch ended on the main edge and split it too (a merge).
        Assert.Equal(4, r.Nodes.Count);
        Assert.Equal(4, r.Edges.Count);
        var fork = RouteGraph.NodeAt(r, new HexCoord(3, 4))!;
        var merge = RouteGraph.NodeAt(r, new HexCoord(11, 4))!;
        Assert.Equal(RouteNodeKind.Fork, fork.Kind);
        Assert.Equal(RouteNodeKind.Merge, merge.Kind);
        Assert.Equal(RouteNodeKind.Start, r.StartNode()!.Kind);
        Assert.Equal(RouteNodeKind.End, r.Nodes.Single(n => n.C == new HexCoord(13, 4)).Kind);
        Assert.Equal(2, r.Out(fork.Id).Count());
        Assert.Equal(2, r.In(merge.Id).Count());
        // The main line is unchanged hex by hex.
        Assert.Equal(Line((0, 4), (13, 4)), r.MainPath());
        Assert.Equal(r.Nodes.Select(n => n.Id), r.Reachable().OrderBy(id => r.Nodes.FindIndex(n => n.Id == id)));
        Assert.DoesNotContain(r.Edges, r.IsLoopEdge);
    }

    [Fact]
    public void Fork_RoundRobin_Tags_Weights_Load_PickExits()
    {
        var (db, sc, r) = Forked();
        var fork = RouteGraph.NodeAt(r, new HexCoord(3, 4))!;
        var northEdge = r.Out(fork.Id).Single(e => e.Cells.Contains(new HexCoord(5, 2)));
        bool North(Unit u) => u.Nav?.Taken.Contains(northEdge.Id) == true;

        fork.Rule = RouteForkRule.RoundRobin;
        sc.Waves.Add(Wave("wolf", 4));
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(12);
        var wolves = sim.Units.Where(u => u.Group == "g").ToList();
        Assert.Equal(4, wolves.Count);
        Assert.Equal(2, wolves.Count(North));

        // By tags: the northern exit is for the pack; goblins take the untagged one.
        northEdge.Tags.Add("pack");
        fork.Rule = RouteForkRule.Tags;
        sc.Waves.Clear();
        sc.Waves.Add(Wave("wolf", 2));
        sc.Waves[0].Groups.Add(new SpawnGroupDef { Id = "gob", Unit = "goblin", Count = 2, Interval = 0.6, Delay = 2, SpawnBase = "spawn_1", Route = "r1" });
        sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(12);
        Assert.All(sim.Units.Where(u => u.Def.Id == "wolf"), u => Assert.True(North(u)));
        Assert.All(sim.Units.Where(u => u.Def.Id == "goblin"), u => Assert.False(North(u)));

        // By weights: the battle's RNG — the same seed, the same split; a zero weight is never taken.
        fork.Rule = RouteForkRule.Weights;
        northEdge.Tags.Clear();
        foreach (var e in r.Out(fork.Id)) e.Weight = e == northEdge ? 0 : 1;
        sc.Waves.Clear();
        sc.Waves.Add(Wave("goblin", 5));
        sim = ScenarioSystem.Create(db, sc, 5);
        sim.RunSeconds(10);
        Assert.DoesNotContain(sim.Units, North);
        northEdge.Weight = 1;
        string Split(ulong seed)
        {
            var s2 = ScenarioSystem.Create(db, sc, seed);
            s2.RunSeconds(10);
            return string.Concat(s2.Units.Where(u => u.Group == "g").Select(u => North(u) ? "N" : "S"));
        }
        Assert.Equal(Split(9), Split(9));

        // By load: the next one goes where fewer enemies walk now.
        fork.Rule = RouteForkRule.Load;
        sc.Waves.Clear();
        sc.Waves.Add(Wave("goblin", 2, interval: 0.3));
        sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(4);
        var g2 = sim.Units.Where(u => u.Group == "g").ToList();
        Assert.Equal(1, g2.Count(North));
    }

    [Fact]
    public void Crossing_IsIndependent_RoutesShareHexes()
    {
        var (db, sc, r) = Forked();
        // A second route crossing the first at (6,4): nothing is created, both stay one line.
        var cross = RouteGraph.FromCells("r2", Line((6, 0), (6, 8)), "spawn_1", "", "Поперёк");
        cross.End = RouteEndKind.Hold;
        sc.Routes.Add(cross);
        Assert.Equal(2, cross.Nodes.Count);
        Assert.Equal(4, r.Nodes.Count);
        Assert.Contains(new HexCoord(6, 4), cross.AllCells());
        Assert.Contains(new HexCoord(6, 4), r.AllCells());
        Assert.DoesNotContain(ScenarioObjectsCheck.Route(db, sc, db.Get<MapDef>(sc.Map)!, r), p => p.Severity == IssueSeverity.Error);
    }

    /// <summary>A route with a loop: start → A → B → back to A (twice), then A → the end at the base.</summary>
    private static (ContentDatabase, ScenarioDef, RouteDef, RouteEdgeDef Loop) Looped(int repeats)
    {
        var db = Db();
        var map = TestUtil.Field(16, 9);
        map.Id = "loop_field";
        db.Add(map);
        var sc = new ScenarioDef { Id = "loop", Name = "Петля", Map = map.Id, Seed = 3, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 4)));
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(14, 4)));
        var r = RouteGraph.FromCells("r1", Line((0, 4), (13, 4)), "spawn_1", "base_1", "С петлёй");
        var a = RouteGraph.NodeOn(r, new HexCoord(4, 4))!;
        // A → B (north) → back to A: a loop.
        var b = new RouteNodeDef { Id = RouteGraph.NewNodeId(r), C = new HexCoord(6, 1) };
        r.Nodes.Add(b);
        r.Edges.Add(new RouteEdgeDef { Id = RouteGraph.NewEdgeId(r), From = a.Id, To = b.Id, Cells = HexCoord.Line(a.C, b.C).Skip(1).ToList(), Priority = 10 });
        var back = RouteGraph.AddBranch(r, b, HexCoord.Line(b.C, a.C));
        back.Loop!.MaxRepeats = repeats;
        sc.Routes.Add(r);
        sc.Waves.Add(Wave("goblin", 1));
        db.Add(sc);
        return (db, sc, r, back);
    }

    [Fact]
    public void Loop_RepeatsLimited_ThenLeavesToTheTarget()
    {
        var (db, sc, r, back) = Looped(2);
        Assert.True(r.IsLoopEdge(back));
        var sim = ScenarioSystem.Create(db, sc, 1);
        var b = sim.Structures.Of("base_1")!;
        TestUtil.RunUntil(sim, () => b.Hp < b.HpMax, 90);
        var gob = sim.Units.Single(u => u.Group == "g");
        Assert.Equal(2, gob.Nav!.Uses[back.Id]);
        Assert.True(b.Hp < b.HpMax, "после петли враг должен дойти до базы");
        Assert.DoesNotContain(ScenarioObjectsCheck.Route(db, sc, db.Get<MapDef>(sc.Map)!, r), p => p.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Loop_ExitCondition_AndInfinitePatrol()
    {
        var (db, sc, r, back) = Looped(0);
        // Without a limit and an exit it is an error unless the route is a patrol.
        var map = db.Get<MapDef>(sc.Map)!;
        Assert.Contains(ScenarioObjectsCheck.Route(db, sc, map, r), p => p.Severity == IssueSeverity.Error && p.Message.Contains("петля"));
        back.Loop!.ExitCondition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "leave" };
        Assert.DoesNotContain(ScenarioObjectsCheck.Route(db, sc, map, r), p => p.Severity == IssueSeverity.Error);
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(30);
        var gob = sim.Units.Single(u => u.Group == "g");
        int loops = gob.Nav!.Uses.GetValueOrDefault(back.Id);
        Assert.True(loops >= 2, $"петля должна повторяться до условия (повторов {loops})");
        sim.Scenario.SetFlag("leave");
        var b = sim.Structures.Of("base_1")!;
        TestUtil.RunUntil(sim, () => b.Hp < b.HpMax, 60);
        Assert.True(b.Hp < b.HpMax);
        Assert.InRange(gob.Nav.Uses[back.Id], loops, loops + 1);

        // A patrol without an end loops forever (allowed explicitly).
        var (db2, sc2, r2, back2) = Looped(0);
        r2.End = RouteEndKind.Patrol;
        Assert.DoesNotContain(ScenarioObjectsCheck.Route(db2, sc2, db2.Get<MapDef>(sc2.Map)!, r2), p => p.Severity == IssueSeverity.Error && p.Message.Contains("петля"));
    }

    [Fact]
    public void Node_Dwell_AndFlag_ReachTriggers()
    {
        var (db, sc, r) = Forked();
        var merge = RouteGraph.NodeAt(r, new HexCoord(11, 4))!;
        merge.DwellSeconds = 3;
        merge.Event = "at_gate";
        merge.Name = "Ворота";
        sc.Triggers.Add(new TriggerDef
        {
            Name = "У ворот", Once = true, Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "at_gate" },
            Actions = { new TriggerActionDef { Type = TriggerActionType.Message, Text = "Враг у ворот" } },
        });
        sc.Waves.Add(Wave("goblin", 1));
        var sim = ScenarioSystem.Create(db, sc, 1);
        TestUtil.RunUntil(sim, () => sim.Scenario.Flags.Contains("at_gate"), 30);
        var gob = sim.Units.Single(u => u.Group == "g");
        Assert.Equal(new HexCoord(11, 4), gob.Pos);
        long reached = sim.Tick;
        sim.RunSeconds(2.5);
        Assert.Equal(new HexCoord(11, 4), gob.Pos);
        Assert.Contains("стоит в узле «Ворота»", gob.Ai.Explanation);
        sim.RunSeconds(2);
        Assert.NotEqual(new HexCoord(11, 4), gob.Pos);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("Враг у ворот"));
        Assert.True(sim.Tick > reached);
    }

    [Fact]
    public void ClosedExits_UseTheFallbackRoute_OrWait_AndSwitchingStartsAtTheNearestNode()
    {
        var (db, sc, r) = Forked();
        var fork = RouteGraph.NodeAt(r, new HexCoord(3, 4))!;
        foreach (var e in r.Out(fork.Id)) e.Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "open" };
        // No fallback: the enemy waits in the fork and says why.
        sc.Waves.Add(Wave("goblin", 1));
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(8);
        var gob = sim.Units.Single(u => u.Group == "g");
        Assert.Equal(fork.C, gob.Pos);
        Assert.Contains("нет доступного выхода", gob.Ai.Explanation);
        sim.Scenario.SetFlag("open");
        sim.RunSeconds(2);
        Assert.NotEqual(fork.C, gob.Pos);

        // With a fallback route: the enemy switches to it from its nearest node.
        var alt = RouteGraph.FromCells("r_alt", Line((3, 4), (3, 7), (13, 7), (13, 5)), "spawn_1", "base_1", "Обход");
        sc.Routes.Add(alt);
        r.Fallback = "r_alt";
        sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(10);
        gob = sim.Units.Single(u => u.Group == "g");
        Assert.Equal("r_alt", gob.RouteId);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("переходит на «Обход»"));
        Assert.True(gob.Pos.R > 4, $"враг должен уйти по обходу, сейчас {gob.Pos}");

        // A passable hex on an exit becoming a wall closes that exit too (checked in battle).
        foreach (var e in r.Out(fork.Id)) e.Condition = null;
        var mainExit = r.Out(fork.Id).First(e => !e.Cells.Contains(new HexCoord(5, 2)));
        var sim2 = ScenarioSystem.Create(db, sc, 1);
        sim2.Map.Get(mainExit.Cells[1])!.Terrain = db.Terrain("wall")!;
        sim2.RunSeconds(8);
        var g2 = sim2.Units.Single(u => u.Group == "g");
        Assert.Equal("r1", g2.RouteId);
        Assert.DoesNotContain(mainExit.Id, g2.Nav!.Taken);
        Assert.Contains(r.Out(fork.Id).Single(e => e != mainExit).Id, g2.Nav.Taken);
    }

    [Fact]
    public void Group_SpreadsOverItsRoutes()
    {
        var (db, sc, r) = Forked();
        var alt = RouteGraph.FromCells("r_alt", Line((0, 4), (0, 7), (13, 7), (13, 5)), "spawn_1", "base_1", "Обход");
        sc.Routes.Add(alt);
        var w = Wave("goblin", 4);
        w.Groups[0].AltRoutes.Add("r_alt");
        w.Groups[0].RouteSelect = GroupRouteSelect.RoundRobin;
        sc.Waves.Add(w);
        var sim = ScenarioSystem.Create(db, sc, 1);
        Assert.Equal(new[] { "r1", "r_alt", "r1", "r_alt" }, sim.Scenario.Waves[0].Planned.Select(p => p.Route).ToArray());
        sim.RunSeconds(6);
        Assert.Equal(2, sim.Units.Count(u => u.RouteId == "r_alt"));
        Assert.DoesNotContain(ScenarioObjectsCheck.Groups(db, sc), p => p.Severity == IssueSeverity.Error);
        w.Groups[0].AltRoutes.Add("nope");
        Assert.Contains(ScenarioObjectsCheck.Groups(db, sc), p => p.Message.Contains("«nope» не найден"));
    }

    [Fact]
    public void GraphChecks_DeadEnds_Unreachable_Weights_Fallback_AndMapChanges()
    {
        var (db, sc, r) = Forked();
        var map = db.Get<MapDef>(sc.Map)!;
        List<ScenarioProblem> P() => ScenarioObjectsCheck.Route(db, sc, map, r);
        Assert.DoesNotContain(P(), p => p.Severity == IssueSeverity.Error);
        // A branch that ends nowhere.
        var fork = RouteGraph.NodeAt(r, new HexCoord(3, 4))!;
        var dead = RouteGraph.AddBranch(r, fork, Line((3, 4), (3, 8)));
        Assert.Contains(P(), p => p.Severity == IssueSeverity.Error && p.Message.Contains("обрывается") && p.At == new HexCoord(3, 8));
        r.Edges.Remove(dead);
        r.Nodes.RemoveAll(n => n.Id == dead.To);
        RouteGraph.UpdateKinds(r);
        // A node nobody reaches.
        r.Nodes.Add(new RouteNodeDef { Id = "lost", C = new HexCoord(8, 8) });
        Assert.Contains(P(), p => p.Severity == IssueSeverity.Warning && p.Message.Contains("недостижим"));
        r.Nodes.RemoveAll(n => n.Id == "lost");
        // Weights all zero at a weighted fork; a missing fallback.
        fork.Rule = RouteForkRule.Weights;
        foreach (var e in r.Out(fork.Id)) e.Weight = 0;
        r.Fallback = "missing";
        Assert.Contains(P(), p => p.Message.Contains("у всех выходов вес 0"));
        Assert.Contains(P(), p => p.Message.Contains("запасной маршрут «missing» не найден"));
        foreach (var e in r.Out(fork.Id)) e.Weight = 1;
        r.Fallback = "";
        // The map changes: a wall on the northern branch is found again with its hex.
        TestUtil.SetCell(map, new HexCoord(7, 2), "wall");
        Assert.Contains(P(), p => p.Severity == IssueSeverity.Error && p.At == new HexCoord(7, 2) && p.Message.Contains("непроходим"));
        // The whole content check (publication) shows it too.
        Assert.Contains(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Message.Contains("(7,2)"));
    }

    [Fact]
    public void Join_CopiesTheOtherRoute_FromItsNearestNode()
    {
        var (db, sc, r) = Forked();
        var a = RouteGraph.FromCells("ra", Line((5, 8), (8, 8)), "spawn_1", "", "Начало");
        var aEnd = a.Nodes.Single(n => n.Kind == RouteNodeKind.End);
        var bNode = RouteGraph.NodeOn(r, new HexCoord(8, 4))!;
        var joined = RouteGraph.Join(a, r, aEnd, bNode, Line((8, 8), (8, 4)));
        Assert.NotNull(joined);
        Assert.Equal("base_1", a.Target);
        // The joined route reaches the end of the other one hex by hex.
        var path = a.MainPath();
        Assert.Equal(new HexCoord(5, 8), path[0]);
        Assert.Equal(new HexCoord(13, 4), path[^1]);
        for (int i = 1; i < path.Count; i++) Assert.Equal(1, HexCoord.Distance(path[i - 1], path[i]));
        // The other route is not changed; the joined one has the copied fork (a reachable branch).
        Assert.Equal(5, r.Nodes.Count);
        Assert.Contains(a.Nodes, n => n.C == new HexCoord(11, 4) && n.Kind == RouteNodeKind.Merge || n.C == new HexCoord(13, 4));
    }
}
