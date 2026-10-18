using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>TZ part 2 §16 test 7: several creatures on one hex.</summary>
public class StackingTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new UnitDef
        {
            Id = "rat", Name = "Крыса", Faction = "enemy", BasicAttack = "hit",
            Stacking = new StackingDef { CanShare = true, MaxPerHex = 2, CrowdAccuracyPenalty = 0.1 },
            Stats = { [Stats.HpMax] = 30, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 3, [Stats.DetectionRange] = 8 },
        });
        db.Add(new UnitDef
        {
            Id = "loner", Name = "Одиночка", Faction = "enemy", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 30, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 3, [Stats.DetectionRange] = 8 },
        });
        return db;
    }

    [Fact]
    public void SharingEnemies_StandOnOneHex_AsSeparateUnits_UpToTheLimit()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(8, 3));
        var a = sim.Spawn(db.Unit("rat")!, null, new HexCoord(4, 1), HexDirection.W);
        var b = sim.Spawn(db.Unit("rat")!, null, new HexCoord(2, 1), HexDirection.E);
        var c = sim.Spawn(db.Unit("rat")!, null, new HexCoord(2, 0), HexDirection.E);
        Assert.Null(sim.Enqueue(b, Command.Move(a.Pos)));
        TestUtil.RunUntil(sim, () => b.Pos == a.Pos && b.StepTo == null, 5);
        Assert.Equal(a.Pos, b.Pos);
        var cell = sim.Map.Get(a.Pos)!;
        Assert.Equal(2, cell.Occupants.Count);

        // Limit 2: the third one cannot enter.
        var mc = MoveContext.For(sim, c);
        Assert.False(Occupancy.CanEnter(mc, cell));

        // Separate entities: damage to one does not touch the other.
        sim.Combat.DealDamage(null, a, 10, "physical", null, "тест");
        Assert.True(a.Hp < a.HpMax);
        Assert.Equal(b.HpMax, b.Hp);

        // Crowd penalty: accuracy drops for every cell mate.
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(5, 1), HexDirection.W);
        double crowded = sim.Combat.HitChance(a, hero, db.Skill("hit"), a.Pos, true, false);
        sim.Combat.Kill(b, null, "тест");
        double alone = sim.Combat.HitChance(a, hero, db.Skill("hit"), a.Pos, true, false);
        Assert.True(alone > crowded, $"толпа должна снижать точность ({alone} > {crowded})");
    }

    [Fact]
    public void Hostiles_NeverShare_AndNonSharingTypes_DoNot()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(8, 3));
        var rat = sim.Spawn(db.Unit("rat")!, null, new HexCoord(4, 1), HexDirection.W);
        var loner = sim.Spawn(db.Unit("loner")!, null, new HexCoord(3, 1), HexDirection.E);
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 0), HexDirection.E);
        var cell = sim.Map.Get(rat.Pos)!;
        Assert.False(Occupancy.CanEnter(MoveContext.For(sim, hero), cell), "враждебные не делят клетку");
        Assert.False(Occupancy.CanEnter(MoveContext.For(sim, loner), cell), "тип без совместного размещения");
        // A sharing rat cannot enter the loner's cell: every creature in the cell must allow sharing.
        var rat2 = sim.Spawn(db.Unit("rat")!, null, new HexCoord(3, 0), HexDirection.E);
        Assert.False(Occupancy.CanEnter(MoveContext.For(sim, rat2), sim.Map.Get(loner.Pos)!));
    }

    [Fact]
    public void ScenarioStackingMode_OverridesUnitTypes()
    {
        var db = Db();
        var forbid = new Simulation(db, TestUtil.Field(8, 3), new SimOptions { PlayerFactions = { "player" }, AiFactions = new(), Stacking = StackingMode.ForbidAll });
        var a = forbid.Spawn(db.Unit("rat")!, null, new HexCoord(4, 1), HexDirection.W);
        var b = forbid.Spawn(db.Unit("rat")!, null, new HexCoord(3, 1), HexDirection.E);
        Assert.False(Occupancy.CanEnter(MoveContext.For(forbid, b), forbid.Map.Get(a.Pos)!));

        var allow = new Simulation(db, TestUtil.Field(8, 3), new SimOptions { PlayerFactions = { "player" }, AiFactions = new(), Stacking = StackingMode.AllowAll });
        var x = allow.Spawn(db.Unit("loner")!, null, new HexCoord(4, 1), HexDirection.W);
        var y = allow.Spawn(db.Unit("loner")!, null, new HexCoord(3, 1), HexDirection.E);
        Assert.True(Occupancy.CanEnter(MoveContext.For(allow, y), allow.Map.Get(x.Pos)!));
    }

    [Fact]
    public void LegacyAllowStacking_MigratesToStackingMode()
    {
        var json = """{ "formatVersion": 2, "kind": "scenario", "scenario": { "id": "s", "map": "m", "limits": { "allowStacking": true } } }""";
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var root = ContentMigrator.Migrate(node, "scenario", out _, out var error);
        Assert.Null(error);
        var limits = root!["scenario"]!["limits"]!.AsObject();
        Assert.False(limits.ContainsKey("allowStacking"));
        Assert.Equal("AllowAll", (string?)limits["stacking"]);
    }
}

