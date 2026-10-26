using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.3 — blocking and holding (ТЗ ч. 6, 4; T13–T16; docs/DECISIONS_6.md §98).</summary>
public class Part6BlockTests
{
    private static readonly HexCoord Guard = new(4, 2);

    /// <summary>The six neighbours of the guard's hex.</summary>
    private static readonly HexCoord[] Around = Guard.Neighbors().ToArray();

    private static Simulation Field(ContentDatabase db, bool ai = false, MapDef? map = null) => TestUtil.Sim(db, map ?? TestUtil.Field(10, 5), ai: ai);

    /// <summary>An enemy walking west past the guard (a move order — «advancing»).</summary>
    private static Unit Walker(Simulation sim, ContentDatabase db, string id, HexCoord at)
    {
        var u = sim.Spawn(db.Unit(id)!, "enemy", at, HexDirection.W);
        Assert.Null(sim.Enqueue(u, Command.Move(new HexCoord(0, at.R))));
        return u;
    }

    [Fact]
    public void T13_ACapacityOfThree_HoldsThreeOrdinaryEnemies_TheFourthGoesOn()
    {
        var db = TestUtil.Shipped();
        var sim = Field(db);
        var knight = sim.Spawn(db.Unit("knight")!, "player", Guard, HexDirection.E);
        Assert.Equal(3, sim.Blocking.Limit(knight));
        var at = new[] { new HexCoord(5, 2), new HexCoord(5, 1), new HexCoord(4, 3), new HexCoord(4, 1) };
        var gobs = at.Select(c => Walker(sim, db, "goblin", c)).ToList();
        sim.Step();
        var held = gobs.Where(g => sim.Blocking.BlockerOf(g) == knight).ToList();
        Assert.Equal(3, held.Count);
        Assert.Equal(3, sim.Blocking.Load(knight));
        Assert.Equal(1, sim.Blocking.Overflow(knight)); // the warning «переполнено»
        var free = gobs.Single(g => !held.Contains(g));
        var start = held.Select(g => g.Pos).ToList();
        TestUtil.RunUntil(sim, () => free.Pos.Q <= 1, 8);
        Assert.True(free.Pos.Q <= 1, "четвёртый должен пройти дальше");
        Assert.Equal(start, held.Select(g => g.Pos).ToList());
    }

    [Fact]
    public void T14_AHeavyEnemyTakesTwoSlots_AndTheBlockersDeathDropsEveryLinkAtOnce()
    {
        var db = TestUtil.Shipped();
        var sim = Field(db);
        var knight = sim.Spawn(db.Unit("knight")!, "player", Guard, HexDirection.E);
        Assert.Equal(2, db.Unit("orc_brute")!.Blockable.SlotCost);
        // Heavy first, so the order of targets cannot hide the weight.
        db.Unit("knight")!.Blocking.TargetOrder = BlockTargetOrder.Heaviest;
        var brute = Walker(sim, db, "orc_brute", new HexCoord(5, 2));
        var g1 = Walker(sim, db, "goblin", new HexCoord(5, 1));
        var g2 = Walker(sim, db, "goblin", new HexCoord(4, 3));
        sim.Step();
        Assert.Equal(knight, sim.Blocking.BlockerOf(brute));
        Assert.Equal(3, sim.Blocking.Load(knight));
        Assert.Single(new[] { g1, g2 }.Where(g => sim.Blocking.BlockerOf(g) == knight));
        Assert.Equal(2, sim.Blocking.Links);

        sim.Combat.Kill(knight, null, "тест");
        Assert.Equal(0, sim.Blocking.Links);
        Assert.All(new[] { brute, g1, g2 }, e => Assert.Null(sim.Blocking.BlockerOf(e)));
        Assert.All(new[] { brute, g1, g2 }, e => Assert.NotEqual(MovementHold.Blocked, sim.Blocking.HoldOf(e)));
    }

