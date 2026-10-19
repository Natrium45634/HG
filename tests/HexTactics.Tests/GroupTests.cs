using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 3, stage 4: group orders, positions, jams, roles and formations (docs/DECISIONS_3.md §44).</summary>
public class GroupTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new SkillDef
        {
            Id = "shot", Name = "Выстрел", Targeting = TargetingMode.Unit, RangeMax = 4, Ranged = true, ApCost = 2, PrepTime = 0.1, RecoveryTime = 0.1,
            Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 6 } } } },
        });
        db.Add(new UnitDef
        {
            Id = "archer", Name = "Лучник", Faction = "player", BasicAttack = "shot",
            Stats = { [Stats.HpMax] = 60, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 9 },
        });
        db.Add(new UnitDef
        {
            Id = "healer", Name = "Лекарь", Faction = "player", BasicAttack = "hit", Role = UnitRole.Support,
            Stats = { [Stats.HpMax] = 60, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 9 },
        });
        db.Add(new FormationDef
        {
            Id = "line", Name = "Линия", Kind = FormationKind.Slots,
            Slots = { new() { Offset = new HexCoord(0, 0) }, new() { Offset = new HexCoord(0, -1) }, new() { Offset = new HexCoord(0, 1) } },
        });
        db.Add(new FormationDef { Id = "compact", Name = "Компактная", Kind = FormationKind.Free });
        return db;
    }

    [Fact]
    public void Hungarian_IsOptimal_OnSmallMatrices()
    {
        var rnd = new Random(7);
        for (int t = 0; t < 40; t++)
        {
            int n = rnd.Next(1, 5), m = rnd.Next(n, 6);
            var c = new double[n, m];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                c[i, j] = rnd.Next(0, 20);
            var a = GroupPlanner.Hungarian(c);
            Assert.Equal(n, a.Distinct().Count());
            double got = Enumerable.Range(0, n).Sum(i => c[i, a[i]]);
            double best = double.MaxValue;
            foreach (var perm in Perms(Enumerable.Range(0, m).ToList(), n)) best = Math.Min(best, Enumerable.Range(0, n).Sum(i => c[i, perm[i]]));
            Assert.Equal(best, got, 6);
        }
    }

    private static IEnumerable<List<int>> Perms(List<int> items, int k)
    {
        if (k == 0)
        {
            yield return new List<int>();
            yield break;
        }
        foreach (var x in items)
        foreach (var rest in Perms(items.Where(y => y != x).ToList(), k - 1))
            yield return new List<int> { x }.Concat(rest).ToList();
    }

    [Fact]
    public void MoveToOnePoint_GivesDistinctHexes_Stable_AndExecutes()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(14, 10));
        var units = new List<Unit>();
        for (int i = 0; i < 5; i++) units.Add(sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1 + i), HexDirection.E));
        var point = new HexCoord(9, 4);
        var a1 = sim.Groups.AssignMove(units, point);
        var a2 = sim.Groups.AssignMove(units, point);
        Assert.Equal(5, a1.Cells.Values.Distinct().Count());
        Assert.Contains(point, a1.Cells.Values);
        Assert.All(a1.Cells.Values, c => Assert.True(HexCoord.Distance(c, point) <= 2));
        Assert.Equal(a1.Cells.OrderBy(k => k.Key), a2.Cells.OrderBy(k => k.Key));

        var r = sim.IssueGroupOrder(units, new GroupOrder { Command = GroupCommand.Move, Point = point });
        Assert.Equal(5, r.Ordered.Count);
        for (int t = 0; t < sim.TicksFor(20); t++)
        {
            sim.Step();
            var cells = units.Select(u => u.Pos).ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count());
        }
        Assert.True(units.All(u => a1.Cells[u.Id] == u.Pos), string.Join("\n", sim.Log.Entries.Where(e => e.SourceUnit == units[2].Id).Select(e => e.Text).TakeLast(25)));
        // The reservations are released once everybody arrived.
        Assert.All(sim.Map.Cells, c => Assert.Equal(-1, c.DestinationOf));
    }

    [Fact]
    public void AllyOnThePoint_IsNotAnEndPoint_AndLackOfSpaceIsPartial()
    {
        var db = Db();
        var map = TestUtil.Field(12, 8);
        // Around (8,3) only one neighbour (to the west, the entrance) is free: with a spread of 1 there are 2 places.
        for (int d = 0; d < 6; d++)
            if ((HexDirection)d != HexDirection.W)
                TestUtil.SetCell(map, new HexCoord(8, 3) + ((HexDirection)d).Offset(), "wall");
        var sim = TestUtil.Sim(db, map);
        var bystander = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 4), HexDirection.E);
        var units = Enumerable.Range(0, 3).Select(i => sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1 + i), HexDirection.E)).ToList();
        var a = sim.Groups.AssignMove(units, bystander.Pos);
        Assert.DoesNotContain(bystander.Pos, a.Cells.Values);
        Assert.Equal(3, a.Cells.Values.Distinct().Count());

        var pocket = sim.Groups.AssignMove(units, new HexCoord(8, 3), maxSpread: 1);
        Assert.True(pocket.Partial);
        Assert.Equal(2, pocket.Cells.Count);
        var r = sim.IssueGroupOrder(units, new GroupOrder { Command = GroupCommand.Move, Point = new HexCoord(8, 3), MaxSpread = 1 });
        Assert.True(r.Partial);
        Assert.Contains("частично", r.Message);
    }

    [Fact]
    public void HeadOn_InACorridor_UnitsSwap_InsteadOfWaitingForever()
    {
        var db = Db();
        var map = TestUtil.Field(10, 3);
        for (int q = 0; q < 10; q++)
        {
            TestUtil.SetCell(map, new HexCoord(q, 0), "wall");
            TestUtil.SetCell(map, new HexCoord(q, 2), "wall");
        }
        var sim = TestUtil.Sim(db, map);
        var a = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 1), HexDirection.E);
        var b = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(6, 1), HexDirection.W);
        sim.Enqueue(a, Command.Move(new HexCoord(7, 1)));
        sim.Enqueue(b, Command.Move(new HexCoord(1, 1)));
        TestUtil.RunUntil(sim, () => a.Pos == new HexCoord(7, 1) && b.Pos == new HexCoord(1, 1), 30);
        Assert.True(a.Pos == new HexCoord(7, 1) && b.Pos == new HexCoord(1, 1), string.Join("\n", sim.Log.Entries.Where(e => e.Level <= LogLevel.Diagnostic).Select(e => e.Text).TakeLast(30)));
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("меняются местами"));
    }

    [Fact]
    public void GroupAttack_MeleeAdjacent_RangedAtRange_SupportStays_NoOneOnEnemyHex()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(14, 10));
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(9, 5), HexDirection.W);
        enemy.Stats.SetBase(Stats.HpMax, 400);
        enemy.Hp = 400;
        var m1 = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 4), HexDirection.E);
        var m2 = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 5), HexDirection.E);
        var ar = sim.Spawn(db.Unit("archer")!, null, new HexCoord(1, 6), HexDirection.E);
        var he = sim.Spawn(db.Unit("healer")!, null, new HexCoord(1, 7), HexDirection.E);
        sim.Vision.Recompute();
        var group = new List<Unit> { m1, m2, ar, he };
        var r = sim.IssueGroupOrder(group, new GroupOrder { Command = GroupCommand.Attack, TargetUnitId = enemy.Id });
        Assert.Contains(r.Skipped, s => s.Unit == he.Id && s.Reason.Contains("поддержка"));
        var a = r.Assignment!;
        Assert.Equal(1, HexCoord.Distance(a.Cells[m1.Id], enemy.Pos));
        Assert.Equal(1, HexCoord.Distance(a.Cells[m2.Id], enemy.Pos));
        Assert.NotEqual(a.Cells[m1.Id], a.Cells[m2.Id]);
        Assert.InRange(HexCoord.Distance(a.Cells[ar.Id], enemy.Pos), 2, 4);
        bool onEnemy = false;
        for (int t = 0; t < sim.TicksFor(15); t++)
        {
            sim.Step();
            onEnemy |= group.Any(u => u.Pos == enemy.Pos);
        }
        Assert.False(onEnemy);
        Assert.True(enemy.Hp < 400);
        Assert.Equal(new HexCoord(1, 7), he.Pos);
    }

    [Fact]
    public void Formations_Line_RotatesWithHeading_Surround_RingsTheTarget()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(16, 12));
        var units = Enumerable.Range(0, 3).Select(i => sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 4 + i), HexDirection.E)).ToList();
        var line = db.Get<FormationDef>("line")!;
        var point = new HexCoord(10, 5);
        var a = sim.Groups.AssignMove(units, point, line);
        var expected = line.Slots.Select(s => point + GroupPlanner.Rotate(s.Offset, sim.Groups.Heading(units, point))).ToHashSet();
        Assert.True(expected.SetEquals(a.Cells.Values), string.Join(",", a.Cells.Values));
        // From the other side the line is rotated.
        var west = sim.Groups.Heading(units, new HexCoord(0, 5) - new HexCoord(20, 0));
        Assert.NotEqual(sim.Groups.Heading(units, point), west);

        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(8, 8), HexDirection.W);
        sim.Vision.Recompute();
        var r = sim.IssueGroupOrder(units, new GroupOrder { Command = GroupCommand.Surround, TargetUnitId = enemy.Id });
        Assert.Equal(3, r.Assignment!.Cells.Count);
        Assert.All(r.Assignment.Cells.Values, c => Assert.Equal(1, HexCoord.Distance(c, enemy.Pos)));
        Assert.All(units, u => Assert.Equal(r.Assignment.Cells[u.Id].DirectionTo(enemy.Pos), r.Assignment.Facings[u.Id]));
    }

    [Fact]
    public void OrderParameters_QueueModes_ExcludeTags_Cancel()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(14, 10));
        var a = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 2), HexDirection.E);
        var b = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 4), HexDirection.E);
        b.Tag = "rear";
        sim.Enqueue(a, Command.Move(new HexCoord(3, 2)));
        sim.Enqueue(a, Command.Move(new HexCoord(3, 0)));
        sim.Step();
        // Excluded by tag.
        var r = sim.IssueGroupOrder(new[] { a, b }, new GroupOrder { Command = GroupCommand.Face, Facing = HexDirection.W, ExcludeTags = { "rear" }, QueueMode = GroupQueueMode.Append });
        Assert.Contains(r.Skipped, s => s.Unit == b.Id);
        Assert.Equal(3, a.Queue.Count); // appended
        // After the current action: the pending ones give way, the active one stays.
        sim.IssueGroupOrder(new[] { a }, new GroupOrder { Command = GroupCommand.Face, Facing = HexDirection.E, QueueMode = GroupQueueMode.AfterCurrent });
        Assert.Equal(2, a.Queue.Count);
        Assert.Equal(CommandStatus.Active, a.Queue.Items[0].Status);
        // Replace.
        sim.IssueGroupOrder(new[] { a }, new GroupOrder { Command = GroupCommand.Move, Point = new HexCoord(6, 6) });
        Assert.Single(a.Queue.Items.Where(c => c.Status == CommandStatus.Pending));
        // Stop after the current action / cancel the queue.
        sim.IssueGroupOrder(new[] { a }, new GroupOrder { Command = GroupCommand.StopAfterCurrent });
        Assert.True(a.Queue.Items.All(c => c.Status != CommandStatus.Pending));
        sim.IssueGroupOrder(new[] { a, b }, new GroupOrder { Command = GroupCommand.CancelQueue });
        Assert.Equal(0, a.Queue.Count);
        Assert.All(sim.Map.Cells, c => Assert.Equal(-1, c.DestinationOf));
    }

    [Fact]
    public void Follow_Disperse_AndAutoAttackPriority()
    {
        var db = Db();
        db.Unit("soldier")!.AutoAttacks.Add(new AutoAttackDef { Interval = 0.5 });
        var sim = TestUtil.Sim(db, TestUtil.Field(14, 10));
        var leader = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var f1 = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 6), HexDirection.E);
        sim.IssueGroupOrder(new[] { f1 }, new GroupOrder { Command = GroupCommand.Follow, TargetUnitId = leader.Id });
        sim.Enqueue(leader, Command.Move(new HexCoord(9, 3)));
        sim.RunSeconds(10);
        Assert.True(HexCoord.Distance(f1.Pos, leader.Pos) <= 2, $"ведомый {f1.Pos}, ведущий {leader.Pos}");

        var crowd = Enumerable.Range(0, 3).Select(i => sim.Spawn(db.Unit("soldier")!, null, new HexCoord(5, 7 + (i % 2)) + new HexCoord(i, 0), HexDirection.E)).ToList();
        var r = sim.IssueGroupOrder(crowd, new GroupOrder { Command = GroupCommand.Disperse });
        var cells = r.Assignment!.Cells.Values.ToList();
        for (int i = 0; i < cells.Count; i++)
        for (int j = i + 1; j < cells.Count; j++)
            Assert.True(HexCoord.Distance(cells[i], cells[j]) >= 2);

        var pr = sim.IssueGroupOrder(new[] { leader }, new GroupOrder { Command = GroupCommand.AutoAttackPriority, AutoPriority = AutoTargetRule.LowestHp });
        Assert.Equal(AutoTargetRule.LowestHp, leader.AutoAttackPriority);
        Assert.Single(pr.Ordered);
    }
}