/// <summary>TZ part 2 §16 test 8: blocking with limits and per-enemy properties.</summary>
public class BlockingTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new UnitDef
        {
            Id = "guard", Name = "Страж", Faction = "player", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 200, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8, [Stats.BlockLimit] = 2 },
        });
        db.Add(new UnitDef
        {
            Id = "grunt", Name = "Рядовой", Faction = "enemy", BasicAttack = "hit",
            Stacking = new StackingDef { CanShare = true, MaxPerHex = 3 },
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        db.Add(new UnitDef
        {
            Id = "boss", Name = "Босс", Faction = "enemy", BasicAttack = "hit", Blockable = new BlockableDef { SlotCost = 3 },
            Stats = { [Stats.HpMax] = 500, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        db.Add(new UnitDef
        {
            Id = "ghost", Name = "Призрак", Faction = "enemy", BasicAttack = "hit", Blockable = new BlockableDef { CanBypass = true },
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        db.Add(new UnitDef
        {
            Id = "giant", Name = "Гигант", Faction = "enemy", BasicAttack = "hit", Blockable = new BlockableDef { RequiredAbility = "heavy_guard" },
            Stats = { [Stats.HpMax] = 300, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        db.Add(new StatusDef { Id = "stun", Name = "Оглушение", Duration = 3, Levels = new() { new StatusLevelDef { Threshold = 1, Flags = { BlocksActions = true, BlocksMovement = true } } } });
        db.Add(new StatusDef { Id = "root", Name = "Корни", Duration = 3, Levels = new() { new StatusLevelDef { Threshold = 1, Flags = { BlocksMovement = true } } } });
        return db;
    }

    /// <summary>A corridor along row 1 so that the enemies must pass the guard.</summary>
    private static MapDef Corridor()
    {
        var m = TestUtil.Field(10, 3);
        foreach (var c in m.Cells.Where(c => c.C.R != 1)) c.T = "wall";
        return m;
    }

    private static HexCoord Row(int q) => new(q, 1);

    [Fact]
    public void Guard_BlocksAdvancingEnemies_UpToItsLimit_TheRestPassOrWait()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 5));
        var guard = sim.Spawn(db.Unit("guard")!, null, new HexCoord(4, 2), HexDirection.E);
        Assert.Equal(2, sim.Blocking.Limit(guard));
        var e1 = sim.Spawn(db.Unit("grunt")!, null, new HexCoord(5, 2), HexDirection.W);
        var e2 = sim.Spawn(db.Unit("grunt")!, null, new HexCoord(5, 1), HexDirection.W);
        var e3 = sim.Spawn(db.Unit("grunt")!, null, new HexCoord(5, 3), HexDirection.W);
        foreach (var e in new[] { e1, e2, e3 }) Assert.Null(sim.Enqueue(e, Command.Move(new HexCoord(0, e.Pos.R))));
        sim.Step();
        Assert.Equal(2, sim.Blocking.Load(guard));
        var blocked = new[] { e1, e2, e3 }.Where(e => sim.Blocking.BlockerOf(e) == guard).ToList();
        Assert.Equal(2, blocked.Count);
        var free = new[] { e1, e2, e3 }.Single(e => sim.Blocking.BlockerOf(e) == null);
        Assert.Equal(MovementHold.Blocked, sim.Blocking.HoldOf(blocked[0]));

        var start = blocked.Select(e => e.Pos).ToList();
        TestUtil.RunUntil(sim, () => free.Pos.Q <= 2, 6);
        Assert.True(free.Pos.Q <= 2, "третий враг должен пройти: у стража нет слота");
        Assert.Equal(start, blocked.Select(e => e.Pos).ToList());
        Assert.All(blocked, e => Assert.StartsWith("заблокирован", e.Queue.Current!.StatusText));
    }

    [Fact]
    public void SlotCost_Bypass_AndRequiredAbility_AreRespected()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 5));
        var guard = sim.Spawn(db.Unit("guard")!, null, new HexCoord(4, 2), HexDirection.E);
        var boss = sim.Spawn(db.Unit("boss")!, null, new HexCoord(5, 2), HexDirection.W);
        var ghost = sim.Spawn(db.Unit("ghost")!, null, new HexCoord(5, 1), HexDirection.W);
        var giant = sim.Spawn(db.Unit("giant")!, null, new HexCoord(4, 3), HexDirection.W);
        foreach (var e in new[] { boss, ghost, giant }) Assert.Null(sim.Enqueue(e, Command.Move(new HexCoord(0, e.Pos.R))));
        sim.Step();
        Assert.Null(sim.Blocking.BlockerOf(boss)); // needs 3 slots, the guard has 2
        Assert.Null(sim.Blocking.BlockerOf(ghost)); // bypasses
        Assert.Null(sim.Blocking.BlockerOf(giant)); // needs heavy_guard

        // Raising the limit (a modifier) lets the guard hold the boss; the ability lets it hold the giant.
        guard.Stats.SetBase(Stats.BlockLimit, 3);
        db.Unit("guard")!.Blocking.Abilities.Add("heavy_guard");
        var sim2 = TestUtil.Sim(db, TestUtil.Field(10, 5));
        var g2 = sim2.Spawn(db.Unit("guard")!, null, new HexCoord(4, 2), HexDirection.E);
        g2.Stats.SetBase(Stats.BlockLimit, 4);
        var b2 = sim2.Spawn(db.Unit("boss")!, null, new HexCoord(5, 2), HexDirection.W);
        var gi2 = sim2.Spawn(db.Unit("giant")!, null, new HexCoord(4, 3), HexDirection.W);
        foreach (var e in new[] { b2, gi2 }) Assert.Null(sim2.Enqueue(e, Command.Move(new HexCoord(0, e.Pos.R))));
        sim2.Step();
        Assert.Equal(g2, sim2.Blocking.BlockerOf(b2));
        Assert.Equal(g2, sim2.Blocking.BlockerOf(gi2));
        Assert.Equal(4, sim2.Blocking.Load(g2));
    }

    [Fact]
    public void Block_IsReleased_WhenBlockerDies_IsStunned_OrLosesLimit()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, Corridor());
        var guard = sim.Spawn(db.Unit("guard")!, null, Row(4), HexDirection.E);
        var e = sim.Spawn(db.Unit("grunt")!, null, Row(5), HexDirection.W);
        Assert.Null(sim.Enqueue(e, Command.Move(Row(0))));
        sim.Step();
        Assert.Equal(guard, sim.Blocking.BlockerOf(e));

        // Stun: the guard cannot hold anyone.
        Assert.True(sim.Statuses.Apply(guard, db.Status("stun")!, 1, 3, -1));
        sim.Step();
        Assert.Null(sim.Blocking.BlockerOf(e));
        sim.Statuses.Remove(guard, guard.Statuses.First(x => x.Def.Id == "stun"), "тест");
        sim.Step();
        Assert.Equal(guard, sim.Blocking.BlockerOf(e));

        // Limit lowered to 0 by a modifier.
        guard.Stats.SetBase(Stats.BlockLimit, 0);
        sim.Step();
        Assert.Null(sim.Blocking.BlockerOf(e));
        guard.Stats.SetBase(Stats.BlockLimit, 2);
        sim.Step();
        Assert.Equal(guard, sim.Blocking.BlockerOf(e));

        // Death: never a stale "eternal" block.
        sim.Combat.Kill(guard, null, "тест");
        Assert.Null(sim.Blocking.BlockerOf(e));
        Assert.Equal(0, sim.Blocking.Links);
        TestUtil.RunUntil(sim, () => e.Pos.Q <= 1, 6);
        Assert.True(e.Pos.Q <= 1, "после гибели стража враг продолжает движение");
    }

    [Fact]
    public void Blocked_Held_AndFree_AreDistinct()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 5));
        var guard = sim.Spawn(db.Unit("guard")!, null, new HexCoord(4, 2), HexDirection.E);
        var blocked = sim.Spawn(db.Unit("grunt")!, null, new HexCoord(5, 2), HexDirection.W);
        var held = sim.Spawn(db.Unit("grunt")!, null, new HexCoord(8, 4), HexDirection.W);
        var free = sim.Spawn(db.Unit("grunt")!, null, new HexCoord(8, 0), HexDirection.W);
        Assert.Null(sim.Enqueue(blocked, Command.Move(new HexCoord(0, 2))));
        Assert.True(sim.Statuses.Apply(held, db.Status("root")!, 1, 3, -1));
        sim.Step();
        Assert.Equal(MovementHold.Blocked, sim.Blocking.HoldOf(blocked));
        Assert.Equal(MovementHold.Held, sim.Blocking.HoldOf(held));
        Assert.Equal(MovementHold.None, sim.Blocking.HoldOf(free));
        Assert.Contains("заблокирован", sim.Knowledge.IntentOf(blocked));
    }

    [Fact]
    public void Ai_AttacksItsBlocker_ByDefault_AndDetourEnemies_GoAround()
    {
        var db = Db();
        db.Unit("grunt")!.AiProfile = "aggressive";
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 5), ai: true);
        var guard = sim.Spawn(db.Unit("guard")!, null, new HexCoord(4, 2), HexDirection.E);
        var e = sim.Spawn(db.Unit("grunt")!, null, new HexCoord(5, 2), HexDirection.W);
        Assert.Null(sim.Enqueue(e, Command.Move(new HexCoord(0, 2))));
        sim.Step();
        Assert.Equal(guard, sim.Blocking.BlockerOf(e));
        TestUtil.RunUntil(sim, () => guard.Hp < guard.HpMax, 6);
        Assert.True(guard.Hp < guard.HpMax, "заблокированный враг атакует блокирующего");

        // Detour: an enemy with "find a detour" walks around a blocker on an open field.
        var db2 = Db();
        db2.Unit("grunt")!.Blockable = new BlockableDef { WhenBlocked = BlockedBehavior.FindDetour };
        var sim2 = TestUtil.Sim(db2, TestUtil.Field(10, 5));
        sim2.Spawn(db2.Unit("guard")!, null, new HexCoord(4, 2), HexDirection.E);
        var d = sim2.Spawn(db2.Unit("grunt")!, null, new HexCoord(5, 2), HexDirection.W);
        Assert.Null(sim2.Enqueue(d, Command.Move(new HexCoord(0, 2))));
        TestUtil.RunUntil(sim2, () => d.Pos.Q <= 1, 10);
        Assert.True(d.Pos.Q <= 1, $"враг с поведением «искать обход» должен обойти стража (стоит на {d.Pos})");
    }

    [Fact]
    public void FlyersAndUndergroundAreNotBlocked_ByDefault()
    {
        var db = Db();
        db.Add(new UnitDef
        {
            Id = "bat", Name = "Летучая", Faction = "enemy", MoveCategory = "fly", Layer = Layers.Air, BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 50, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 3, [Stats.DetectionRange] = 8 },
        });
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 5));
        var guard = sim.Spawn(db.Unit("guard")!, null, new HexCoord(4, 2), HexDirection.E);
        var bat = sim.Spawn(db.Unit("bat")!, null, new HexCoord(5, 2), HexDirection.W);
        Assert.Null(sim.Enqueue(bat, Command.Move(new HexCoord(0, 2))));
        sim.Step();
        Assert.Null(sim.Blocking.BlockerOf(bat));
        Assert.False(sim.Blocking.Compatible(guard, bat));
        // An explicit air layer in the blocker lets it hold flyers.
        db.Unit("guard")!.Blocking.Layers.Add(Layers.Air);
        Assert.True(sim.Blocking.Compatible(guard, bat));
    }
}

