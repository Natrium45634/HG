using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 3, stage 1: single-unit control in the core (docs/DECISIONS_3.md §39–§41).</summary>
public class ControlTests
{
    [Fact]
    public void ManualTurn_IsFreeAndInstant_ByDefault()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        u.Ap = 5;
        sim.Enqueue(u, Command.Turn(HexDirection.W));
        Assert.Equal(0, u.Queue.Items[0].EstimatedAp, 6);
        sim.Step();
        Assert.Equal(HexDirection.W, u.Facing);
        Assert.Equal(0, u.Queue.Count);
        Assert.Equal(5 + 0.05, u.Ap, 6); // only the regeneration of one tick
        Assert.True(u.BusyUntil <= sim.Tick);
    }

    [Fact]
    public void ManualTurn_DoesNotCancelRoute()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        sim.Enqueue(u, Command.Move(new HexCoord(5, 1)));
        sim.Enqueue(u, Command.Turn(HexDirection.NW));
        sim.Enqueue(u, Command.Move(new HexCoord(5, 3)));
        sim.RunSeconds(6);
        Assert.Equal(new HexCoord(5, 3), u.Pos);
        Assert.Equal(0, u.Queue.Count);
    }

    [Fact]
    public void RouteNode_ArrivalFacing_Wait_AndCondition()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        var node = Command.Move(new HexCoord(3, 1));
        node.ArrivalFacing = HexDirection.SW;
        node.WaitAfter = 1.0;
        node.Until = new ConditionDef { Type = ConditionType.FlagSet, Flag = "go" };
        sim.Enqueue(u, node);
        sim.Enqueue(u, Command.Move(new HexCoord(5, 1)));
        var est = sim.Planner.ProjectQueue(u);
        Assert.Equal(HexDirection.SW, est[0].EndFacing);
        Assert.True(est[0].Estimate.Seconds >= 1.0);

        sim.RunSeconds(3);
        Assert.Equal(new HexCoord(3, 1), u.Pos);
        Assert.Equal(HexDirection.SW, u.Facing);
        Assert.Equal(2, u.Queue.Count); // waits for the flag
        Assert.Equal(CommandStatus.Waiting, u.Queue.Items[0].Status);
        sim.Scenario.SetFlag("go");
        sim.RunSeconds(3);
        Assert.Equal(new HexCoord(5, 1), u.Pos);
    }

    [Fact]
    public void RouteNode_Wait_TakesTime()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        var node = Command.Move(new HexCoord(2, 1));
        node.WaitAfter = 2.0;
        sim.Enqueue(u, node);
        sim.Enqueue(u, Command.Move(new HexCoord(3, 1)));
        sim.RunSeconds(2.0);
        Assert.Equal(new HexCoord(2, 1), u.Pos);
        sim.RunSeconds(1.5);
        Assert.Equal(new HexCoord(3, 1), u.Pos);
    }

    [Fact]
    public void OptionalNode_NextToOccupiedTarget_Counts_UnreachableIsSkipped()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field();
        // (9,5) is enclosed by walls: not even a neighbouring hex can be reached.
        for (int d = 0; d < 6; d++) TestUtil.SetCell(map, new HexCoord(9, 5) + ((HexDirection)d).Offset(), "wall");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        sim.Spawn(db.Unit("soldier")!, null, new HexCoord(4, 1), HexDirection.E); // idle ally on the node
        var occupied = Command.Move(new HexCoord(4, 1));
        occupied.Optional = true;
        var wall = Command.Move(new HexCoord(9, 5));
        wall.Optional = true;
        sim.Enqueue(u, occupied);
        sim.Enqueue(u, wall);
        sim.Enqueue(u, Command.Move(new HexCoord(6, 3)));
        sim.RunSeconds(15);
        Assert.True(u.Pos == new HexCoord(6, 3), string.Join("\n", sim.Log.Entries.Where(e => e.SourceUnit == u.Id).Select(e => e.Text)));
        Assert.True(sim.Log.Entries.Any(e => e.Text.Contains("необязательная точка пропущена")) && sim.Log.Entries.Any(e => e.Text.Contains("достаточно рядом")),
            string.Join("\n", sim.Log.Entries.Where(e => e.SourceUnit == u.Id).Select(e => e.Text)));
    }

    [Fact]
    public void ClearPending_KeepsTheExecutingOrder()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        sim.Enqueue(u, Command.Move(new HexCoord(5, 0)));
        sim.Enqueue(u, Command.Turn(HexDirection.W));
        sim.Enqueue(u, Command.Move(new HexCoord(5, 3)));
        sim.Step();
        Assert.Equal(CommandStatus.Active, u.Queue.Items[0].Status);
        Assert.Equal(2, sim.ClearPending(u));
        Assert.Single(u.Queue.Items);
        Assert.Equal(CommandType.Move, u.Queue.Items[0].Type);
        sim.RunSeconds(5);
        Assert.Equal(new HexCoord(5, 0), u.Pos);

        // Nothing started yet (paused planning): every order is pending and goes away.
        sim.Enqueue(u, Command.Move(new HexCoord(2, 0)));
        Assert.Equal(1, sim.ClearPending(u));
        Assert.Equal(0, u.Queue.Count);
    }

    [Fact]
    public void Routing_MinCost_MinDistance_AvoidDanger()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field(12, 7);
        // A mud band across the direct line: cheapest route goes around, shortest goes through.
        db.Get<TerrainDef>("mud")!.MoveCost = 5;
        for (int r = 0; r <= 4; r++) TestUtil.SetCell(map, new HexCoord(5, r), "mud");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var to = new HexCoord(8, 2);
        PathResult Route(RouteMode m) => sim.Planner.Route(u, new Command { Type = CommandType.Move, TargetCell = to, Routing = m }, u.Pos, u.Facing, to);

        var cost = Route(RouteMode.MinCost);
        var dist = Route(RouteMode.MinDistance);
        Assert.True(cost.Found && dist.Found);
        Assert.Equal(6, dist.Path.Count);
        Assert.Contains(dist.Path, c => map.Cells.First(x => x.C == c).T == "mud");
        Assert.DoesNotContain(cost.Path, c => map.Cells.First(x => x.C == c).T == "mud");
        Assert.True(cost.Cost < dist.Cost);

        // A hazard on the cheap route: «avoid danger» goes around it, «min cost» does not mind (same cost).
        var fire = new CellEffectDef { Id = "fire_t", Name = "Огонь", Hazard = true, Duration = 100 };
        db.Add(fire);
        foreach (var c in cost.Path.Take(cost.Path.Count - 1)) sim.CellEffects.Add(sim.Map.Get(c)!, fire, 1, 100, -1, 0, 0);
        var safe = Route(RouteMode.AvoidDanger);
        Assert.True(safe.Found);
        Assert.True(safe.Path.Count(c => sim.Map.Get(c)!.IsHazard) < cost.Path.Count(c => sim.Map.Get(c)!.IsHazard));
    }

    [Fact]
    public void Routing_EqualCost_PrefersFewerTurns()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        var to = new HexCoord(3, 3);
        var r = sim.Planner.Route(u, new Command { Type = CommandType.Move, TargetCell = to, Routing = RouteMode.MinCost }, u.Pos, u.Facing, to);
        Assert.True(r.Found);
        Assert.Equal(6, r.Path.Count);
        Assert.Equal(1, r.Turns); // E, E, E, SE, SE, SE
        Assert.Equal(new HexCoord(3, 0), r.Path[2]);
        // Steps first: still 6 steps, any number of turns is acceptable but the count is reported.
        db.Rules.Move.TieBreak = new() { RouteTieBreak.Steps, RouteTieBreak.Turns };
        var r2 = sim.Planner.Route(u, new Command { Type = CommandType.Move, TargetCell = to, Routing = RouteMode.MinCost }, u.Pos, u.Facing, to);
        Assert.Equal(6, r2.Path.Count);
        Assert.Equal(1, r2.Turns);
    }

    [Fact]
    public void PlayerMove_ExecutesTheModeRoute()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field(12, 7);
        for (int r = 0; r <= 4; r++) TestUtil.SetCell(map, new HexCoord(5, r), "mud");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var cmd = Command.Move(new HexCoord(8, 2));
        cmd.Routing = RouteMode.MinDistance;
        sim.Enqueue(u, cmd);
        var visited = new System.Collections.Generic.List<HexCoord>();
        for (int i = 0; i < sim.TicksFor(20) && u.Pos != new HexCoord(8, 2); i++)
        {
            sim.Step();
            if (visited.Count == 0 || visited[^1] != u.Pos) visited.Add(u.Pos);
        }
        Assert.Equal(new HexCoord(8, 2), u.Pos);
        Assert.Contains(visited, c => map.Cells.First(x => x.C == c).T == "mud");
    }

    [Fact]
    public void Threats_OnlyFromVisibleEnemies_AndHazardIcons()
    {
        var db = TestUtil.Minimal();
        db.Unit("dummy")!.AutoAttacks.Add(new AutoAttackDef { Range = 2 });
        db.Unit("dummy")!.Stats[Stats.DetectionRange] = 3;
        var map = TestUtil.Field(16, 8);
        TestUtil.SetCell(map, new HexCoord(3, 5), "wall");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 3), HexDirection.E);
        u.Stats.SetBase(Stats.DetectionRange, 8);
        var near = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(6, 3), HexDirection.W);
        var far = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(14, 3), HexDirection.W);
        sim.Step();
        var t = sim.Planner.Threats(u);
        Assert.True((t[new HexCoord(5, 3)] & ThreatKind.AutoAttack) != 0);
        Assert.True((t[new HexCoord(4, 3)] & ThreatKind.Detection) != 0);
        Assert.False(t.ContainsKey(new HexCoord(13, 3))); // the far enemy is not seen by the player side
        var h = sim.Planner.HazardsOf(u, sim.Map.Get(new HexCoord(5, 3))!, t);
        Assert.True(h.HasFlag(CellHazard.AutoAttack) && h.HasFlag(CellHazard.Detection));
        Assert.True(sim.Planner.HazardsOf(u, sim.Map.Get(new HexCoord(3, 5))!, t).HasFlag(CellHazard.Obstacle));
        Assert.True(sim.Planner.HazardsOf(u, sim.Map.Get(near.Pos)!, t).HasFlag(CellHazard.Enemy));
        Assert.False(sim.Planner.HazardsOf(u, sim.Map.Get(far.Pos)!, t).HasFlag(CellHazard.Enemy));
    }

    [Fact]
    public void FailedOrder_RecordsStopReason()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field();
        TestUtil.SetCell(map, new HexCoord(6, 1), "wall");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        sim.Enqueue(u, Command.Move(new HexCoord(6, 1)));
        sim.Step();
        Assert.NotEqual("", u.StopReason);
        Assert.Equal(0, u.Queue.Count);
    }

    [Fact]
    public void SightLines_SectorsRangeBlindAndPrimary()
    {
        var db = TestUtil.Minimal();
        var eye = new UnitDef
        {
            Id = "eye", Name = "Глаз", Faction = "enemy", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 50, [Stats.DetectionRange] = 6 },
            Perception = new PerceptionDef
            {
                Model = FacingModel.Multiple,
                Lines =
                {
                    new SightLineDef { Primary = true, Offset = 0, Width = 60 },
                    new SightLineDef { Offset = 3, Width = 60, Range = 2 },
                },
            },
        };
        db.Add(eye);
        var sim = TestUtil.Sim(db, TestUtil.Field(16, 10));
        var e = sim.Spawn(eye, null, new HexCoord(7, 4), HexDirection.E);
        var front = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(11, 4), HexDirection.W);
        var backNear = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(5, 4), HexDirection.E);
        var backFar = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(3, 4), HexDirection.E);
        var side = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(7, 1), HexDirection.E);
        Assert.True(sim.Vision.CanSee(e, front));
        Assert.True(sim.Vision.CanSee(e, backNear));
        Assert.False(sim.Vision.CanSee(e, backFar)); // the back line reaches 2 hexes
        Assert.False(sim.Vision.CanSee(e, side));
        // Turning rotates the extra line with the primary one.
        e.Facing = HexDirection.W;
        Assert.True(sim.Vision.CanSee(e, backFar));
        // An attack from the extra line's side is frontal.
        Assert.Equal(AttackSector.Front, CombatSystem.SectorFor(e, new HexCoord(9, 4)));

        // A blind zone removes cells of a wide sector; a fixed line does not rotate.
        eye.Perception.Lines = new() { new SightLineDef { Primary = true, Width = 360 }, new SightLineDef { Offset = 1, Width = 60, Blind = true, Fixed = true } };
        e.Facing = HexDirection.E;
        var ne = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(9, 2), HexDirection.E); // to the north-east (fixed blind line)
        Assert.False(sim.Vision.CanSee(e, ne));
        Assert.True(sim.Vision.CanSee(e, backFar));
        e.Facing = HexDirection.W;
        Assert.False(sim.Vision.CanSee(e, ne));

        var issues = new System.Collections.Generic.List<ContentIssue>();
        eye.Perception.Lines[0].Primary = false;
        ContentChecks.Run(db, issues);
        Assert.Contains(issues, i => i.Message.Contains("основная линия"));
    }
}
