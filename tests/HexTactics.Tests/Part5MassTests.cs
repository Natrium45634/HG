using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage D — mass and forced movement (ТЗ 15.2–15.3, T21; docs/DECISIONS_5.md §87).</summary>
public class Part5MassTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new SkillDef
        {
            Id = "shove", Name = "Толчок", Targeting = TargetingMode.Unit, RangeMax = 1, ApCost = 1, PrepTime = 0, RecoveryTime = 0.2, HitMode = HitMode.AlwaysHit,
            Stages = new() { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Push, Distance = 3, RequiresHit = false } } } },
        });
        db.Unit("soldier")!.Skills.Add("shove");
        db.Add(new UnitDef { Id = "ogre", Name = "Огр", Faction = "enemy", Stats = { [Stats.HpMax] = 200, [Stats.Weight] = 400 } });
        db.Add(new UnitDef { Id = "imp", Name = "Бес", Faction = "enemy", Stats = { [Stats.HpMax] = 50, [Stats.Weight] = 50 } });
        db.Add(new UnitDef { Id = "pawn", Name = "Пешка", Faction = "enemy", Stats = { [Stats.HpMax] = 50, [Stats.Weight] = 100 } });
        return db;
    }

    [Fact]
    public void Formula_AllOrNothing_TheHeavyDoNotMove_TheLightFlyTheWholeImpulse()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(12, 5));
        // Built-in (as in 1.0): impulse × reference / mass — 3 × 100 / 400 = 0, 3 × 100 / 50 = 6 (the light fly further).
        var ogre = sim.Spawn(db.Unit("ogre")!, "enemy", new HexCoord(4, 2), HexDirection.W);
        var imp = sim.Spawn(db.Unit("imp")!, "enemy", new HexCoord(4, 0), HexDirection.W);
        Assert.Equal(0, sim.Combat.KnockbackHexes(ogre, 3));
        Assert.Equal(6, sim.Combat.KnockbackHexes(imp, 3));
        // A rule «success = impulse × 40 ≥ mass», all or nothing.
        db.Rules.Move.KnockbackFormula = "if(impulse * 40 >= mass, impulse, 0)";
        Assert.Equal(0, sim.Combat.KnockbackHexes(ogre, 3));
        Assert.Equal(10, sim.Combat.KnockbackHexes(ogre, 10)); // 10 × 40 = 400 ≥ 400
        Assert.Equal(0, sim.Combat.KnockbackHexes(imp, 1)); // 40 < 50
        Assert.Equal(2, sim.Combat.KnockbackHexes(imp, 2));
        // The formula is checked like every formula of the rules.
        db.Rules.Move.KnockbackFormula = "impulse * speed";
        Assert.Contains(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Path.Contains("KnockbackFormula"));
    }

    /// <summary>A soldier shoves the ogre by the order; the battle is driven by frames at the given speed.</summary>
    private static (string Hash, HexCoord Ogre) Shove(int speedIndex, string formula)
    {
        var db = Db();
        db.Rules.Move.KnockbackFormula = formula;
        var sim = TestUtil.Sim(db, TestUtil.Field(12, 5));
        var hero = sim.Spawn(db.Unit("soldier")!, "player", new HexCoord(3, 2), HexDirection.E);
        var ogre = sim.Spawn(db.Unit("ogre")!, "enemy", new HexCoord(4, 2), HexDirection.W);
        var imp = sim.Spawn(db.Unit("imp")!, "enemy", new HexCoord(3, 1), HexDirection.W);
        sim.Input.Enqueue(hero, Command.Skill("shove", null, ogre.Id));
        sim.Input.Enqueue(hero, Command.Skill("shove", null, imp.Id));
        var driver = new SimDriver(sim) { Paused = false };
        driver.SetSpeedIndex(speedIndex);
        long ticks = sim.TicksFor(4);
        while (sim.Tick < ticks)
        {
            driver.Paused = false;
            driver.Advance(System.Math.Min(sim.Dt, (ticks - sim.Tick) * sim.Dt / driver.Speeds[driver.SpeedIndex]));
        }
        return (sim.Digest().Hash, ogre.Pos);
    }

    [Fact]
    public void T21_TheHeavyIsNotPushedByAWeakImpulse_AndAt4xTheSameAsAt1x()
    {
        var db = Db();
        int fastest = new SimDriver(TestUtil.Sim(db, TestUtil.Field(4, 4))).Speeds.Count - 1;
        var (h1, o1) = Shove(0, "if(impulse * 40 >= mass, impulse, 0)");
        var (h4, o4) = Shove(fastest, "if(impulse * 40 >= mass, impulse, 0)");
        Assert.Equal(new HexCoord(4, 2), o1);
        Assert.Equal(o1, o4);
        Assert.Equal(h1, h4);
        // A strong enough rule moves it — the same at both speeds as well.
        var (s1, p1) = Shove(0, "if(impulse * 200 >= mass, impulse, 0)");
        var (s4, p4) = Shove(fastest, "if(impulse * 200 >= mass, impulse, 0)");
        Assert.NotEqual(new HexCoord(4, 2), p1);
        Assert.Equal(p1, p4);
        Assert.Equal(s1, s4);
    }

    private static (Simulation Sim, Unit Imp, Unit Ogre, Unit Hero) Line(CollisionPolicy policy)
    {
        var db = Db();
        db.Rules.Move.Collision = policy;
        db.Rules.Move.ImpactDamage = 4;
        var sim = TestUtil.Sim(db, TestUtil.Field(12, 5));
        var hero = sim.Spawn(db.Unit("soldier")!, "player", new HexCoord(1, 2), HexDirection.E);
        // Of the reference mass: a push moves them exactly its impulse.
        var imp = sim.Spawn(db.Unit("pawn")!, "enemy", new HexCoord(2, 2), HexDirection.W);
        var ogre = sim.Spawn(db.Unit("pawn")!, "enemy", new HexCoord(4, 2), HexDirection.W); // another one behind, two hexes on
        return (sim, imp, ogre, hero);
    }

    [Fact]
    public void CollisionPolicies_Stop_ChainPush_ImpactDamage_DisplaceIfLegal()
    {
        // Stop (as in 1.0): the imp stops before the one behind it.
        var (sim, imp, behind, hero) = Line(CollisionPolicy.Stop);
        sim.Combat.Displace(imp, hero.Pos, true, 3, hero, "тест");
        Assert.Equal(new HexCoord(3, 2), imp.Pos);
        Assert.Equal(new HexCoord(4, 2), behind.Pos);

        // Chain push: the one behind takes the rest of the impulse.
        (sim, imp, behind, hero) = Line(CollisionPolicy.ChainPush);
        sim.Combat.Displace(imp, hero.Pos, true, 3, hero, "тест");
        Assert.Equal(new HexCoord(5, 2), imp.Pos);
        Assert.Equal(new HexCoord(6, 2), behind.Pos);

        // Impact damage: both take 4 × the two hexes not passed.
        (sim, imp, behind, hero) = Line(CollisionPolicy.ImpactDamage);
        double a = imp.Hp, b = behind.Hp;
        sim.Combat.Displace(imp, hero.Pos, true, 3, hero, "тест");
        Assert.Equal(new HexCoord(3, 2), imp.Pos);
        Assert.True(imp.Hp < a && behind.Hp < b, "удар должен ранить обоих");

        // Displace if legal: the imp slides to a free hex at the side and stops.
        (sim, imp, behind, hero) = Line(CollisionPolicy.DisplaceIfLegal);
        sim.Combat.Displace(imp, hero.Pos, true, 3, hero, "тест");
        Assert.Equal(1, HexCoord.Distance(imp.Pos, new HexCoord(3, 2)));
        Assert.NotEqual(2, imp.Pos.R); // left the line of the push
        Assert.Equal(new HexCoord(4, 2), behind.Pos);
    }
}