/// <summary>TZ part 2 §16 test 13: facing models.</summary>
public class FacingModelTests
{
    private static ContentDatabase Db(PerceptionDef p)
    {
        var db = TestUtil.Minimal();
        db.Unit("dummy")!.Perception = p;
        return db;
    }

    private static (Simulation sim, Unit hero, Unit target) Setup(PerceptionDef p, HexCoord heroPos)
    {
        var db = Db(p);
        var sim = TestUtil.Sim(db, TestUtil.Field(9, 7));
        var target = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 3), HexDirection.E);
        var hero = sim.Spawn(db.Unit("soldier")!, null, heroPos, HexDirection.E);
        return (sim, hero, target);
    }

    [Fact]
    public void Single_RearIsRear_Multiple_UsesTheMostFrontalFacing()
    {
        var behind = new HexCoord(3, 3).Neighbor(HexDirection.W);
        var (_, _, single) = Setup(new PerceptionDef { Model = FacingModel.Single }, behind);
        Assert.Equal(AttackSector.Rear, CombatSystem.SectorFor(single, behind));

        var (_, _, two) = Setup(new PerceptionDef { Model = FacingModel.Multiple, ExtraFacings = { 3 } }, behind);
        Assert.Equal(AttackSector.Front, CombatSystem.SectorFor(two, behind));
        var side = new HexCoord(3, 3).Neighbor(HexDirection.NE);
        Assert.Equal(AttackSector.NearFlank, CombatSystem.SectorFor(two, side));
    }

    [Fact]
    public void Omni_AndNone_HaveNoSectors_AndUseTheNoSectorRule()
    {
        var behind = new HexCoord(3, 3).Neighbor(HexDirection.W);
        foreach (var model in new[] { FacingModel.Omni, FacingModel.None })
        {
            var (sim, hero, target) = Setup(new PerceptionDef { Model = model }, behind);
            Assert.Equal(AttackSector.None, CombatSystem.SectorFor(target, hero.Pos));
            double notice = sim.Combat.NoticeChance(hero, target, hero.Pos, out var sector, out _);
            Assert.Equal(AttackSector.None, sector);
            Assert.Equal(sim.Rules.Detection.NoSectorNotice, notice, 3);
        }
        // Own rule on the unit replaces the battle rule.
        var (sim2, hero2, t2) = Setup(new PerceptionDef { Model = FacingModel.Omni, OwnNoSectorRule = true, NoticeChance = 0.25, SurpriseChance = 0.4 }, behind);
        Assert.Equal(0.25, sim2.Combat.NoticeChance(hero2, t2, hero2.Pos, out _, out _), 3);
    }

    [Fact]
    public void NoFacing_TurnsInstantly_WithoutApCost()
    {
        var (sim, _, target) = Setup(new PerceptionDef { Model = FacingModel.None }, new HexCoord(0, 0));
        double ap = target.Ap;
        Assert.Null(sim.Enqueue(target, Command.Turn(HexDirection.W)));
        sim.Step();
        Assert.Equal(HexDirection.W, target.Facing);
        Assert.True(target.Ap >= ap - 1e-9, "без направления поворот не стоит ОД");
    }

    [Fact]
    public void DetectionZone_LimitsSight_ToTheConeAndAdjacent()
    {
        var db = Db(new PerceptionDef { Model = FacingModel.Single, Zone = DetectionZoneShape.Cone, ZoneSize = 4 });
        var sim = TestUtil.Sim(db, TestUtil.Field(9, 7));
        var watcher = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 3), HexDirection.E);
        var ahead = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(5, 3), HexDirection.W);
        var behind = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 3), HexDirection.E);
        var close = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 3), HexDirection.E);
        Assert.True(sim.Vision.CanSee(watcher, ahead));
        Assert.False(sim.Vision.CanSee(watcher, behind));
        Assert.True(sim.Vision.CanSee(watcher, close), "вплотную видно всегда");

        // Two facings (front and back): the zone covers both directions.
        db.Unit("dummy")!.Perception = new PerceptionDef { Model = FacingModel.Multiple, ExtraFacings = { 3 }, Zone = DetectionZoneShape.Cone, ZoneSize = 4 };
        var sim2 = TestUtil.Sim(db, TestUtil.Field(9, 7));
        var w2 = sim2.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 3), HexDirection.E);
        var b2 = sim2.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 3), HexDirection.E);
        Assert.True(sim2.Vision.CanSee(w2, b2));
    }
}

