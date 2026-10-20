using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 3, stage 5: skills as sequences — series of hits, conditions, costs, defensive skills (docs/DECISIONS_3.md §45).</summary>
public class SeriesTests
{
    private static SkillStageDef Hit(double delay, double mult, double amount = 10) => new()
    {
        Delay = delay, DamageMultiplier = mult, Area = new AreaDef { Shape = AreaShape.Single },
        Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = amount } },
    };

    /// <summary>«Три удара мечом»: 70 %, 85 %, 120 %, always hits, no crits (exact numbers).</summary>
    private static SkillDef Triple(string id = "triple") => new()
    {
        Id = id, Name = "Три удара мечом", Targeting = TargetingMode.Unit, RangeMax = 1, ApCost = 2, PrepTime = 0.2, RecoveryTime = 0.1,
        HitMode = HitMode.AlwaysHit, CanCrit = false, HoldDuringSeries = true,
        Stages = { Hit(0, 0.7), Hit(0.3, 0.85), Hit(0.6, 1.2) },
    };

    private static (Simulation Sim, Unit Caster, Unit Target) Setup(ContentDatabase db, double targetHp = 100, ulong seed = 42)
    {
        db.Get<UnitDef>("dummy")!.Stats[Stats.HpMax] = targetHp;
        var sim = TestUtil.Sim(db, TestUtil.Field(), seed);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var t = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        return (sim, u, t);
    }

    /// <summary>Starts and releases the skill directly (no command), then lets the series play out.</summary>
    private static SkillCast Cast(Simulation sim, Unit u, SkillDef s, Unit? target, double seconds = 2)
    {
        var check = sim.Skills.Validate(u, s, target?.Pos, target?.Id ?? -1, null, u.Pos, u.Facing);
        Assert.True(check.Ok, check.Message);
        var cast = sim.Skills.Begin(u, s, check, target?.Id ?? -1);
        sim.Skills.Release(cast);
        sim.RunSeconds(seconds);
        return cast;
    }

    [Fact]
    public void ThreeSwordStrikes_EachHitHasItsOwnMultiplier()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        db.Add(s);
        var (sim, u, t) = Setup(db);
        var cast = Cast(sim, u, s, t);
        var hits = cast.Series.Hits;
        Assert.Equal(3, hits.Count);
        Assert.Equal(new[] { 7.0, 9.0, 12.0 }, hits.Select(h => h.Damage).ToArray()); // 10 × 0.7, 0.85 (8.5 → 9), 1.2
        Assert.Equal(100 - 28, t.Hp);
        Assert.Equal(new[] { 1, 2, 3 }, hits.Select(h => h.HitNumber).ToArray());
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.SkillHit && e.Text.Contains("удар 2 из 3"));
    }

    [Fact]
    public void EveryHitRollsSeparately()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        s.HitMode = HitMode.Roll;
        db.Add(s);
        db.Get<UnitDef>("dummy")!.Stats[Stats.Evasion] = 0.45;
        int mixed = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (sim, u, t) = Setup(db.Clone(), seed: seed);
            var cast = Cast(sim, u, sim.Content.Skill("triple")!, t);
            Assert.Equal(3, cast.Series.Hits.Count);
            if (cast.Series.Hits.Select(h => h.Hit).Distinct().Count() > 1) mixed++;
        }
        Assert.True(mixed > 0, "в некоторых сериях часть ударов должна промахнуться, часть — попасть");
    }

    [Fact]
    public void TargetDiesAfterSecondHit_ThirdIsCancelled()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        db.Add(s);
        var (sim, u, t) = Setup(db, targetHp: 15);
        var other = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 3), HexDirection.W);
        var cast = Cast(sim, u, s, t);
        Assert.False(t.Alive);
        Assert.Equal(2, cast.Series.Hits.Count);
        Assert.True(cast.Series.Stopped);
        Assert.Contains("погибла", cast.Series.StopReason);
        Assert.Equal(other.HpMax, other.Hp); // no silent redirection
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.SeriesStopped);
    }

    [Fact]
    public void Redirect_OnlyWhenExplicitlySet()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        s.RetargetOnDeath = RetargetRule.Nearest;
        db.Add(s);
        var (sim, u, t) = Setup(db, targetHp: 15);
        var other = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 1), HexDirection.W);
        var cast = Cast(sim, u, s, t);
        Assert.False(t.Alive);
        Assert.Equal(3, cast.Series.Hits.Count);
        Assert.Equal(other.Id, cast.Series.Hits[2].TargetId);
        Assert.Equal(other.HpMax - 12, other.Hp);
    }

    [Fact]
    public void SeriesCrit_OnceForTheWholeSeries_OrPerHit()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        s.CanCrit = true;
        s.SeriesCrit = SeriesCritMode.OncePerSeries;
        db.Add(s);
        var per = Triple("per_hit");
        per.CanCrit = true;
        db.Add(per);
        db.Get<UnitDef>("soldier")!.Stats[Stats.CritChance] = 0.5;
        int mixedPerHit = 0;
        for (ulong seed = 1; seed <= 16; seed++)
        {
            var (sim, u, t) = Setup(db.Clone(), targetHp: 1000, seed: seed);
            var once = Cast(sim, u, sim.Content.Skill("triple")!, t);
            Assert.Single(once.Series.Hits.Select(h => h.Crit).Distinct());
            u.CooldownUntil.Clear();
            var ph = Cast(sim, u, sim.Content.Skill("per_hit")!, t);
            if (ph.Series.Hits.Select(h => h.Crit).Distinct().Count() > 1) mixedPerHit++;
        }
        Assert.True(mixedPerHit > 0);
    }

    [Fact]
    public void CostPerHit_RunsOut_RemainingHitsCancelled_NoHiddenNegative()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        foreach (var st in s.Stages) st.Costs.Add(new StageCostDef { Resource = "mana", Amount = 2 });
        db.Add(s);
        db.Get<UnitDef>("soldier")!.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 10, Start = 5 });
        var (sim, u, t) = Setup(db);
        var cast = Cast(sim, u, s, t);
        Assert.Equal(2, cast.Series.Hits.Count);
        Assert.Equal(1, u.ResourceValue("mana"), 3);
        Assert.Contains("не хватает", cast.Series.StopReason);
    }

    [Fact]
    public void CostPerHit_Debt_GoesVisiblyNegative_DownToTheMinimum()
    {
        var db = TestUtil.Minimal();
        db.Get<ResourceDef>("mana")!.MinValue = -10;
        var s = Triple();
        s.OnResourceOut = ResourceOutPolicy.Debt;
        foreach (var st in s.Stages) st.Costs.Add(new StageCostDef { Resource = "mana", Amount = 4 });
        db.Add(s);
        db.Get<UnitDef>("soldier")!.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 10, Start = 5 });
        var (sim, u, t) = Setup(db);
        var cast = Cast(sim, u, s, t, 0.8);
        Assert.Equal(3, cast.Series.Hits.Count);
        Assert.Equal(-7, u.ResourceValue("mana"), 3);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("в долг"));
    }

    [Fact]
    public void NotEnoughForTheFirstHit_SkillDoesNotStart()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        s.Stages[0].Costs.Add(new StageCostDef { Resource = "mana", Amount = 6 });
        db.Add(s);
        db.Get<UnitDef>("soldier")!.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 10, Start = 5 });
        var (sim, u, t) = Setup(db);
        var check = sim.Skills.Validate(u, s, t.Pos, t.Id, null, u.Pos, u.Facing);
        Assert.Equal(FailureReason.NotEnoughResource, check.Reason);
    }

    [Fact]
    public void StageConditions_And_Or_Not()
    {
        var db = TestUtil.Minimal();
        db.Add(new StatusDef { Id = "burning", Name = "Горение", Duration = 10, Levels = { new StatusLevelDef { Threshold = 1 } } });
        var s = Triple();
        // Second hit only if the target burns; third only if it does NOT burn AND (the previous hit landed OR the caster is wounded).
        s.Stages[1].Condition = new UnitConditionDef { Type = UnitConditionType.HasStatus, Status = "burning" };
        s.Stages[2].Condition = new UnitConditionDef
        {
            Type = UnitConditionType.All,
            Children =
            {
                new UnitConditionDef { Type = UnitConditionType.Not, Children = { new UnitConditionDef { Type = UnitConditionType.HasStatus, Status = "burning" } } },
                new UnitConditionDef
                {
                    Type = UnitConditionType.Any,
                    Children =
                    {
                        new UnitConditionDef { Type = UnitConditionType.PreviousHit },
                        new UnitConditionDef { Type = UnitConditionType.HpFraction, Subject = ConditionSubject.Self, Compare = CompareMode.AtMost, Value = 0.5 },
                    },
                },
            },
        };
        db.Add(s);
        var (sim, u, t) = Setup(db);
        var cast = Cast(sim, u, s, t);
        Assert.Equal(new[] { 1, 3 }, cast.Series.Hits.Select(h => sim.Content.Skill("triple")!.Stages.IndexOf(s.Stages[h.Stage]) + 1).ToArray());
        Assert.False(cast.Series.Stopped);

        var stop = Triple("stopper");
        stop.Stages[1].Condition = new UnitConditionDef { Type = UnitConditionType.HasStatus, Status = "burning" };
        stop.Stages[1].OnConditionFail = ConditionFailAction.StopSeries;
        db.Add(stop);
        var (sim2, u2, t2) = Setup(db);
        var c2 = Cast(sim2, u2, stop, t2);
        Assert.Single(c2.Series.Hits);
        Assert.True(c2.Series.Stopped);
    }

    [Fact]
    public void RepeatedStage_FiveArrows_AtTheirOwnMoments()
    {
        var db = TestUtil.Minimal();
        var s = new SkillDef
        {
            Id = "volley", Name = "Серия стрел", Targeting = TargetingMode.Unit, RangeMax = 6, Ranged = true, ApCost = 2, HitMode = HitMode.AlwaysHit, CanCrit = false,
            Stages = { Hit(0, 0.5) },
        };
        s.Stages[0].Repeat = 5;
        s.Stages[0].RepeatInterval = 0.25;
        db.Add(s);
        var (sim, u, t) = Setup(db);
        var cast = Cast(sim, u, s, t);
        Assert.Equal(5, cast.Series.Hits.Count);
        Assert.Equal(100 - 25, t.Hp);
        var ticks = sim.Log.Entries.Where(e => e.Type == SimEventType.SkillHit).Select(e => e.Tick).ToList();
        Assert.Equal(5, ticks.Distinct().Count());
        Assert.Equal(sim.TicksFor(1.0), ticks[^1] - ticks[0]);
    }

    [Fact]
    public void HoldDuringSeries_NextOrderWaitsForTheLastHit()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        db.Add(s);
        db.Get<UnitDef>("soldier")!.Skills.Add("triple");
        var (sim, u, t) = Setup(db);
        Assert.Null(sim.Enqueue(u, Command.Skill("triple", null, t.Id)));
        sim.Enqueue(u, Command.Move(new HexCoord(2, 4)));
        long lastHit = -1, firstStep = -1;
        for (int i = 0; i < sim.TicksFor(5); i++)
        {
            sim.Step();
            if (lastHit < 0 && t.Hp <= 100 - 28) lastHit = sim.Tick;
            if (firstStep < 0 && u.StepTo != null) firstStep = sim.Tick;
        }
        Assert.True(lastHit > 0 && firstStep > 0, $"последний удар {lastHit}, шаг {firstStep}, HP {t.Hp}, позиция {u.Pos}, очередь {u.Queue.Count}");
        Assert.True(firstStep > lastHit, $"движение началось на тике {firstStep}, до последнего удара ({lastHit})");
    }

    [Fact]
    public void DashToTarget_HitsAtTheEndPoint_ThenFinalRetreat()
    {
        var db = TestUtil.Minimal();
        var s = new SkillDef
        {
            Id = "dash", Name = "Рывок", Targeting = TargetingMode.Unit, RangeMax = 5, ApCost = 2, HitMode = HitMode.AlwaysHit, CanCrit = false,
            Stages = { Hit(0, 1.0) },
        };
        s.Stages[0].Move = StageMove.DashToTarget;
        s.Stages[0].MoveDistance = 4;
        s.Stages.Add(new SkillStageDef { Delay = 0.3, Move = StageMove.Retreat, MoveDistance = 1, Final = true });
        db.Add(s);
        var (sim, u, t) = Setup(db);
        sim.Relocate(t, new HexCoord(6, 2));
        var cast = Cast(sim, u, s, t, 0.1);
        Assert.Equal(1, HexCoord.Distance(u.Pos, t.Pos));
        Assert.Equal(90, t.Hp);
        sim.RunSeconds(0.5);
        Assert.Equal(2, HexCoord.Distance(u.Pos, t.Pos));
    }

    [Fact]
    public void FreeIf_LaunchCostNotPaid()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        s.ApCost = 5;
        s.FreeIf = new UnitConditionDef { Type = UnitConditionType.HpFraction, Subject = ConditionSubject.Target, Compare = CompareMode.AtMost, Value = 0.3 };
        db.Add(s);
        var (sim, u, t) = Setup(db);
        double ap = u.Ap;
        Cast(sim, u, s, t, 0.01);
        Assert.InRange(u.Ap, ap - 5, ap - 4.8);
        t.Hp = 20;
        u.CooldownUntil.Clear();
        ap = u.Ap;
        var cast = Cast(sim, u, s, t, 0.01);
        Assert.True(cast.Free);
        Assert.InRange(u.Ap, ap, ap + 0.2);
    }

    [Fact]
    public void Validator_RepeatsAndLongSeries()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        s.Stages[0].Repeat = 101;
        db.Add(s);
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Source.EndsWith(":triple") && i.Message.Contains("больше 100"));
        s.Stages[0].Repeat = 11;
        issues = ContentValidator.Validate(db);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error && i.Source.EndsWith(":triple"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Source.EndsWith(":triple") && i.Message.Contains("13 ударов"));
        s.OnResourceOut = ResourceOutPolicy.Debt;
        s.Stages[0].Costs.Add(new StageCostDef { Resource = "mana", Amount = 1 });
        issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Source.EndsWith(":triple") && i.Message.Contains("долг невозможен"));
    }

    // ------------------------------------------------------------------ defensive skills

    private static SkillDef Wall() => new()
    {
        Id = "wall", Name = "Стена щитов", Kind = SkillKind.Defense, Targeting = TargetingMode.Self, ApCost = 1, PrepTime = 0, RecoveryTime = 0,
        Stages = { new SkillStageDef() },
        Defense = new DefenseStanceDef { Enabled = true, DamageReduction = 50, Sectors = { AttackSector.Front, AttackSector.NearFlank } },
    };

    private static (Simulation, Unit Defender, Unit Attacker) Duel(ContentDatabase db, HexDirection defenderFacing)
    {
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var d = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(3, 2), defenderFacing);
        var a = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(4, 2), HexDirection.W);
        return (sim, d, a);
    }

    private static double HitOnce(Simulation sim, Unit attacker, Unit target, SkillDef? skill = null)
    {
        double before = target.Hp;
        var s = skill ?? sim.Content.Skill("hit")!;
        sim.Damage.Apply(new DamageRequest { Attacker = attacker, Target = target, Amount = 20, DamageType = "physical", Source = DamageSource.Skill, Skill = s,
            Roll = new AttackRoll { Hit = true, Noticed = true, Sector = CombatSystem.SectorFor(target, attacker.Pos) } });
        return before - target.Hp;
    }

    [Fact]
    public void DefenseSkill_ReducesFrontalDamage_NotFromTheRear()
    {
        var db = TestUtil.Minimal();
        db.Add(Wall());
        db.Get<UnitDef>("soldier")!.DefenseSkill = "wall";
        var (sim, d, a) = Duel(db, HexDirection.E);
        Assert.Null(sim.Enqueue(d, Command.Defend()));
        sim.RunSeconds(0.2);
        Assert.NotNull(d.Stance);
        Assert.True(d.Defending);
        Assert.Equal(10, HitOnce(sim, a, d));
        d.Facing = HexDirection.W; // the attacker is now behind
        Assert.Equal(20, HitOnce(sim, a, d));
    }

    [Fact]
    public void DefenseSkill_EndsOnNewOrder_Stun_UsedBlocks_AndUpkeep()
    {
        var db = TestUtil.Minimal();
        var wall = Wall();
        wall.Defense.MaxBlocks = 2;
        db.Add(wall);
        db.Get<UnitDef>("soldier")!.DefenseSkill = "wall";
        var (sim, d, a) = Duel(db, HexDirection.E);
        sim.Enqueue(d, Command.Defend());
        sim.RunSeconds(0.2);
        HitOnce(sim, a, d);
        Assert.NotNull(d.Stance);
        HitOnce(sim, a, d);
        Assert.Null(d.Stance); // two attacks — exhausted
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.DefenseEnded && e.Text.Contains("исчерпана"));

        wall.Defense.MaxBlocks = 0;
        sim.Enqueue(d, Command.Defend());
        sim.RunSeconds(0.2);
        Assert.NotNull(d.Stance);
        sim.Enqueue(d, Command.Turn(HexDirection.W));
        sim.RunSeconds(0.2);
        Assert.Null(d.Stance); // a new order

        wall.Defense.Upkeep.Add(new StageCostDef { Resource = "mana", Amount = 5 });
        d.Resources["mana"] = new ResourceState { Def = db.Get<ResourceDef>("mana")!, Max = 100, Current = 2 };
        sim.Enqueue(d, Command.Defend());
        sim.RunSeconds(1.5);
        Assert.Null(d.Stance); // 2 mana for 5/s
        Assert.True(d.ResourceValue("mana") >= 0);
    }

    [Fact]
    public void DefenseSkill_PiercingSkillIgnoresIt_DodgeAvoidsHits()
    {
        var db = TestUtil.Minimal();
        db.Add(Wall());
        var pierce = new SkillDef { Id = "pierce", Name = "Пробой", Targeting = TargetingMode.Unit, PiercesDefense = true, Stages = { Hit(0, 1) } };
        db.Add(pierce);
        db.Get<UnitDef>("soldier")!.DefenseSkill = "wall";
        var (sim, d, a) = Duel(db, HexDirection.E);
        sim.Enqueue(d, Command.Defend());
        sim.RunSeconds(0.2);
        Assert.Equal(20, HitOnce(sim, a, d, pierce));

        var dodge = Wall();
        dodge.Id = "dodge";
        dodge.Defense = new DefenseStanceDef { Enabled = true, DodgeChance = 1, Sectors = new() };
        db.Add(dodge);
        db.Get<UnitDef>("soldier")!.DefenseSkill = "dodge";
        var (sim2, d2, a2) = Duel(db, HexDirection.W);
        sim2.Enqueue(d2, Command.Defend());
        sim2.RunSeconds(0.2);
        Assert.Equal(0, HitOnce(sim2, a2, d2));
        Assert.Contains(sim2.Log.Entries, e => e.Type == SimEventType.DefenseAvoided);
    }

    [Fact]
    public void Series_IsDeterministic_UnderPauseAndSpeedChunks()
    {
        var db = TestUtil.Minimal();
        var s = Triple();
        s.HitMode = HitMode.Roll;
        s.CanCrit = true;
        db.Add(s);
        db.Get<UnitDef>("dummy")!.Stats[Stats.Evasion] = 0.3;
        db.Get<UnitDef>("soldier")!.Skills.Add("triple");
        string Run(int chunk)
        {
            var (sim, u, t) = Setup(db.Clone(), seed: 7);
            Assert.Null(sim.Enqueue(u, Command.Skill("triple", null, t.Id)));
            for (int i = 0; i < 60; i += chunk) sim.Run(chunk); // ×1 by single ticks, ×4 in chunks of four, a pause in between costs nothing
            return string.Join(";", sim.Log.Entries.Where(e => e.Type is SimEventType.SkillHit or SimEventType.Damage or SimEventType.Miss).Select(e => $"{e.Tick}:{e.Text}"));
        }
        var a = Run(1);
        Assert.Equal(a, Run(4));
        Assert.True(a.Contains("удар 3 из 3"), a);
    }

    [Fact]
    public void ShippedContent_ThreeSwordStrikes_AndDefensiveSkills()
    {
        var db = TestUtil.Shipped();
        var s = db.Skill("triple_strike")!;
        Assert.Equal(new[] { 0.7, 0.85, 1.2 }, s.Stages.Select(x => x.DamageMultiplier).ToArray());
        Assert.Equal("shield_wall", db.Unit("knight")!.DefenseSkill);
        Assert.True(db.Skill("shield_wall")!.Defense.Enabled);
        Assert.Equal(5, db.Skill("arrow_volley")!.Stages[0].Repeat);
        var issues = ContentValidator.Validate(db);
        foreach (var id in new[] { "triple_strike", "reckless_assault", "shield_wall", "arrow_volley", "piercing_shot", "dash_strike", "evade", "inferno", "mana_ward", "healing_ward", "mirror_stance" })
            Assert.DoesNotContain(issues, i => i.Source.EndsWith(":" + id) && i.Severity == IssueSeverity.Error);
    }
    [Fact]
    public void EditorTestRun_ShowsEveryHit_WithoutTouchingContent()
    {
        var db = TestUtil.Shipped();
        int skills = db.All<SkillDef>().Count();
        var r = SkillTestRun.Run(db, db.Skill("triple_strike")!, new SkillTestOptions { Targets = 2, TargetHp = 500, Seed = 3 });
        Assert.True(r.Started, r.Problem);
        Assert.True(3 == r.Hits.Select(h => h.HitNumber).Distinct().Count(), string.Join("\n", r.Log));
        Assert.Contains(r.Log, l => l.Contains("удар 3 из 3"));
        Assert.NotEmpty(r.Breakdowns);
        Assert.Equal(skills, db.All<SkillDef>().Count());
        Assert.Null(db.Unit("__preview_caster"));

        var wall = SkillTestRun.Run(db, db.Skill("shield_wall")!, new SkillTestOptions { Targets = 1, Seed = 3 });
        Assert.True(wall.Started, wall.Problem);
        Assert.Contains(wall.Breakdowns, b => b.Contains("защита"));
    }
}
