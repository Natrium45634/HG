using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage D — «не хочет» ≠ «не может» атаковать (ТЗ 13.8, T15; docs/DECISIONS_5.md §87).</summary>
public class Part5TemperTests
{
    private static (ContentDatabase Db, Simulation Sim, Unit Mimic, Unit Soldier) Scene(bool peaceful, bool forbid)
    {
        var db = TestUtil.Minimal();
        db.Add(new AiProfileDef { Id = "slime_ai", Name = "Слизь", Temperament = peaceful ? AiTemperament.PassiveUntilAttacked : AiTemperament.Aggressive });
        db.Add(new OriginDef
        {
            Id = "shy", Name = "Робкие", Kind = OriginKind.Custom,
            Rules = { new CategoryRuleDef { Id = "no_first", Kind = CategoryRuleKind.Forbid, What = ForbidWhat.InitiateAttack } },
        });
        db.Add(new UnitDef
        {
            Id = "mimic", Name = "Слизь-мимик", Faction = "enemy", BasicAttack = "hit", AiProfile = "slime_ai", MainCategory = forbid ? "shy" : "",
            Stats = { [Stats.HpMax] = 80, [Stats.ApMax] = 10, [Stats.ApRegen] = 3, [Stats.BaseDamage] = 6, [Stats.MoveSpeed] = 2 },
        });
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 6), ai: true);
        var soldier = sim.Spawn(db.Unit("soldier")!, "player", new HexCoord(4, 2), HexDirection.E);
        var mimic = sim.Spawn(db.Unit("mimic")!, "enemy", new HexCoord(5, 2), HexDirection.W);
        return (db, sim, mimic, soldier);
    }

    [Fact]
    public void T15_PeacefulMimic_DoesNotStart_ButAnswers()
    {
        var (_, sim, mimic, soldier) = Scene(peaceful: true, forbid: false);
        sim.RunSeconds(6);
        Assert.Equal(soldier.HpMax, soldier.Hp, 3);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("мирный: не нападает первым"));
        // The capability is there: an order makes it attack.
        sim.Input.Enqueue(mimic, Command.Attack(soldier.Id));
        sim.RunSeconds(3);
        Assert.True(soldier.Hp < soldier.HpMax, "приказ атаковать должен выполниться — способность не запрещена");

        // Attacked — it answers the attacker by its policy.
        var (_, sim2, mimic2, soldier2) = Scene(peaceful: true, forbid: false);
        sim2.RunSeconds(1);
        sim2.Input.Enqueue(soldier2, Command.Attack(mimic2.Id));
        sim2.RunSeconds(6);
        Assert.Contains(soldier2.Id, mimic2.Provokers);
        Assert.True(soldier2.Hp < soldier2.HpMax, "на нападение мирный отвечает напавшему");
    }

    [Fact]
    public void T15_CannotInitiateAttack_NeverStartsUnderAnyAi_EvenByOrder_ButMayAnswer()
    {
        // An aggressive AI, but the category forbids the first attack.
        var (_, sim, mimic, soldier) = Scene(peaceful: false, forbid: true);
        Assert.True(mimic.Forbidden.ContainsKey(nameof(ForbidWhat.InitiateAttack)));
        sim.RunSeconds(6);
        Assert.Equal(soldier.HpMax, soldier.Hp, 3);
        // Even an order does not make it attack first.
        sim.Input.Enqueue(mimic, Command.Attack(soldier.Id));
        sim.RunSeconds(3);
        Assert.Equal(soldier.HpMax, soldier.Hp, 3);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("нападать первым") && e.Text.Contains("не нападал"));
        // Once attacked, it answers the attacker.
        sim.Input.Enqueue(soldier, Command.Attack(mimic.Id));
        sim.RunSeconds(6);
        Assert.True(soldier.Hp < soldier.HpMax, "запрет первой атаки не запрещает ответ");
    }
}