    [Fact]
    public void T15_AnUnstoppableUnit_IsNotBlocked_ButWallsStillStopIt()
    {
        var db = TestUtil.Shipped();
        // A wall across the field with one gap at the far row.
        var map = TestUtil.Field(10, 5);
        foreach (var c in map.Cells.Where(c => c.C.Q == 2 && c.C.R != 4).ToList()) TestUtil.SetCell(map, c.C, "wall");
        var sim = Field(db, map: map);
        var knight = sim.Spawn(db.Unit("knight")!, "player", Guard, HexDirection.E);
        db.Unit("goblin")!.Blockable.IgnoreBlockers.Add("knight");
        var gob = Walker(sim, db, "goblin", new HexCoord(5, 2));
        var orc = Walker(sim, db, "orc_brute", new HexCoord(5, 1));
        sim.Step();
        Assert.Null(sim.Blocking.BlockerOf(gob));
        Assert.Equal(knight, sim.Blocking.BlockerOf(orc));
        var walls = map.Cells.Where(c => c.T == "wall").Select(c => c.C).ToHashSet();
        var visited = new HashSet<HexCoord>();
        for (int i = 0; i < sim.TicksFor(12) && gob.Pos.Q > 0; i++)
        {
            sim.Step();
            visited.Add(gob.Pos);
        }
        Assert.Empty(visited.Intersect(walls));
        Assert.Contains(visited, c => c.R == 4 && c.Q == 2); // through the gap, not the wall

        // The same through a status (a passive could grant it): «Неудержим».
        var db2 = TestUtil.Shipped();
        var sim2 = Field(db2);
        sim2.Spawn(db2.Unit("knight")!, "player", Guard, HexDirection.E);
        db2.Add(new StatusDef { Id = "unstoppable", Name = "Неудержим", Duration = 10, Levels = { new StatusLevelDef { Threshold = 1, Flags = { IgnoresBlocking = true } } } });
        var g2 = Walker(sim2, db2, "goblin", new HexCoord(5, 2));
        sim2.Statuses.Apply(g2, db2.Status("unstoppable")!, 1, 0, -1, force: true);
        sim2.Step();
        Assert.Null(sim2.Blocking.BlockerOf(g2));
    }

    [Fact]
    public void T16_TwoBlockersForOneEnemy_TheSameWinnerEveryTime_ByPriorityThenDistanceThenId()
    {
        string Winner(int priorityA, int priorityB, HexCoord a, HexCoord b, bool bFirst)
        {
            var db = TestUtil.Shipped();
            var ka = ContentJson.Clone(db.Unit("scout")!);
            ka.Id = "block_a";
            ka.Blocking.Priority = priorityA;
            ka.Blocking.Range = 2;
            var kb = ContentJson.Clone(db.Unit("scout")!);
            kb.Id = "block_b";
            kb.Blocking.Priority = priorityB;
            kb.Blocking.Range = 2;
            db.Add(ka);
            db.Add(kb);
            var sim = Field(db);
            Unit A, B;
            if (bFirst)
            {
                B = sim.Spawn(kb, "player", b, HexDirection.E);
                A = sim.Spawn(ka, "player", a, HexDirection.E);
            }
            else
            {
                A = sim.Spawn(ka, "player", a, HexDirection.E);
                B = sim.Spawn(kb, "player", b, HexDirection.E);
            }
            var e = Walker(sim, db, "goblin", new HexCoord(5, 2));
            sim.Step();
            var w = sim.Blocking.BlockerOf(e);
            return w == A ? "A" : w == B ? "B" : "-";
        }

        // Equal: the same data — the same winner, run after run.
        var first = Winner(0, 0, new HexCoord(4, 2), new HexCoord(5, 3), false);
        for (int i = 0; i < 5; i++) Assert.Equal(first, Winner(0, 0, new HexCoord(4, 2), new HexCoord(5, 3), false));
        Assert.Equal("A", first); // both adjacent: the smaller ID (spawned first)
        // The priority wins over the distance and the ID.
        Assert.Equal("B", Winner(0, 5, new HexCoord(4, 2), new HexCoord(5, 4), false));
        // Equal priority: the closer one, whoever was spawned first.
        Assert.Equal("A", Winner(0, 0, new HexCoord(4, 2), new HexCoord(5, 4), true));
        Assert.Equal("B", Winner(0, 0, new HexCoord(3, 2), new HexCoord(5, 3), false));
    }

