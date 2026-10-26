using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage D — the undead and the holy light (T14), the marks of characteristics (ТЗ 12.5); docs/DECISIONS_5.md §87.</summary>
public class Part5ContentTests
{
    [Fact]
    public void T14_TheUndeadTake125PercentOfHolyDamage_WithoutTheCategoryTheEffectIsGone()
    {
        var db = TestUtil.Shipped();
        db.Add(new UnitDef { Id = "ghoul", Name = "Упырь", Faction = "enemy", MainCategory = "undead", Stats = { [Stats.HpMax] = 500 } });
        db.Add(new UnitDef { Id = "ghoul_plain", Name = "Упырь без категории", Faction = "enemy", Stats = { [Stats.HpMax] = 500 } });
        var sim = TestUtil.Sim(db, TestUtil.Field(8, 4));
        var undead = sim.Spawn(db.Unit("ghoul")!, "enemy", new HexCoord(2, 1), HexDirection.E);
        var plain = sim.Spawn(db.Unit("ghoul_plain")!, "enemy", new HexCoord(5, 1), HexDirection.E);
        sim.Step();
        Assert.Contains(undead.PassiveInstances, p => p.Def.Id == "holy_weakness" && p.Active);
        double Hit(Unit u)
        {
            double before = u.Hp;
            sim.Damage.Apply(new DamageRequest { Target = u, Amount = 100, DamageType = "light", Cause = "тест" });
            return before - u.Hp;
        }
        Assert.Equal(125, Hit(undead), 3);
        Assert.Equal(100, Hit(plain), 3);
        // Other damage is not changed.
        double before = undead.Hp;
        sim.Damage.Apply(new DamageRequest { Target = undead, Amount = 40, DamageType = "fire", Cause = "тест" });
        Assert.Equal(40, before - undead.Hp, 3);
    }

    [Fact]
    public void Characteristics_AreMarked_Core_Content_OrWithoutBehaviour()
    {
        var db = TestUtil.Shipped();
        Assert.Equal(StatUse.Core, StatUsage.Of(db, Stats.HpMax).Use);
        Assert.Equal(StatUse.Core, StatUsage.Of(db, Stats.Weight).Use);
        Assert.Equal(StatUse.Core, StatUsage.Of(db, "resist_light").Use);
        Assert.Contains("формуле урона", StatUsage.Of(db, "resist_light").Text);
        // A custom one: nothing reads it — no effect «сама собой».
        db.Add(new StatDef { Id = "luck", Name = "Удача" });
        db.Add(new UnitDef { Id = "lucky", Name = "Везунчик", Faction = "player", Stats = { ["luck"] = 7 } });
        var (use, text) = StatUsage.Of(db, "luck");
        Assert.Equal(StatUse.None, use);
        Assert.Contains("без поведения", text);
        // Content that reads it makes it «используется контентом».
        db.Add(new PassiveDef { Id = "lucky_strike", Name = "Удачный удар", StatMods = { new StatModDef { Stat = "luck", Add = 1 } } });
        (use, text) = StatUsage.Of(db, "luck");
        Assert.Equal(StatUse.Content, use);
        Assert.Contains("«Удачный удар»", text);
        // Every built-in characteristic of the core is in the list.
        foreach (var id in Stats.All) Assert.Equal(StatUse.Core, StatUsage.Of(db, id).Use);
    }
}