/// <summary>TZ part 2 §16 test 14: disclosure of enemy information.</summary>
public class InfoDisclosureTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new SkillDef
        {
            Id = "spark", Name = "Искра", Kind = SkillKind.Spell, Targeting = TargetingMode.Unit, RangeMax = 6, ApCost = 1, PrepTime = 0, HitMode = HitMode.AlwaysHit,
            Stages = new() { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 5 } } } },
        });
        db.Unit("soldier")!.Skills.Add("spark");
        var dummy = db.Unit("dummy")!;
        dummy.Stats["resist_fire"] = 0.5;
        dummy.Skills.Add("spark");
        return db;
    }

    private static MapDef WalledField()
    {
        var m = TestUtil.Field(12, 5);
        // A wall column between q=5 and q=7 that blocks line of sight in row 2.
        foreach (var c in m.Cells.Where(c => c.C.Q == 6)) c.T = "wall";
        return m;
    }

    private static Simulation FogSim(ContentDatabase db) =>
        new(db, WalledField(), new SimOptions { FogOfWar = true, PlayerFactions = { "player" }, AiFactions = new() });

    [Fact]
    public void Hidden_Unknown_Seen_Stale_AndAdminSeesAll()
    {
        var db = Db();
        var sim = FogSim(db);
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(9, 2), HexDirection.W);
        sim.Step();
        var info = sim.Knowledge.GetInfo(enemy);
        Assert.False(info.VisibleNow);
        Assert.False(info.NameField.Known);
        Assert.Equal("Неизвестный противник", info.Name);
        Assert.False(info.HealthField.Known);

        // Seen: name, health, facing, intent (while visible).
        sim.Relocate(enemy, new HexCoord(4, 2), null, "тест");
        TestUtil.RunUntil(sim, () => sim.Vision.IsVisibleToPlayer(enemy), 1);
        info = sim.Knowledge.GetInfo(enemy);
        Assert.True(info.VisibleNow);
        Assert.True(info.NameField.Known);
        Assert.True(info.HealthField.Known);
        Assert.True(info.FacingField.Known);
        Assert.True(info.IntentField.Known);
        Assert.False(info.ResistancesField.Known, "сопротивления — только после наблюдения");
        Assert.False(info.SkillsField.Known);

        // Gone behind the wall: the name stays (stale), live health is hidden.
        double hp = enemy.Hp;
        sim.Relocate(enemy, new HexCoord(9, 2), null, "тест");
        TestUtil.RunUntil(sim, () => !sim.Vision.IsVisibleToPlayer(enemy), 1);
        sim.Combat.DealDamage(null, enemy, 10, "physical", null, "тест");
        sim.Step();
        info = sim.Knowledge.GetInfo(enemy);
        Assert.False(info.VisibleNow);
        Assert.True(info.NameField.Known && info.NameField.Stale);
        Assert.False(info.HealthField.Known);
        Assert.Equal(hp, info.Hp);
        Assert.False(info.IntentField.Known);

        // Admin mode sees everything.
        var admin = new Simulation(db, WalledField(), new SimOptions { FogOfWar = true, AdminMode = true, PlayerFactions = { "player" }, AiFactions = new() });
        admin.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var ae = admin.Spawn(db.Unit("dummy")!, null, new HexCoord(9, 2), HexDirection.W);
        admin.Step();
        var ai = admin.Knowledge.GetInfo(ae);
        Assert.True(ai.NameField.Known && ai.ResistancesField.Known && ai.SkillsField.Known);
        Assert.Contains(ai.Resistances, r => r.Name == "fire");
    }

    [Fact]
    public void Resistances_AfterHit_Skills_AfterUse()
    {
        var db = Db();
        var sim = FogSim(db);
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(4, 2), HexDirection.W);
        sim.Step();
        Assert.Null(sim.Enqueue(hero, Command.Skill("spark", null, enemy.Id)));
        TestUtil.RunUntil(sim, () => enemy.Hp < enemy.HpMax, 3);
        var info = sim.Knowledge.GetInfo(enemy);
        Assert.True(info.ResistancesField.Known);
        Assert.Contains(info.Resistances, r => r.Name == "fire" && r.Value.Contains("50"));
        Assert.DoesNotContain(info.Resistances, r => r.Name == "poison");
        Assert.False(info.SkillsField.Known);

        Assert.Null(sim.Enqueue(enemy, Command.Skill("spark", null, hero.Id)));
        TestUtil.RunUntil(sim, () => hero.Hp < hero.HpMax, 3);
        info = sim.Knowledge.GetInfo(enemy);
        Assert.True(info.SkillsField.Known);
        Assert.Equal(new[] { "Искра" }, info.Skills);
        Assert.Equal(2, info.TotalSkills);
    }

    [Fact]
    public void PerUnitOverride_AndDisabledPanel()
    {
        var db = Db();
        db.Unit("dummy")!.Info = new InfoOverrideDef
        {
            Override = true,
            Values = new InfoDisclosureDef { Health = Disclosure.Never, Resistances = Disclosure.Always, Intent = Disclosure.Never },
        };
        var sim = FogSim(db);
        sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(4, 2), HexDirection.W);
        sim.Step();
        var info = sim.Knowledge.GetInfo(enemy);
        Assert.False(info.HealthField.Known);
        Assert.True(info.ResistancesField.Known);
        Assert.Contains(info.Resistances, r => r.Name == "fire");
        Assert.False(info.IntentField.Known);

        sim.Rules.Info.PanelEnabled = false;
        db.Unit("dummy")!.Info.Override = false;
        Assert.False(sim.Knowledge.GetInfo(enemy).PanelEnabled);
    }
}