    [Fact]
    public void Range_Stun_Duration_AndTheCannotBlockStatus()
    {
        var db = TestUtil.Shipped();
        var kd = db.Unit("knight")!;
        kd.Blocking.Range = 2;
        var sim = Field(db);
        var knight = sim.Spawn(kd, "player", Guard, HexDirection.E);
        var far = Walker(sim, db, "goblin", new HexCoord(6, 2));
        sim.Step();
        Assert.Equal(knight, sim.Blocking.BlockerOf(far)); // held at two hexes, never entering the neighbourhood

        // Stunned: off unless «блокирует под оглушением»; the status «не может блокировать» — off always.
        knight.Flags.BlocksActions = true;
        sim.Blocking.Update();
        Assert.Null(sim.Blocking.BlockerOf(far));
        kd.Blocking.WhileStunned = true;
        sim.Blocking.Update();
        Assert.Equal(knight, sim.Blocking.BlockerOf(far));
        knight.Flags.CannotBlock = true;
        sim.Blocking.Update();
        Assert.Null(sim.Blocking.BlockerOf(far));
        knight.Flags.Clear();

        // «Держит не дольше 1 с», then lets it through for 2 s.
        kd.Blocking.MaxDuration = 1;
        kd.Blocking.PassTime = 2;
        var sim2 = Field(db);
        var k2 = sim2.Spawn(kd, "player", Guard, HexDirection.E);
        var g = Walker(sim2, db, "goblin", new HexCoord(5, 2));
        sim2.Step();
        Assert.Equal(k2, sim2.Blocking.BlockerOf(g));
        long since = sim2.Blocking.BlockedSince(g);
        Assert.True(since >= 0);
        TestUtil.RunUntil(sim2, () => sim2.Blocking.BlockerOf(g) == null, 3);
        Assert.Null(sim2.Blocking.BlockerOf(g));
        Assert.InRange((sim2.Tick - since) * sim2.Dt, 0.95, 1.1);
        Assert.True(sim2.Blocking.Passing(g));
        TestUtil.RunUntil(sim2, () => g.Pos.Q <= 2, 4);
        Assert.True(g.Pos.Q <= 2, "отпущенный враг проходит");
    }

    [Fact]
    public void BreakThrough_UsesItsSkillOnTheBlocker()
    {
        var db = TestUtil.Shipped();
        var bd = db.Unit("orc_brute")!;
        bd.Blockable.WhenBlocked = BlockedBehavior.BreakThrough;
        bd.Blockable.BreakSkill = "quake";
        var sim = Field(db, ai: true);
        var knight = sim.Spawn(db.Unit("knight")!, "player", Guard, HexDirection.E);
        var brute = Walker(sim, db, "orc_brute", new HexCoord(5, 2));
        bool Used() => brute.Casting?.Skill.Id == "quake" || brute.CooldownUntil.ContainsKey("quake")
                       || brute.Queue.Items.Any(c => c.SkillId == "quake" && c.TargetUnitId == knight.Id);
        TestUtil.RunUntil(sim, Used, 6);
        Assert.True(Used(), "громила должен применить навык прорыва к блокирующему");

        // The check: a skill of breaking through that is not the unit's own.
        bd.Blockable.BreakSkill = "fireball";
        Assert.Contains(ContentValidator.Validate(db), i => i.Message.Contains("Навык прорыва «fireball»"));
    }
}
