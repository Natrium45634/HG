using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

public class PathfindingTests
{
    [Fact]
    public void StraightPath_HasMinimalCost()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        var p = sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(5, 0));
        Assert.True(p.Found);
        Assert.Equal(5, p.Path.Count);
        Assert.Equal(5, p.Cost, 6);
    }

    [Fact]
    public void Prefers_CheaperLongerRoute_OverExpensiveShortOne()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field(10, 5);
        // Mud wall across the direct line (cost 3 each) — going around is cheaper.
        for (int q = 3; q <= 5; q++) TestUtil.SetCell(map, new HexCoord(q, 2), "mud");
        TestUtil.SetCell(map, new HexCoord(3, 1), "mud");
        TestUtil.SetCell(map, new HexCoord(5, 3), "mud");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 2), HexDirection.E);
        var p = sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(7, 2));
        Assert.True(p.Found);
        double cost = p.Cost;
        // Direct path would cost 1+3+3+3+1+1 = 12; the found path must be cheaper and avoid mud.
        Assert.True(cost < 12, $"cost {cost}");
        Assert.DoesNotContain(p.Path, c => sim.Map.Get(c)!.Terrain.Id == "mud");
        Assert.True(p.Path.Count > HexCoord.Distance(new HexCoord(1, 2), new HexCoord(7, 2)));
    }

    [Fact]
    public void Walls_Occupancy_AndMissingCells_AreRespected()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field(8, 3);
        foreach (var c in map.Cells.Where(c => c.C.Q == 4).ToList()) c.T = "wall";
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        var p = sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(6, 1));
        Assert.False(p.Found);
        Assert.NotNull(p.Nearest);
        Assert.Equal(3, p.Nearest!.Value.Q);

        // Target is a wall: reason mentions impassable terrain.
        var p2 = sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(4, 1));
        Assert.False(p2.Found);
        Assert.Contains("непроходима", p2.Reason);

        // Target outside of the map.
        var p3 = sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(50, 50));
        Assert.False(p3.Found);

        // Occupied target.
        var other = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 1), HexDirection.E);
        var p4 = sim.Pathfinder.Find(sim, u, u.Pos, other.Pos);
        Assert.False(p4.Found);
        Assert.Contains("занята", p4.Reason);
    }

    [Fact]
    public void Height_ClimbLimit_AndCost()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field(6, 1);
        TestUtil.SetCell(map, new HexCoord(2, 0), "grass", 2); // too high to climb from 0
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        Assert.False(sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(4, 0)).Found);

        map = TestUtil.Field(6, 1);
        TestUtil.SetCell(map, new HexCoord(2, 0), "grass", 1);
        sim = TestUtil.Sim(db, map);
        u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        var p = sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(4, 0));
        Assert.True(p.Found);
        // 4 steps of cost 1 + one climb level (cost 1 per level by default).
        Assert.Equal(5, p.Cost, 6);
    }

    [Fact]
    public void Flood_ReturnsCellsWithinBudget()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, MapDef.CreateHexagon("hex", 5, "grass"));
        var u = sim.Spawn(db.Unit("soldier")!, null, HexCoord.Zero, HexDirection.E);
        var reach = sim.Planner.ReachableWithAp(u, u.Pos, 2);
        Assert.Equal(1 + 6 + 12, reach.Count);
    }
}

