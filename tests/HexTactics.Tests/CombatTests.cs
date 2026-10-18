using System;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

public class AreaTests
{
    private static int Count(AreaDef a) => AreaResolver.CanonicalOffsets(a).Count;

    [Fact]
    public void TemplateShapes_HaveExpectedSizes()
    {
        Assert.Equal(1, Count(new AreaDef { Shape = AreaShape.Single }));
        Assert.Equal(4, Count(new AreaDef { Shape = AreaShape.Line, Size = 4 }));
        Assert.Equal(5, Count(new AreaDef { Shape = AreaShape.Line, Size = 4, IncludeCenter = true }));
        Assert.Equal(18, Count(new AreaDef { Shape = AreaShape.Radius, Size = 2 }));
        Assert.Equal(19, Count(new AreaDef { Shape = AreaShape.Radius, Size = 2, IncludeCenter = true }));
        Assert.Equal(12, Count(new AreaDef { Shape = AreaShape.Ring, Size = 2, InnerRadius = 2 }));
        Assert.Equal(18, Count(new AreaDef { Shape = AreaShape.Ring, Size = 2, InnerRadius = 1 }));
        Assert.Equal(12, Count(new AreaDef { Shape = AreaShape.Cross, Size = 2 }));
        Assert.Equal(3, Count(new AreaDef { Shape = AreaShape.Sector, Size = 1, Angle = 120 }));
        // 60° cone of length 3 with inclusive boundaries: rows 1, 3, 3.
        Assert.Equal(7, Count(new AreaDef { Shape = AreaShape.Cone, Size = 3 }));
        Assert.Equal(36, Count(new AreaDef { Shape = AreaShape.Sector, Size = 3, Angle = 360 }));
    }

    [Fact]
    public void Areas_RotateWithOrientation_AndTranslateToAnchor()
    {
        var cone = new AreaDef { Shape = AreaShape.Cone, Size = 2 };
        var anchor = new HexCoord(5, -2);
        var east = AreaResolver.Resolve(cone, anchor, HexDirection.E).Select(c => c.Cell).ToHashSet();
        Assert.Contains(anchor + new HexCoord(1, 0), east);
        for (int d = 0; d < 6; d++)
        {
            var rotated = AreaResolver.Resolve(cone, anchor, (HexDirection)d).Select(c => c.Cell).ToHashSet();
            var expected = east.Select(c => c.RotateAround(anchor, d)).ToHashSet();
            Assert.Equal(expected, rotated);
            Assert.Contains(anchor + ((HexDirection)d).Offset(), rotated);
        }
    }

    [Fact]
    public void Mask_IsStoredAsData_AndRotatesCorrectly()
    {
        var mask = new AreaDef
        {
            Shape = AreaShape.Mask,
            Cells = { new MaskCellDef { Offset = new HexCoord(1, 0) }, new MaskCellDef { Offset = new HexCoord(2, -1) }, new MaskCellDef { Offset = new HexCoord(0, 1) } },
        };
        var anchor = new HexCoord(0, 0);
        var west = AreaResolver.Resolve(mask, anchor, HexDirection.W).Select(c => c.Cell).ToList();
        Assert.Equal(new[] { new HexCoord(-1, 0), new HexCoord(-2, 1), new HexCoord(0, -1) }, west);
        var json = ContentJson.Serialize(mask);
        var back = ContentJson.Deserialize<AreaDef>(json)!;
        Assert.Equal(west, AreaResolver.Resolve(back, anchor, HexDirection.W).Select(c => c.Cell).ToList());
    }

