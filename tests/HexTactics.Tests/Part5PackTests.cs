using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage D — packs and stacking groups (ТЗ 13.4, 14.2–14.3, T16; docs/DECISIONS_5.md §87).</summary>
public class Part5PackTests
{
    /// <summary>The outgoing damage bonus of the unit's acting passives, % (each passive once, stacks included).</summary>
    private static double Bonus(Simulation sim, Unit u) =>
        sim.Passives.Modifiers(u, ModifierTarget.OutgoingDamage).Where(x => x.M.Mode == ModifierMode.Percent).Sum(x => x.M.Value * x.Stacks);

    [Fact]
    public void T16_TheLeaderWithThreeWolves_Plus15_OneFalls_Plus10_AlliesOnlyInTheAura_NoDoubleBonus()
    {
        var db = TestUtil.Shipped();
        var sim = TestUtil.Sim(db, TestUtil.Field(16, 9));
        var leader = sim.Spawn(db.Unit("wolf_leader")!, "enemy", new HexCoord(5, 4), HexDirection.E);
        var near = new[] { new HexCoord(6, 4), new HexCoord(5, 5), new HexCoord(4, 4) }.Select(c => sim.Spawn(db.Unit("wolf")!, "enemy", c, HexDirection.E)).ToList();
        var far = sim.Spawn(db.Unit("wolf")!, "enemy", new HexCoord(13, 4), HexDirection.E);
        sim.RunSeconds(0.6);
        // «Сила стаи»: +5 % for each other wolf of the pack within 3 hexes — three of them.
        Assert.Equal(15, Bonus(sim, leader), 3);
        // «Вой вожака» (+15 %) only for the wolves in its aura; the one far away gets nothing.
        Assert.All(near, w => Assert.Equal(15, Bonus(sim, w), 3));
        Assert.Equal(0, Bonus(sim, far), 3);
        // One falls: +10 %.
        sim.Combat.Kill(near[0], null, "тест");
        sim.RunSeconds(0.6);
        Assert.Equal(10, Bonus(sim, leader), 3);
        // A second leader next to the same wolves: the howl acts once («один экземпляр»), not twice.
        var second = sim.Spawn(db.Unit("wolf_leader")!, "enemy", new HexCoord(6, 5), HexDirection.E);
        sim.RunSeconds(0.6);
        Assert.All(near.Skip(1), w => Assert.Equal(15, Bonus(sim, w), 3));
        Assert.Single(near[1].PassiveInstances, p => p.Def.Id == "leader_howl" && p.Active);
        Assert.True(second.Alive);
    }

    private static ContentDatabase Groups(StackGroupRule rule)
    {
        var db = TestUtil.Minimal();
        PassiveDef Howl(string id, double value, int radius = 3) => new()
        {
            Id = id, Name = id, AuraRadius = radius, AuraTypes = { "tag:pack" }, StackGroup = "howl", GroupRule = rule,
            Modifiers = { new ModifierDef { Target = ModifierTarget.OutgoingDamage, Mode = ModifierMode.Percent, Value = value } },
        };
        db.Add(Howl("howl_small", 10));
        db.Add(Howl("howl_big", 20));
        db.Add(new UnitDef { Id = "elder", Name = "Старый вожак", Faction = "enemy", Passives = { "howl_small" }, Tags = { "pack" } });
        db.Add(new UnitDef { Id = "alpha", Name = "Альфа", Faction = "enemy", Passives = { "howl_big" }, Tags = { "pack" } });
        db.Add(new UnitDef { Id = "pup", Name = "Волчонок", Faction = "enemy", Tags = { "pack" } });
        return db;
    }

    [Fact]
    public void StackingGroup_Highest_Latest_All_AndTheReasonIsShown()
    {
        double Of(StackGroupRule rule, out PassiveInstance? loser)
        {
            var db = Groups(rule);
            var sim = TestUtil.Sim(db, TestUtil.Field(12, 8));
            sim.Spawn(db.Unit("elder")!, "enemy", new HexCoord(3, 3), HexDirection.E);
            sim.Spawn(db.Unit("alpha")!, "enemy", new HexCoord(5, 3), HexDirection.E);
            var pup = sim.Spawn(db.Unit("pup")!, "enemy", new HexCoord(4, 3), HexDirection.E);
            sim.RunSeconds(0.6);
            loser = pup.PassiveInstances.FirstOrDefault(p => !p.Active && p.Inactive.Contains("группа «howl»"));
            return Bonus(sim, pup);
        }
        Assert.Equal(20, Of(StackGroupRule.Highest, out var l1), 3);
        Assert.Equal("howl_small", l1!.Def.Id);
        Assert.Contains("действует «howl_big»", l1.Inactive);
        // The latest source: the auras come by the owner's ID — the alpha was spawned second.
        Assert.Equal(20, Of(StackGroupRule.Latest, out _), 3);
        Assert.Equal(10, Of(StackGroupRule.First, out _), 3);
        Assert.Equal(30, Of(StackGroupRule.All, out var none), 3);
        Assert.Null(none);
    }

    [Fact]
    public void BetweenLeaders_TheSameAura_TheStrongerPackWins()
    {
        // One aura from two leaders, scaled by each leader's pack («за каждого»): «по экземпляру от каждого» + group «сильнейший».
        var db = TestUtil.Minimal();
        db.Add(new PassiveDef
        {
            Id = "rally", Name = "Сбор стаи", AuraRadius = 4, AuraTypes = { "tag:pack" }, Stacking = PassiveStacking.BySource, StackGroup = "rally",
            Modifiers =
            {
                new ModifierDef
                {
                    Target = ModifierTarget.OutgoingDamage, Mode = ModifierMode.Percent, Value = 4,
                    PerEach = new PerEachDef { Types = { "tag:pack" }, Radius = 2, MaxValue = 40 },
                },
            },
        });
        db.Add(new UnitDef { Id = "leader", Name = "Вожак", Faction = "enemy", Passives = { "rally" }, Tags = { "pack" } });
        db.Add(new UnitDef { Id = "pup", Name = "Волчонок", Faction = "enemy", Tags = { "pack" } });
        var sim = TestUtil.Sim(db, TestUtil.Field(14, 9));
        var weak = sim.Spawn(db.Unit("leader")!, "enemy", new HexCoord(3, 4), HexDirection.E);
        var strong = sim.Spawn(db.Unit("leader")!, "enemy", new HexCoord(8, 4), HexDirection.E);
        // The strong leader has three more pups around it.
        foreach (var c in new[] { new HexCoord(9, 4), new HexCoord(8, 5), new HexCoord(9, 3) }) sim.Spawn(db.Unit("pup")!, "enemy", c, HexDirection.E);
        var between = sim.Spawn(db.Unit("pup")!, "enemy", new HexCoord(6, 4), HexDirection.E);
        sim.RunSeconds(0.6);
        var acting = between.PassiveInstances.Where(p => p.Def.Id == "rally" && p.Active).ToList();
        Assert.Single(acting);
        Assert.Equal(strong.Id, acting[0].AuraOwner);
        Assert.True(Bonus(sim, between) > 4 * 2, "сбор сильной стаи должен быть больше");
        Assert.Contains(between.PassiveInstances, p => p.Def.Id == "rally" && !p.Active && p.AuraOwner == weak.Id);
        // Without the group both act (the old behaviour of «по экземпляру»).
        db.Get<PassiveDef>("rally")!.StackGroup = "";
        sim.RunSeconds(0.6);
        Assert.Equal(2, between.PassiveInstances.Count(p => p.Def.Id == "rally" && p.Active));
    }
}
