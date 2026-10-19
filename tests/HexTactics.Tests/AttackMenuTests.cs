using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 3, stage 2: attack on an enemy hex, approach, radial menu order, use parameters (docs/DECISIONS_3.md §42).</summary>
public class AttackMenuTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new SkillDef
        {
            Id = "shot", Name = "Выстрел", Targeting = TargetingMode.Unit, RangeMax = 3, Ranged = true, ApCost = 2, PrepTime = 0.1, RecoveryTime = 0.1,
            Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 8 } } } },
        });
        db.Add(new SkillDef
        {
            Id = "nova", Name = "Взрыв", Targeting = TargetingMode.Self, ApCost = 4,
            Stages = { new SkillStageDef { Area = new AreaDef { Shape = AreaShape.Radius, Size = 2 }, Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 30 } } } },
        });
        db.Add(new SkillDef
        {
            Id = "jab", Name = "Тычок", Targeting = TargetingMode.Unit, ApCost = 1,
            Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 3 } } } },
        });
        db.Add(new SkillDef { Id = "guard", Name = "Защита", Kind = SkillKind.Defense, Targeting = TargetingMode.Self, ApCost = 1 });
        db.Add(new UnitDef
        {
            Id = "archer", Name = "Лучник", Faction = "player", BasicAttack = "shot",
            Stats = { [Stats.HpMax] = 60, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 9 },
        });
        return db;
    }

    [Fact]
    public void AttackOrder_ApproachesMeleeTarget_NeverEntersItsHex()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(6, 2), HexDirection.W);
        var cmd = Command.Attack(enemy.Id);
        cmd.Approach = true;
        Assert.Null(sim.Enqueue(u, cmd));
        var est = sim.Planner.ProjectQueue(u)[0];
        Assert.True(est.Estimate.Feasible);
        Assert.Equal(1, HexCoord.Distance(est.EndPos, enemy.Pos));
        bool entered = false;
        for (int i = 0; i < sim.TicksFor(10) && enemy.Hp >= 100; i++)
        {
            sim.Step();
            if (u.Pos == enemy.Pos) entered = true;
        }
        Assert.False(entered);
        Assert.True(enemy.Hp < 100);
        Assert.Equal(1, HexCoord.Distance(u.Pos, enemy.Pos));
    }

    [Fact]
    public void AttackOrder_WithoutApproach_DoesNotMove()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(6, 2), HexDirection.W);
        sim.Enqueue(u, Command.Attack(enemy.Id));
        sim.RunSeconds(2);
        Assert.Equal(new HexCoord(1, 2), u.Pos);
        Assert.Equal(100, enemy.Hp);
    }

    [Fact]
    public void RangedApproach_FindsLineOfSight()
    {
        var db = Db();
        var map = TestUtil.Field(12, 8);
        TestUtil.SetCell(map, new HexCoord(4, 3), "wall");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("archer")!, null, new HexCoord(2, 3), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(5, 3), HexDirection.W);
        sim.Spawn(db.Unit("soldier")!, null, new HexCoord(5, 6), HexDirection.NE); // a spotter: the side sees the target behind the wall
        sim.Step();
        var chk = sim.Planner.CheckAgainst(u, db.Skill("shot")!, enemy, u.Pos, u.Facing);
        Assert.False(chk.Now);
        Assert.True(chk.AfterApproach, chk.Reason);
        var cmd = Command.Attack(enemy.Id);
        cmd.Approach = true;
        sim.Enqueue(u, cmd);
        TestUtil.RunUntil(sim, () => enemy.Hp < 100, 10);
        Assert.True(enemy.Hp < 100);
        Assert.True(sim.Los.Has(sim.Map, sim.Rules, u.Pos, enemy.Pos));
        Assert.True(HexCoord.Distance(u.Pos, enemy.Pos) <= 3 && HexCoord.Distance(u.Pos, enemy.Pos) >= 2, "лучник не подходит вплотную без нужды");

        // A skill used only from the spot explains it.
        db.Skill("shot")!.AllowMoveBeforeUse = false;
        var u2 = sim.Spawn(db.Unit("archer")!, null, new HexCoord(0, 7), HexDirection.E);
        sim.Step();
        var chk2 = sim.Planner.CheckAgainst(u2, db.Skill("shot")!, enemy, u2.Pos, u2.Facing);
        Assert.False(chk2.Now || chk2.AfterApproach);
        Assert.Contains("с места", chk2.Reason);
    }

    [Fact]
    public void CheckAgainst_ExplainsImmunity()
    {
        var db = Db();
        db.Unit("dummy")!.Defense.Immune.Add("physical");
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(2, 2), HexDirection.W);
        sim.Step();
        var r = sim.Planner.CheckAgainst(u, db.Skill("hit")!, enemy, u.Pos, u.Facing);
        Assert.True(r.Now); // it can be used (the damage is 0) …
        var far = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(9, 2), HexDirection.W);
        sim.Step();
        var r2 = sim.Planner.CheckAgainst(u, db.Skill("hit")!, far, u.Pos, u.Facing);
        Assert.Contains("невосприимчива", r2.Reason); // … and a failing check names the immunity
    }

    [Fact]
    public void RadialMenu_DefenseInCentre_RingsWeakToStrong_ManualPriority()
    {
        var db = Db();
        var def = db.Unit("soldier")!;
        def.Skills = new() { "nova", "jab", "shot", "guard" };
        def.DefenseSkill = "guard";
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(def, null, new HexCoord(1, 2), HexDirection.E);
        var (defense, first, second) = sim.Planner.RadialSkills(u);
        Assert.Equal("guard", defense?.Id);
        Assert.Equal(new[] { "jab", "shot", "hit", "nova" }, first.Select(s => s.Id));
        Assert.Empty(second);
        // Manual priority puts «nova» first; «second ring» moves a skill outward; capacity overflows.
        db.Skill("nova")!.MenuPriority = 1;
        db.Skill("jab")!.Ring = MenuRing.Second;
        (_, first, second) = sim.Planner.RadialSkills(u, firstCapacity: 2);
        Assert.Equal("nova", first[0].Id);
        Assert.Equal(2, first.Count);
        Assert.Contains(second, s => s.Id == "jab");
        Assert.Equal(4, first.Count + second.Count);
        Assert.True(u.KnowsSkill("guard"));
    }

    [Fact]
    public void CostsAtRelease_AndCooldownAtStart()
    {
        var db = Db();
        var hit = db.Skill("hit")!;
        hit.PayAt = CostMoment.Release;
        hit.PrepTime = 1.0;
        hit.Cooldown = 5;
        hit.CooldownAt = CostMoment.Start;
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(2, 2), HexDirection.W);
        u.Ap = 5;
        sim.Enqueue(u, Command.Attack(enemy.Id));
        sim.Step();
        Assert.NotNull(u.Casting);
        Assert.True(u.Ap > 4.9, "ОД не должны списываться до срабатывания");
        Assert.True(u.CooldownRemaining("hit", sim.Tick) > 0, "перезарядка должна начаться при запуске");
        TestUtil.RunUntil(sim, () => u.Casting == null, 3);
        Assert.True(u.Ap < 4.5);
        Assert.True(enemy.Hp < 100);

        // Not enough AP at the release: the skill does not fire and nothing is spent.
        var u2 = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 4), HexDirection.E);
        var e2 = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(2, 4), HexDirection.W);
        sim.Vision.Recompute();
        u2.Ap = 2.5;
        sim.Enqueue(u2, Command.Attack(e2.Id));
        sim.Step();
        u2.Ap = 0.5; // spent elsewhere during the preparation
        TestUtil.RunUntil(sim, () => u2.Casting == null, 3);
        Assert.Equal(100, e2.Hp);
        Assert.True(sim.Log.Entries.Any(e => e.Text.Contains("не сработало")), string.Join("\n", sim.Log.Entries.Where(e => e.SourceUnit == u2.Id).Select(e => e.Text)));
    }

    [Fact]
    public void FacingAfterAttack_Keep_AndRouteStopsWhenAsked()
    {
        var db = Db();
        var hit = db.Skill("hit")!;
        hit.FacingAfter = FacingAfterUse.Keep;
        hit.ContinueRoute = false;
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.W);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        sim.Enqueue(u, Command.Attack(enemy.Id));
        sim.Enqueue(u, Command.Move(new HexCoord(2, 5)));
        TestUtil.RunUntil(sim, () => enemy.Hp < 100, 3);
        sim.RunSeconds(1);
        Assert.Equal(HexDirection.W, u.Facing);
        Assert.Equal(0, u.Queue.Count);
        Assert.Equal(new HexCoord(2, 2), u.Pos);
    }
}
