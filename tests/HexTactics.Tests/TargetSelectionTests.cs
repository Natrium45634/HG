using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 3, stage 3: automatic target choice and «target lost» policies (docs/DECISIONS_3.md §43).</summary>
public class TargetSelectionTests
{
    private static (Simulation Sim, Unit Me, List<Unit> Enemies) Setup(ulong seed = 42)
    {
        var db = TestUtil.Minimal();
        db.Add(new StatDef { Id = Stats.MagicPower, Name = "Маг. сила", Max = 1000 });
        db.Add(new StatusDef { Id = "marked", Name = "Метка", Duration = 30 });
        db.Add(new StatusDef
        {
            Id = "fire_weak", Name = "Уязвимость к огню", Duration = 30,
            Levels = { new StatusLevelDef { Threshold = 1, Defense = new DefenseDef { Modifiers = { new DamageModDef { Match = "fire", Percent = 50 } } } } },
        });
        db.Add(new SkillDef
        {
            Id = "bolt", Name = "Стрела", Targeting = TargetingMode.Unit, RangeMax = 8, Ranged = true, ApCost = 1, PrepTime = 0, RecoveryTime = 0.1,
            UseMode = SkillUseMode.Both,
            Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 20 } } } },
        });
        var db2 = db;
        var sim = TestUtil.Sim(db2, TestUtil.Field(16, 10), seed);
        var me = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 4), HexDirection.E);
        me.Skills.Add(db.Skill("bolt")!);
        var e = new List<Unit>();
        e.Add(sim.Spawn(db.Unit("dummy")!, null, new HexCoord(4, 4), HexDirection.W)); // 0: nearest
        e.Add(sim.Spawn(db.Unit("dummy")!, null, new HexCoord(9, 4), HexDirection.W)); // 1: farthest
        e.Add(sim.Spawn(db.Unit("dummy")!, null, new HexCoord(6, 2), HexDirection.W)); // 2
        e.Add(sim.Spawn(db.Unit("dummy")!, null, new HexCoord(6, 6), HexDirection.W)); // 3
        sim.Vision.Recompute();
        return (sim, me, e);
    }

    private static Unit? Pick(Simulation sim, Unit me, List<Unit> pool, params AutoTargetRuleDef[] rules) =>
        sim.Targets.Choose(me, pool, rules, TargetSelectionMode.Sequential, false, me.Skills[0], -1, out _);

    private static AutoTargetRuleDef R(AutoTargetRule r) => new() { Rule = r };

    [Fact]
    public void Criteria_PickTheRightTarget()
    {
        var (sim, me, e) = Setup();
        e[2].Hp = 30;
        e[3].Stats.SetBase(Stats.HpMax, 300);
        e[3].Hp = 250;
        e[1].Stats.SetBase(Stats.Power, 50);
        e[0].Stats.SetBase(Stats.MagicPower, 40);
        e[1].Tag = "archer_t";
        sim.Statuses.Apply(e[3], sim.Content.Status("marked")!, 1, 0, me.Id);
        Assert.Equal(e[0], Pick(sim, me, e, R(AutoTargetRule.Nearest)));
        Assert.Equal(e[1], Pick(sim, me, e, R(AutoTargetRule.Farthest)));
        Assert.Equal(e[2], Pick(sim, me, e, R(AutoTargetRule.LowestHp)));
        Assert.Equal(e[3], Pick(sim, me, e, R(AutoTargetRule.HighestHp)));
        Assert.Equal(e[3], Pick(sim, me, e, R(AutoTargetRule.HighestMaxHp)));
        Assert.Equal(e[1], Pick(sim, me, e, R(AutoTargetRule.HighestPower)));
        Assert.Equal(e[0], Pick(sim, me, e, R(AutoTargetRule.HighestMagicPower)));
        Assert.Equal(e[1], Pick(sim, me, e, new AutoTargetRuleDef { Rule = AutoTargetRule.WithTag, Tag = "archer_t" }));
        Assert.Equal(e[3], Pick(sim, me, e, new AutoTargetRuleDef { Rule = AutoTargetRule.WithStatus, Status = "marked" }));
        e[2].Hp = 15;
        Assert.Equal(e[2], Pick(sim, me, e, R(AutoTargetRule.Killable), R(AutoTargetRule.Nearest)));
        // Vulnerable to the skill's fire.
        sim.Statuses.Apply(e[1], sim.Content.Status("fire_weak")!, 1, 0, me.Id);
        Assert.Equal(e[1], Pick(sim, me, e, R(AutoTargetRule.Vulnerable), R(AutoTargetRule.Nearest)));
        // Most enemies near the target: e[2] and e[3] are 4 apart; put another one next to e[3].
        var extra = sim.Spawn(sim.Content.Unit("dummy")!, null, new HexCoord(7, 6), HexDirection.W);
        var all = e.Append(extra).ToList();
        var crowd = Pick(sim, me, all, new AutoTargetRuleDef { Rule = AutoTargetRule.MostEnemiesNearby, Radius = 1 }, R(AutoTargetRule.Nearest));
        Assert.True(crowd == e[3] || crowd == extra);
    }

    [Fact]
    public void SpecExample_BossInRange_ElseLowestHp_ThenNearest()
    {
        var (sim, me, e) = Setup();
        var rules = new[] { R(AutoTargetRule.BossOrElite), R(AutoTargetRule.LowestHp), R(AutoTargetRule.Nearest) };
        e[2].Hp = 40;
        e[3].Hp = 40;
        // No boss: the lowest health; two equal → the nearest.
        var pick = Pick(sim, me, e, rules);
        Assert.True(pick == e[2] || pick == e[3]);
        Assert.Equal(HexCoord.Distance(me.Pos, e[2].Pos) <= HexCoord.Distance(me.Pos, e[3].Pos) ? e[2] : e[3], pick);
        // A boss in the pool wins.
        e[1].Tag = "boss";
        Assert.Equal(e[1], Pick(sim, me, e, rules));
        // A strict filter with nobody matching → no target.
        e[1].Tag = "";
        Assert.Null(Pick(sim, me, e, new AutoTargetRuleDef { Rule = AutoTargetRule.BossOrElite, Strict = true }));
    }

    [Fact]
    public void RandomAmongEquals_IsReproducible_AndVaries()
    {
        var picks = new HashSet<int>();
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var (sim, me, e) = Setup(seed);
            var a = sim.Targets.Choose(me, e, new[] { R(AutoTargetRule.Random) }, TargetSelectionMode.Sequential, true, null, -1, out _);
            var (sim2, me2, e2) = Setup(seed);
            var b = sim2.Targets.Choose(me2, e2, new[] { R(AutoTargetRule.Random) }, TargetSelectionMode.Sequential, true, null, -1, out _);
            Assert.Equal(e.IndexOf(a!), e2.IndexOf(b!));
            picks.Add(e.IndexOf(a!));
        }
        Assert.True(picks.Count >= 2);
    }

    [Fact]
    public void AutomaticSkill_ChoosesByRules_AndHits()
    {
        var (sim, me, e) = Setup();
        var bolt = me.Skills[0];
        bolt.AutoTarget.Rules = new() { R(AutoTargetRule.LowestHp) };
        e[1].Hp = 50;
        var cmd = Command.Skill("bolt", null);
        cmd.AutoTarget = true;
        Assert.Null(sim.Enqueue(me, cmd));
        var est = sim.Planner.ProjectQueue(me)[0];
        Assert.Contains(e[1].Name, est.Estimate.Problem);
        TestUtil.RunUntil(sim, () => e[1].Hp < 50, 3);
        Assert.Equal(30, e[1].Hp, 6);
        Assert.Equal(e[1].Id, cmd.TargetUnitId);
        Assert.Contains("LowestHp", cmd.TargetChoice);
    }

    [Fact]
    public void TargetLost_Cancel_SpendsNothing_NewTarget_PicksAnother()
    {
        var (sim, me, e) = Setup();
        var bolt = me.Skills[0];
        bolt.PrepTime = 0.5;
        bolt.Cooldown = 10;
        // Cancel (default): the target dies before the release — nothing is spent, no cooldown.
        me.Ap = 5;
        sim.Enqueue(me, Command.Skill("bolt", null, e[0].Id));
        sim.Step();
        Assert.NotNull(me.Casting);
        sim.Combat.Kill(e[0], null, "тест");
        TestUtil.RunUntil(sim, () => me.Queue.Count == 0, 2);
        Assert.Equal(0, me.Queue.Count);
        Assert.True(me.Ap >= 5 - 1e-9, "ОД вернулись");
        Assert.Equal(0, me.CooldownRemaining("bolt", sim.Tick));

        // New target: another enemy is attacked.
        bolt.OnTargetLost = TargetLostPolicy.NewTarget;
        sim.Enqueue(me, Command.Skill("bolt", null, e[1].Id));
        sim.Step();
        sim.Combat.Kill(e[1], null, "тест");
        TestUtil.RunUntil(sim, () => e.Skip(2).Any(x => x.Hp < x.HpMax), 3);
        Assert.Contains(e.Skip(2), x => x.Hp < x.HpMax);
        Assert.Contains(sim.Log.Entries, x => x.Text.Contains("новая цель"));
    }

    [Fact]
    public void TargetLost_WaitReturn_AndLastKnownPosition()
    {
        var (sim, me, e) = Setup();
        var bolt = me.Skills[0];
        me.Stats.SetBase(Stats.DetectionRange, 8);
        // Hide e[1] beyond the detection range.
        bolt.OnTargetLost = TargetLostPolicy.WaitReturn;
        sim.Relocate(e[1], new HexCoord(15, 9));
        sim.Vision.Recompute();
        Assert.False(sim.Vision.CanFactionSee(me.FactionId, e[1]));
        var cmd = Command.Skill("bolt", null, e[1].Id);
        sim.Enqueue(me, cmd);
        sim.RunSeconds(1);
        Assert.Equal(CommandStatus.Waiting, cmd.Status);
        Assert.Equal(e[1].HpMax, e[1].Hp);
        sim.Relocate(e[1], new HexCoord(8, 4));
        sim.Vision.Recompute();
        TestUtil.RunUntil(sim, () => e[1].Hp < e[1].HpMax, 2);
        Assert.True(e[1].Hp < e[1].HpMax, "цель вернулась — навык применён");

        // Last known position: the unit walks to where the target was seen.
        bolt.OnTargetLost = TargetLostPolicy.LastKnown;
        bolt.PrepTime = 0.5;
        var cmd2 = Command.Skill("bolt", null, e[3].Id);
        sim.Enqueue(me, cmd2);
        sim.Step();
        var seen = e[3].Pos;
        sim.Relocate(e[3], new HexCoord(15, 0));
        sim.Vision.Recompute();
        sim.RunSeconds(6);
        Assert.True(HexCoord.Distance(me.Pos, seen) <= 1 || me.Pos != new HexCoord(2, 4), "юнит пошёл к последней известной позиции цели: " + string.Join("\n", sim.Log.Entries.Where(x => x.SourceUnit == me.Id).Select(x => x.Text).TakeLast(15)));
    }
}
