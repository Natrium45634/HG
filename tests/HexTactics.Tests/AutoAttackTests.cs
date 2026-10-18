using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>TZ part 2 §16 test 9: auto-attacks through the common combat system.</summary>
public class AutoAttackTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new SkillDef
        {
            Id = "bolt", Name = "Выстрел", Kind = SkillKind.Attack, Ranged = true, Targeting = TargetingMode.Unit, RangeMax = 5, ApCost = 2, PrepTime = 0.1,
            RecoveryTime = 0.1, HitMode = HitMode.AlwaysHit, CanCrit = false,
            Stages = new() { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 10 } } } },
        });
        db.Add(new UnitDef
        {
            Id = "archer", Name = "Лучник", Faction = "player", BasicAttack = "hit", Skills = { "bolt" },
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
            AutoAttacks = { new AutoAttackDef { Skill = "bolt", Interval = 1 } },
        });
        db.Add(new UnitDef
        {
            Id = "bat", Name = "Летучая", Faction = "enemy", MoveCategory = "fly", Layer = Layers.Air, BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 50, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 3, [Stats.DetectionRange] = 8 },
        });
        db.Rules.Combat.MinDamageOnHit = 0;
        return db;
    }

    private static Simulation Sim(ContentDatabase db, MapDef? map = null, bool fog = false, ulong seed = 42) =>
        new(db, map ?? TestUtil.Field(12, 5), new SimOptions { Seed = seed, FogOfWar = fog, PlayerFactions = { "player" }, AiFactions = new() });

    private static void Run(Simulation sim, double seconds) => TestUtil.RunUntil(sim, () => false, seconds);

    [Fact]
    public void Archer_AttacksDetectedEnemyInRange_ThroughTheDamagePipeline()
    {
        var db = Db();
        db.Unit("dummy")!.Defense.Modifiers.Add(new DamageModDef { Match = "fire", Percent = -50 });
        var sim = Sim(db);
        var archer = sim.Spawn(db.Unit("archer")!, null, new HexCoord(2, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(5, 2), HexDirection.W);
        Run(sim, 0.5);
        Assert.True(enemy.Hp < enemy.HpMax, "лучник должен атаковать сам");
        var hit = sim.Damage.Recent.First(r => r.Target == enemy.Id).Result;
        Assert.Equal(DamageSource.AutoAttack, hit.Source);
        Assert.Equal(5, hit.HealthLost, 3); // resistance −50 % applied — not bypassed
        Assert.Equal(1, sim.AutoAttacks.StatesOf(archer)[0].Fired);
    }

    [Fact]
    public void OutOfRange_Undetected_OrBehindAWall_IsNotAttacked()
    {
        var db = Db();
        var map = TestUtil.Field(12, 5);
        foreach (var c in map.Cells.Where(c => c.C.Q == 6)) c.T = "wall";
        var sim = Sim(db, map, fog: true);
        var archer = sim.Spawn(db.Unit("archer")!, null, new HexCoord(2, 2), HexDirection.E);
        var far = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(11, 0), HexDirection.W);   // out of range
        var hidden = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(7, 2), HexDirection.W); // behind the wall: no LoS, not seen
        Run(sim, 3);
        Assert.Equal(far.HpMax, far.Hp);
        Assert.Equal(hidden.HpMax, hidden.Hp);
        Assert.Equal(0, sim.AutoAttacks.StatesOf(archer)[0].Fired);
        Assert.Contains("нет целей", sim.AutoAttacks.StatesOf(archer)[0].Status);
    }

    [Fact]
    public void Interval_IsRespected_AndOnlyFreeApIsUsed()
    {
        var db = Db();
        var sim = Sim(db);
        var archer = sim.Spawn(db.Unit("archer")!, null, new HexCoord(2, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(5, 2), HexDirection.W);
        enemy.Stats.SetBase(Stats.HpMax, 1000);
        enemy.Hp = 1000;
        Run(sim, 3.05);
        int fired = sim.AutoAttacks.StatesOf(archer)[0].Fired;
        Assert.InRange(fired, 3, 4); // t = 0, 1, 2, 3 s

        // Reserved AP (a planned skill after a wait) is not spent by auto-attacks with «пропустить».
        var db2 = Db();
        db2.Unit("archer")!.AutoAttacks[0].StopWhen.Clear();
        db2.Unit("archer")!.Stats[Stats.ApRegen] = 0;
        var sim2 = Sim(db2);
        var a2 = sim2.Spawn(db2.Unit("archer")!, null, new HexCoord(2, 2), HexDirection.E);
        var e2 = sim2.Spawn(db2.Unit("dummy")!, null, new HexCoord(5, 2), HexDirection.W);
        a2.Ap = 2;
        Assert.Null(sim2.Enqueue(a2, Command.Wait(5)));
        Assert.Null(sim2.Enqueue(a2, Command.Skill("bolt", null, e2.Id)));
        Assert.True(a2.ReservedAp >= 2 - 1e-9);
        Run(sim2, 1);
        Assert.Equal(e2.HpMax, e2.Hp);
        Assert.Contains("свободных ОД", sim2.AutoAttacks.StatesOf(a2)[0].Status);
        // «Тратить зарезервированные ОД» allows it.
        db2.Unit("archer")!.AutoAttacks[0].OnShortage = ResourceShortage.UseReserved;
        Run(sim2, 1.1);
        Assert.True(e2.Hp < e2.HpMax);
    }

    [Fact]
    public void ManualCommands_TakePriority()
    {
        var db = Db();
        var sim = Sim(db);
        var archer = sim.Spawn(db.Unit("archer")!, null, new HexCoord(2, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(5, 2), HexDirection.W);
        Assert.Null(sim.Enqueue(archer, Command.Move(new HexCoord(0, 2))));
        sim.Step();
        Assert.Equal(enemy.HpMax, enemy.Hp);
        Assert.Contains("движени", sim.AutoAttacks.StatesOf(archer)[0].Status);
        // Turned off for the unit by the player.
        sim.Commands.ClearQueue(archer, "т", CommandStatus.Cancelled);
        sim.AutoAttacks.DisabledUnits.Add(archer.Id);
        Run(sim, 2);
        Assert.Equal(enemy.HpMax, enemy.Hp);
        sim.AutoAttacks.DisabledUnits.Remove(archer.Id);
        Run(sim, 1);
        Assert.True(enemy.Hp < enemy.HpMax);
    }

    [Fact]
    public void TargetRules_Sequential_Weighted_AndReproducibleRandomTies()
    {
        var db = Db();
        db.Unit("archer")!.AutoAttacks[0].Rules.Add(new AutoTargetRuleDef { Rule = AutoTargetRule.LowestHp });
        var sim = Sim(db);
        var archer = sim.Spawn(db.Unit("archer")!, null, new HexCoord(2, 2), HexDirection.E);
        var near = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 1), HexDirection.W);
        var weak = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(6, 2), HexDirection.W);
        weak.Hp = 30;
        Run(sim, 0.3);
        Assert.Equal(weak.Id, sim.AutoAttacks.StatesOf(archer)[0].TargetId);
        Assert.Equal(near.HpMax, near.Hp);

        // Weighted: distance dominates a small HP difference.
        var db2 = Db();
        var aa = db2.Unit("archer")!.AutoAttacks[0];
        aa.Selection = TargetSelectionMode.Weighted;
        aa.Rules.Add(new AutoTargetRuleDef { Rule = AutoTargetRule.Nearest, Weight = 3 });
        aa.Rules.Add(new AutoTargetRuleDef { Rule = AutoTargetRule.LowestHp, Weight = 1 });
        var sim2 = Sim(db2);
        var a2 = sim2.Spawn(db2.Unit("archer")!, null, new HexCoord(2, 2), HexDirection.E);
        var n2 = sim2.Spawn(db2.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        var w2 = sim2.Spawn(db2.Unit("dummy")!, null, new HexCoord(6, 2), HexDirection.W);
        w2.Hp = 80;
        Run(sim2, 0.3);
        Assert.Equal(n2.Id, sim2.AutoAttacks.StatesOf(a2)[0].TargetId);

        // Random among equals — only through the simulation RNG: the same seed gives the same pick.
        int Pick(ulong seed)
        {
            var d = Db();
            d.Unit("archer")!.AutoAttacks[0].RandomTies = true;
            var s = Sim(d, seed: seed);
            var a = s.Spawn(d.Unit("archer")!, null, new HexCoord(2, 2), HexDirection.E);
            s.Spawn(d.Unit("dummy")!, null, new HexCoord(4, 2), HexDirection.W);
            s.Spawn(d.Unit("dummy")!, null, new HexCoord(2, 4), HexDirection.W);
            s.Spawn(d.Unit("dummy")!, null, new HexCoord(4, 0), HexDirection.W);
            Run(s, 0.3);
            return s.AutoAttacks.StatesOf(a)[0].TargetId;
        }
        Assert.Equal(Pick(7), Pick(7));
        Assert.Equal(Pick(99), Pick(99));
    }

    [Fact]
    public void Swordsman_AttacksTheEnemyHeBlocks_AndBlockedEnemy_AttacksItsBlocker()
    {
        var db = Db();
        db.Add(new UnitDef
        {
            Id = "knight", Name = "Мечник", Faction = "player", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 300, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8, [Stats.BlockLimit] = 1 },
            AutoAttacks = { new AutoAttackDef { Interval = 1, Rules = { new AutoTargetRuleDef { Rule = AutoTargetRule.BlockedByMe }, new AutoTargetRuleDef { Rule = AutoTargetRule.Nearest } } } },
        });
        db.Unit("dummy")!.AutoAttacks.Add(new AutoAttackDef { Interval = 1, Rules = { new AutoTargetRuleDef { Rule = AutoTargetRule.BlockingMe } } });
        var sim = Sim(db);
        var knight = sim.Spawn(db.Unit("knight")!, null, new HexCoord(4, 2), HexDirection.E);
        var runner = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(5, 2), HexDirection.W);
        var other = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(5, 1), HexDirection.W);
        Assert.Null(sim.Enqueue(runner, Command.Move(new HexCoord(0, 2))));
        sim.Step();
        Assert.Equal(knight, sim.Blocking.BlockerOf(runner));
        Run(sim, 2.5);
        Assert.Equal(runner.Id, sim.AutoAttacks.StatesOf(knight)[0].TargetId);
        Assert.True(runner.Hp < runner.HpMax, "мечник атакует того, кого блокирует");
        Assert.True(knight.Hp < knight.HpMax, "заблокированный враг атакует блокирующего");
        Assert.True(sim.Damage.Recent.Where(r => r.Attacker == runner.Id).All(r => r.Target == knight.Id));

        // «Атаковать, когда заблокирован» off: the blocked enemy holds fire.
        var db2 = Db();
        db2.Add(db.Unit("knight")!);
        db2.Unit("dummy")!.AutoAttacks.Add(new AutoAttackDef { Interval = 1, WhenBlocked = false });
        var s2 = Sim(db2);
        var k2 = s2.Spawn(db2.Unit("knight")!, null, new HexCoord(4, 2), HexDirection.E);
        k2.Stats.SetBase(Stats.HpMax, 300);
        k2.Hp = 300;
        var r2 = s2.Spawn(db2.Unit("dummy")!, null, new HexCoord(5, 2), HexDirection.W);
        Assert.Null(s2.Enqueue(r2, Command.Move(new HexCoord(0, 2))));
        Run(s2, 2);
        Assert.Equal(k2, s2.Blocking.BlockerOf(r2));
        Assert.DoesNotContain(s2.Damage.Recent, r => r.Attacker == r2.Id);
    }

    [Fact]
    public void Layers_MeleeAutoAttackIgnoresFlyers()
    {
        var db = Db();
        db.Unit("soldier")!.AutoAttacks.Add(new AutoAttackDef { Interval = 1 });
        var sim = Sim(db);
        var s = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var bat = sim.Spawn(db.Unit("bat")!, null, new HexCoord(3, 2), HexDirection.W);
        Run(sim, 2);
        Assert.Equal(bat.HpMax, bat.Hp);
        Assert.Equal(0, sim.AutoAttacks.StatesOf(s)[0].Fired);
    }
}