    [Fact]
    public void BlockedByObstacles_ExcludesCellsBehindWalls()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field(10, 3);
        TestUtil.SetCell(map, new HexCoord(3, 1), "wall");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        var skill = new SkillDef
        {
            Id = "ray", Targeting = TargetingMode.Direction,
            Stages = new() { new SkillStageDef { Area = new AreaDef { Shape = AreaShape.Line, Size = 5 }, Effects = { new SkillEffectDef { Type = EffectType.Damage, Amount = 1 } } } },
        };
        var cells = sim.Skills.StageCells(u, skill, skill.Stages[0], u.Pos, HexDirection.E, -1, true).Select(c => c.Cell).ToList();
        Assert.Contains(new HexCoord(3, 1), cells);      // the wall cell itself
        Assert.DoesNotContain(new HexCoord(4, 1), cells); // behind the wall
    }

    [Fact]
    public void Line_WithoutPierce_StopsAtFirstTarget()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 3));
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        var e1 = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 1), HexDirection.W);
        var e2 = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(5, 1), HexDirection.W);
        var skill = new SkillDef
        {
            Id = "bolt", Targeting = TargetingMode.Direction, HitMode = HitMode.AlwaysHit,
            Stages = new() { new SkillStageDef { Pierce = false, Area = new AreaDef { Shape = AreaShape.Line, Size = 6 }, Effects = { new SkillEffectDef { Type = EffectType.Damage, Amount = 10, DamageType = "fire" } } } },
        };
        db.Add(skill);
        var cast = new SkillCast { Caster = u, Skill = skill, Anchor = u.Pos, Orientation = HexDirection.E, CasterPos = u.Pos, Released = true };
        sim.Skills.ResolveStage(cast, 0);
        Assert.True(e1.Hp < e1.HpMax);
        Assert.Equal(e2.HpMax, e2.Hp);
    }
}