public class MovementAndApTests
{
    [Fact]
    public void Unit_MovesGradually_NotTeleported()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        Assert.Null(sim.Enqueue(u, Command.Move(new HexCoord(4, 0))));
        sim.Step();
        Assert.Equal(new HexCoord(0, 0), u.Pos);
        Assert.Equal(new HexCoord(1, 0), u.StepTo);
        // move_speed 2 → 0.5 s per cell of cost 1 = 10 ticks.
        sim.Run(10);
        Assert.Equal(new HexCoord(1, 0), u.Pos);
        TestUtil.RunUntil(sim, () => u.Pos == new HexCoord(4, 0), 5);
        Assert.Equal(new HexCoord(4, 0), u.Pos);
        Assert.Equal(0, u.Queue.Count);
    }

    [Fact]
    public void Ap_IsSpentPerStep_AndRegeneratesIndependently()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var a = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        var b = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 3), HexDirection.E);
        a.Ap = 5;
        b.Ap = 2;
        sim.Enqueue(a, Command.Move(new HexCoord(3, 0)));
        Assert.Equal(3, a.ReservedAp, 6);
        Assert.Equal(2, a.FreeAp, 6);
        sim.Step();
        // First step paid at start: 5 - 1 + regen(0.05).
        Assert.Equal(4.05, a.Ap, 6);
        Assert.Equal(2.05, b.Ap, 6);
        Assert.Equal(2, a.ReservedAp, 6);
        sim.RunSeconds(1);
        Assert.Equal(3.05, b.Ap, 6); // b regenerates 1/s regardless of a
    }

    [Fact]
    public void CancellingCommand_ReleasesReservation_WithoutRefundingStartedSteps()
    {
        var db = TestUtil.Minimal();
        db.Rules.Turn.ManualCostsAp = true; // a paid turn reserves AP (part 1 rule, optional since part 3)
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        u.Ap = 10;
        sim.Enqueue(u, Command.Move(new HexCoord(5, 0)));
        sim.Enqueue(u, Command.Turn(HexDirection.W));
        Assert.Equal(5 + 3 * 0.25, u.ReservedAp, 6);
        var turn = u.Queue.Items[1];
        Assert.True(sim.RemoveCommand(u, turn.Id));
        Assert.Equal(5, u.ReservedAp, 6);
        sim.Step();
        double apAfterStep = u.Ap;
        sim.ClearQueue(u);
        Assert.Equal(0, u.ReservedAp, 6);
        Assert.Equal(apAfterStep, u.Ap, 6);
        // The step already in progress is completed (no half-cell positions).
        sim.RunSeconds(1);
        Assert.Equal(new HexCoord(1, 0), u.Pos);
    }

    [Fact]
    public void NotEnoughAp_WaitsForRegen_ThenContinues()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        u.Ap = 0.5;
        sim.Enqueue(u, Command.Move(new HexCoord(2, 0)));
        sim.Step();
        Assert.Equal(CommandStatus.Waiting, u.Queue.Current!.Status);
        TestUtil.RunUntil(sim, () => u.Pos == new HexCoord(2, 0), 5);
        Assert.Equal(new HexCoord(2, 0), u.Pos);
    }

    [Fact]
    public void ImpossibleWait_IsBounded_ByTimeout()
    {
        var db = TestUtil.Minimal();
        db.Units().First(x => x.Id == "soldier").Stats[Stats.ApRegen] = 0;
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        u.Ap = 0;
        sim.Enqueue(u, Command.Move(new HexCoord(3, 0)));
        sim.Enqueue(u, Command.Wait(1));
        sim.RunSeconds(db.Rules.Queue.NotEnoughAp.WaitSeconds + 0.5);
        // The move was skipped after the timeout; the queue moved on.
        Assert.True(u.Queue.Count <= 1);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.CommandSkipped);
    }

    [Fact]
    public void BlockedPath_Repaths_AroundNewObstacle()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 5));
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 2), HexDirection.E);
        sim.Enqueue(u, Command.Move(new HexCoord(6, 2)));
        sim.Step();
        // Someone steps into the planned route.
        var blocker = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(3, 2), HexDirection.E);
        TestUtil.RunUntil(sim, () => u.Pos == new HexCoord(6, 2), 12);
        Assert.Equal(new HexCoord(6, 2), u.Pos);
        Assert.Equal(new HexCoord(3, 2), blocker.Pos);
    }

    [Fact]
    public void NoPath_CancelsQueue_AndReportsReason()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field(8, 3);
        foreach (var c in map.Cells.Where(c => c.C.Q == 4)) c.T = "wall";
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        sim.Enqueue(u, Command.Move(new HexCoord(6, 1)));
        sim.Enqueue(u, Command.Turn(HexDirection.W));
        sim.Step();
        Assert.Equal(0, u.Queue.Count);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.CommandFailed);
        Assert.Equal(new HexCoord(1, 1), u.Pos);
    }

    [Fact]
    public void Turning_CostsApAndTime_ShortestWay()
    {
        var db = TestUtil.Minimal();
        // Part 1 cost of a manual turn is an option since part 3 (docs/DECISIONS_3.md §40).
        db.Rules.Turn.ManualCostsAp = true;
        db.Rules.Turn.ManualTakesTime = true;
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        u.Ap = 5;
        sim.Enqueue(u, Command.Turn(HexDirection.SE));
        sim.Step();
        Assert.Equal(HexDirection.SE, u.Facing);
        Assert.Equal(5 - 0.25 + 0.05, u.Ap, 6);
    }

    [Fact]
    public void AutoFaceOnMove_IsOption()
    {
        var db = TestUtil.Minimal();
        db.Rules.Turn.AutoFaceOnMove = false;
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(3, 3), HexDirection.W);
        sim.Enqueue(u, Command.Move(new HexCoord(5, 3)));
        sim.RunSeconds(3);
        Assert.Equal(HexDirection.W, u.Facing);

        db.Rules.Turn.AutoFaceOnMove = true;
        sim = TestUtil.Sim(db, TestUtil.Field());
        u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(3, 3), HexDirection.W);
        sim.Enqueue(u, Command.Move(new HexCoord(5, 3)));
        sim.RunSeconds(3);
        Assert.Equal(HexDirection.E, u.Facing);
    }

    [Fact]
    public void QueueProjection_ShowsCumulativeReserve_AndWaits()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 0), HexDirection.E);
        u.Ap = 4;
        sim.Enqueue(u, Command.Move(new HexCoord(3, 0)));
        sim.Enqueue(u, Command.Move(new HexCoord(6, 0)));
        var proj = sim.Planner.ProjectQueue(u);
        Assert.Equal(2, proj.Count);
        Assert.Equal(3, proj[0].CumulativeAp, 6);
        Assert.Equal(6, proj[1].CumulativeAp, 6);
        Assert.Equal(new HexCoord(3, 0), proj[1].StartPos);
        Assert.Equal(-2, proj[1].RemainingNoRegen, 6);
        // With regen during the first move (1.5 s) the second still needs a short wait or none.
        Assert.True(proj[1].WaitForApSeconds >= 0);
    }
}

internal static class DbExt
{
    public static System.Collections.Generic.IEnumerable<UnitDef> Units(this ContentDatabase db) => db.All<UnitDef>();
}