public class BlockingWalkTests
{
    [Fact]
    public void WalkingEnemy_IsStoppedNextToTheBlocker()
    {
        var db = TestUtil.Minimal();
        db.Add(new UnitDef
        {
            Id = "guard", Name = "Страж", Faction = "player", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 200, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8, [Stats.BlockLimit] = 1 },
        });
        var sim = TestUtil.Sim(db, TestUtil.Field(12, 5));
        var guard = sim.Spawn(db.Unit("guard")!, null, new HexCoord(4, 2), HexDirection.E);
        var e = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(10, 2), HexDirection.W);
        // The target lies beyond the guard: the walker passes through its zone and must be held there.
        Assert.Null(sim.Enqueue(e, Command.Move(new HexCoord(0, 2))));
        TestUtil.RunUntil(sim, () => sim.Blocking.BlockerOf(e) != null, 8);
        Assert.Equal(guard, sim.Blocking.BlockerOf(e));
        TestUtil.RunUntil(sim, () => false, 3);
        Assert.True(HexCoord.Distance(e.Pos, guard.Pos) <= 1, $"враг прошёл мимо стража: {e.Pos}");
        Assert.Null(e.StepTo);
        Assert.True(e.Pos.Q > 2, "враг не должен оказаться за спиной стража");
    }
}