public class CombatTests
{
    private static (Simulation sim, Unit att, Unit def) Duel(Action<ContentDatabase>? setup = null, HexDirection defenderFacing = HexDirection.W)
    {
        var db = TestUtil.Minimal();
        setup?.Invoke(db);
        var sim = TestUtil.Sim(db, TestUtil.Field(), seed: 7);
        var att = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var def = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), defenderFacing);
        return (sim, att, def);
    }

    [Fact]
    public void Resistances_Vulnerabilities_Immunity()
    {
        var (sim, att, def) = Duel(db =>
        {
            var d = db.Unit("dummy")!;
            d.Resistances["fire"] = 0.5;
            d.Resistances["ice"] = -0.5;
            d.Resistances["poison"] = 1.0;
        });
        Assert.Equal(10, sim.Combat.ComputeDamage(att, def, 20, "fire", null).Final);
        Assert.Equal(30, sim.Combat.ComputeDamage(att, def, 20, "ice", null).Final);
        var imm = sim.Combat.ComputeDamage(att, def, 20, "poison", null);
        Assert.True(imm.Immune);
        Assert.Equal(0, imm.Final);
    }

    [Fact]
    public void Armor_ReducesPhysical_WithMinimumFraction()
    {
        var (sim, att, def) = Duel(db => db.Unit("dummy")!.Stats[Stats.Armor] = 8);
        Assert.Equal(12, sim.Combat.ComputeDamage(att, def, 20, "physical", null).Final);
        // 10 - 8 = 2 < 10 * 0.2 = 2 → 2; 5 - 8 < 1 → min fraction 1.
        Assert.Equal(1, sim.Combat.ComputeDamage(att, def, 5, "physical", null).Final);
        Assert.Equal(20, sim.Combat.ComputeDamage(att, def, 20, "fire", null).Final); // fire ignores armor
    }

    [Fact]
    public void Detection_FrontNeverSurprised_RearAlwaysSurprised()
    {
        var (sim, att, def) = Duel(defenderFacing: HexDirection.W); // facing the attacker
        for (int i = 0; i < 200; i++)
        {
            var r = sim.Combat.Roll(att, def, null, att.Pos);
            Assert.Equal(AttackSector.Front, r.Sector);
            Assert.True(r.Noticed);
            Assert.False(r.Surprise);
        }
        var (sim2, att2, def2) = Duel(defenderFacing: HexDirection.E); // back to the attacker
        for (int i = 0; i < 200; i++)
        {
            var r = sim2.Combat.Roll(att2, def2, null, att2.Pos);
            Assert.Equal(AttackSector.Rear, r.Sector);
            Assert.False(r.Noticed);
            Assert.True(r.Surprise);
        }
    }

    [Fact]
    public void Detection_FlankProbabilities_MatchRules()
    {
        // Near flank: notice 70%, surprise 30% (one roll, disjoint).
        var (sim, att, def) = Duel(defenderFacing: HexDirection.NW); // attacker at W of defender → 1 step from NW
        int noticed = 0, surprise = 0, both = 0, n = 4000;
        for (int i = 0; i < n; i++)
        {
            var r = sim.Combat.Roll(att, def, null, att.Pos);
            Assert.Equal(AttackSector.NearFlank, r.Sector);
            if (r.Noticed) noticed++;
            if (r.Surprise) surprise++;
            if (r.Noticed && r.Surprise) both++;
        }
        Assert.Equal(0, both);
        Assert.InRange(noticed / (double)n, 0.66, 0.74);
        Assert.InRange(surprise / (double)n, 0.26, 0.34);
    }

    [Fact]
    public void UnnoticedAttack_IgnoresEvasion_ButCanStillMiss()
    {
        var (sim, att, def) = Duel(db => db.Unit("dummy")!.Stats[Stats.Evasion] = 0.5);
        double noticedHit = sim.Combat.HitChance(att, def, null, att.Pos, true, false);
        double unnoticedHit = sim.Combat.HitChance(att, def, null, att.Pos, false, false);
        Assert.Equal(0.35, noticedHit, 6);
        Assert.Equal(0.85, unnoticedHit, 6);
        Assert.True(unnoticedHit < 1.0);
        double surpriseHit = sim.Combat.HitChance(att, def, null, att.Pos, false, true);
        Assert.Equal(sim.Rules.Combat.MaxHit, surpriseHit, 6);
    }

    [Fact]
    public void HitFormula_Override_IsUsed()
    {
        var (sim, att, def) = Duel(db => db.Rules.Combat.HitFormula = "clamp(base - evasion * 2, 0, 1)");
        def.Stats.SetBase(Stats.Evasion, 0.2);
        Assert.Equal(0.45, sim.Combat.HitChance(att, def, null, att.Pos, true, false), 6);
    }

    [Fact]
    public void Height_GivesAccuracyBonus()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field();
        TestUtil.SetCell(map, new HexCoord(2, 2), "grass", 1);
        var sim = TestUtil.Sim(db, map);
        var att = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var def = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        Assert.Equal(0.85 - 0 + 0.05, sim.Combat.HitChance(att, def, null, att.Pos, true, false), 6);
    }

    [Fact]
    public void AttackCommand_DealsDamage_AndKills_StopsVictimQueue()
    {
        var (sim, att, def) = Duel(db => db.Rules.Combat.MinHit = 1.0);
        def.Hp = 5;
        sim.Enqueue(def, Command.Move(new HexCoord(6, 2)));
        // Hold the victim in place by giving it no AP.
        def.Ap = 0;
        sim.Enqueue(att, Command.Attack(def.Id));
        sim.RunSeconds(1);
        Assert.False(def.Alive);
        Assert.Equal(0, def.Queue.Count);
        Assert.Null(sim.Map.Get(def.Pos)!.Occupant);
        long apBefore = (long)(def.Ap * 1000);
        sim.RunSeconds(1);
        Assert.Equal(apBefore, (long)(def.Ap * 1000)); // dead units do not regenerate AP
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.Death);
    }

    [Fact]
    public void TargetDied_BeforeExecution_CommandIsSkipped()
    {
        var (sim, att, def) = Duel();
        sim.Enqueue(att, Command.Attack(def.Id));
        sim.Combat.Kill(def, null, "тест");
        sim.Step();
        Assert.Equal(0, att.Queue.Count);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.CommandSkipped);
    }

    [Fact]
    public void Preparation_CanBeInterrupted_ByStun_WithRefund()
    {
        var db = TestUtil.Minimal();
        db.Add(new StatusDef { Id = "stun", Name = "Оглушение", Duration = 1, Levels = new() { new StatusLevelDef { Threshold = 1, Flags = { BlocksActions = true, InterruptsCast = true } } } });
        db.Skill("hit")!.PrepTime = 2;
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var att = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var def = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        att.Ap = 10;
        sim.Enqueue(att, Command.Attack(def.Id));
        sim.Step();
        Assert.NotNull(att.Casting);
        double apAfterPay = att.Ap;
        sim.Statuses.Apply(att, db.Status("stun")!, 1, 0, def.Id, force: true);
        Assert.Null(att.Casting);
        Assert.Equal(apAfterPay + 2 * db.Skill("hit")!.Interrupt.Refund, att.Ap, 6);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.CastInterrupted);
        Assert.Equal(def.HpMax, def.Hp);
    }

    [Fact]
    public void SkillAtIntermediateWaypoint_ExecutesInOrder()
    {
        var db = TestUtil.Minimal();
        db.Rules.Combat.MinHit = 1.0;
        var sim = TestUtil.Sim(db, TestUtil.Field(12, 6));
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(0, 2), HexDirection.E);
        var target = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(4, 1), HexDirection.SW);
        sim.Enqueue(u, Command.Move(new HexCoord(3, 2)));
        sim.Enqueue(u, Command.Attack(target.Id));
        sim.Enqueue(u, Command.Move(new HexCoord(7, 2)));
        sim.Enqueue(u, Command.Defend());
        double hpBefore = target.Hp;
        TestUtil.RunUntil(sim, () => u.Pos == new HexCoord(7, 2), 15);
        Assert.True(target.Hp < hpBefore);
        Assert.Equal(new HexCoord(7, 2), u.Pos);
        sim.RunSeconds(0.5);
        Assert.True(u.Defending);
        // Attack happened when the unit was at (3,2), i.e. adjacent to the target.
        var dmg = sim.Log.Entries.First(e => e.Type == SimEventType.Damage);
        var moves = sim.Log.Entries.Where(e => e.Type == SimEventType.Moved && e.Tick < dmg.Tick).ToList();
        Assert.Equal(new HexCoord(3, 2), moves.Last().Cell);
    }

    [Fact]
    public void FriendlyFire_AreaHitsAlliesWhenFilterAllows_AndPreviewWarns()
    {
        var db = TestUtil.Minimal();
        var skill = new SkillDef
        {
            Id = "nova", Targeting = TargetingMode.Cell, RangeMin = 1, RangeMax = 5, HitMode = HitMode.AlwaysHit, ApCost = 1,
            Stages = new() { new SkillStageDef { Area = new AreaDef { Shape = AreaShape.Radius, Size = 1, IncludeCenter = true }, Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 10, Filter = TargetFilterDef.Everyone() } } } },
        };
        db.Add(skill);
        db.Unit("soldier")!.Skills.Add("nova");
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 2), HexDirection.E);
        var ally = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(4, 2), HexDirection.W);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(4, 3), HexDirection.W);
        var pv = sim.Planner.PreviewSkill(u, skill, u.Pos, u.Facing, new HexCoord(4, 2), -1, null);
        Assert.True(pv.Valid);
        Assert.Contains(ally, pv.Allies);
        Assert.Contains(enemy, pv.Enemies);
        Assert.Contains(pv.Warnings, w => w.Contains("союзника"));
        sim.Enqueue(u, Command.Skill("nova", new HexCoord(4, 2)));
        sim.RunSeconds(1.5);
        Assert.Equal(90, ally.Hp);
        Assert.Equal(90, enemy.Hp);
    }

    [Fact]
    public void ReactionChains_AreDepthLimited()
    {
        var db = TestUtil.Minimal();
        db.Add(new PassiveDef { Id = "mirror", Name = "Зеркало", Trigger = PassiveTrigger.OnDamaged, Action = PassiveAction.EffectsOnSource, Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 1, Filter = TargetFilterDef.Everyone() } } });
        db.Unit("soldier")!.Passives.Add("mirror");
        db.Unit("dummy")!.Passives.Add("mirror");
        db.Unit("soldier")!.Stats[Stats.HpMax] = 10000;
        db.Unit("dummy")!.Stats[Stats.HpMax] = 10000;
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var a = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var b = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        sim.Combat.DealDamage(a, b, 1, "fire", null, "тест");
        sim.Reactions.Process();
        double total = (a.HpMax - a.Hp) + (b.HpMax - b.Hp);
        Assert.True(total <= db.Rules.Limits.MaxReactionDepth + 2, $"total {total}");
        Assert.True(sim.Reactions.DroppedByDepth > 0);
    }
}
