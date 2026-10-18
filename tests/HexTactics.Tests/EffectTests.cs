using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

public class StatusTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new StatusDef
        {
            Id = "poison", Name = "Яд", Kind = StatusKind.Accumulating, MaxPoints = 100, DecayPerSecond = 10,
            Levels = new()
            {
                new StatusLevelDef { Name = "лёгкое", Threshold = 20 },
                new StatusLevelDef { Name = "сильное", Threshold = 50, StatMods = { new StatModDef { Stat = Stats.MoveSpeed, Mul = -0.5 } } },
                new StatusLevelDef { Name = "критическое", Threshold = 90, Flags = { BlocksMovement = true } },
            },
        });
        db.Add(new StatusDef { Id = "burn", Name = "Горение", Duration = 2, Reapply = ReapplyPolicy.Intensify, MaxStacks = 3, Levels = new() { new StatusLevelDef { Threshold = 1 }, new StatusLevelDef { Threshold = 2 }, new StatusLevelDef { Threshold = 3 } }, Cancels = { "freeze" } });
        db.Add(new StatusDef { Id = "bleed", Name = "Кровь", Duration = 3, Reapply = ReapplyPolicy.Stack, MaxStacks = 2, Levels = new() { new StatusLevelDef { Threshold = 1, TickInterval = 1, OnTick = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 2, Filter = TargetFilterDef.Everyone() } } } } });
        db.Add(new StatusDef { Id = "slow", Name = "Замедление", Duration = 2, Reapply = ReapplyPolicy.Extend, MaxDuration = 5, Levels = new() { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new StatusDef { Id = "freeze", Name = "Мороз", Duration = 5, Reapply = ReapplyPolicy.Ignore, Levels = new() { new StatusLevelDef { Threshold = 1, Flags = { BlocksActions = true } } }, ImmuneCreatureCategories = { "construct" } });
        return db;
    }

    [Fact]
    public void Accumulation_Thresholds_Decay_AndCap()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        var poison = db.Status("poison")!;
        sim.Statuses.Apply(u, poison, 30, 0, -1);
        var inst = u.FindStatus("poison")!;
        Assert.Equal(0, inst.LevelIndex);
        sim.Statuses.Apply(u, poison, 30, 0, -1);
        Assert.Equal(1, inst.LevelIndex);
        Assert.Equal(1.0, u.Stat(Stats.MoveSpeed), 6); // 2 × (1 − 0.5)
        sim.Statuses.Apply(u, poison, 500, 0, -1);
        Assert.Equal(100, inst.Points, 6);
        Assert.True(u.Flags.BlocksMovement);
        sim.RunSeconds(2); // −20 points
        Assert.Equal(1, inst.LevelIndex);
        Assert.False(u.Flags.BlocksMovement);
        sim.RunSeconds(10);
        Assert.Null(u.FindStatus("poison"));
        Assert.Equal(2.0, u.Stat(Stats.MoveSpeed), 6);
    }

    [Fact]
    public void Resistance_ReducesAccumulation()
    {
        var db = Db();
        db.Unit("soldier")!.StatusResistances["poison"] = 0.5;
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        sim.Statuses.Apply(u, db.Status("poison")!, 40, 0, -1);
        Assert.Equal(20, u.FindStatus("poison")!.Points, 6);
    }

    [Fact]
    public void ReapplyPolicies()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        // Intensify up to max stacks.
        for (int i = 0; i < 5; i++) sim.Statuses.Apply(u, db.Status("burn")!, 1, 0, -1);
        Assert.Equal(3, u.FindStatus("burn")!.Stacks);
        Assert.Equal(2, u.FindStatus("burn")!.LevelIndex);
        // Stack: separate instances, bounded.
        for (int i = 0; i < 4; i++) sim.Statuses.Apply(u, db.Status("bleed")!, 1, 0, -1);
        Assert.Equal(2, u.Statuses.Count(s => s.Def.Id == "bleed"));
        // Extend: capped by MaxDuration.
        for (int i = 0; i < 5; i++) sim.Statuses.Apply(u, db.Status("slow")!, 1, 0, -1);
        Assert.Equal(sim.TicksFor(5), u.FindStatus("slow")!.RemainingTicks);
        // Ignore.
        sim.Statuses.Apply(u, db.Status("freeze")!, 1, 0, -1);
        sim.Run(10);
        long rem = u.FindStatus("freeze")!.RemainingTicks;
        sim.Statuses.Apply(u, db.Status("freeze")!, 1, 0, -1);
        Assert.Equal(rem, u.FindStatus("freeze")!.RemainingTicks);
        // Cancels: burning removes freeze.
        sim.Statuses.Apply(u, db.Status("burn")!, 1, 0, -1);
        Assert.Null(u.FindStatus("freeze"));
    }

    [Fact]
    public void Immunity_ByCreatureCategory()
    {
        var db = Db();
        db.Unit("dummy")!.CreatureCategory = "construct";
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(1, 1), HexDirection.E);
        Assert.False(sim.Statuses.Apply(u, db.Status("freeze")!, 1, 0, -1));
        Assert.Empty(u.Statuses);
    }

    [Fact]
    public void PeriodicStatusDamage_TicksBySimTime()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        sim.Statuses.Apply(u, db.Status("bleed")!, 1, 0, -1);
        sim.RunSeconds(2.99);
        // Ticks at 1 s and 2 s (duration 3 s, 2 damage each).
        Assert.Equal(96, u.Hp);
    }
}

public class CellEffectTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new CellEffectDef
        {
            Id = "fire", Name = "Огонь", Duration = 5, TickInterval = 1, ForbiddenTerrainTags = { "water" }, Tags = { "fire" },
            OnTick = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 5, Filter = TargetFilterDef.Everyone() } },
        });
        db.Add(new CellEffectDef { Id = "gas", Name = "Газ", Duration = 10, Tags = { "poison" } });
        db.Add(new CellEffectDef { Id = "frost", Name = "Иней", Duration = 10, Tags = { "ice" } });
        db.Add(new CellEffectDef
        {
            Id = "spark", Name = "Ток", Duration = 3,
            Spread = new SpreadDef { Enabled = true, Interval = 0.1, Chance = 1, ToTerrainTags = { "water" }, MaxGenerations = 3 },
        });
        db.Add(new SynergyDef
        {
            Id = "boom", Name = "Взрыв", A = new() { Kind = SynergyOperandKind.CellEffect, Value = "fire" }, B = new() { Kind = SynergyOperandKind.CellEffect, Value = "gas" },
            RemoveA = true, RemoveB = true, Radius = 1, UnitEffects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 20, Filter = TargetFilterDef.Everyone() } },
        });
        db.Add(new SynergyDef
        {
            Id = "freeze", Name = "Замерзание", A = new() { Kind = SynergyOperandKind.CellEffect, Value = "frost" }, B = new() { Kind = SynergyOperandKind.TerrainTag, Value = "water" },
            RemoveA = true, ConvertTerrain = "ice_floor",
        });
        return db;
    }

    [Fact]
    public void Effect_DoesNotReplaceTerrain_AndRespectsForbiddenTerrain()
    {
        var db = Db();
        var map = TestUtil.Field();
        TestUtil.SetCell(map, new HexCoord(2, 2), "water");
        var sim = TestUtil.Sim(db, map);
        var grass = sim.Map.Get(new HexCoord(1, 1))!;
        Assert.NotNull(sim.CellEffects.Add(grass, db.CellEffect("fire")!, 1, 0, -1, 0, 0));
        Assert.Equal("grass", grass.Terrain.Id);
        var water = sim.Map.Get(new HexCoord(2, 2))!;
        Assert.Null(sim.CellEffects.Add(water, db.CellEffect("fire")!, 1, 0, -1, 0, 0));
        Assert.Empty(water.Effects);
    }

    [Fact]
    public void Ticks_FollowSimTime_AndExpire()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        var cell = sim.Map.Get(u.Pos)!;
        sim.CellEffects.Add(cell, db.CellEffect("fire")!, 1, 0, -1, 0, 0);
        sim.RunSeconds(3.01);
        Assert.Equal(85, u.Hp); // ticks at 1, 2, 3 s
        sim.RunSeconds(3);
        Assert.Empty(cell.Effects);
        Assert.Equal(80, u.Hp); // tick at 4 s, expired at 5 s
    }

    [Fact]
    public void Synergy_FirePlusGas_Explodes_AndRemovesBoth()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var near = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 1), HexDirection.E);
        var far = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(5, 1), HexDirection.E);
        var cell = sim.Map.Get(new HexCoord(2, 2))!;
        sim.CellEffects.Add(cell, db.CellEffect("gas")!, 1, 0, -1, 0, 0);
        sim.CellEffects.Add(cell, db.CellEffect("fire")!, 1, 0, -1, 0, 0);
        Assert.Empty(cell.Effects);
        Assert.Equal(80, near.Hp);
        Assert.Equal(100, far.Hp);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.Synergy);
    }

    [Fact]
    public void Synergy_EffectPlusTerrain_ConvertsTerrain()
    {
        var db = Db();
        var map = TestUtil.Field();
        TestUtil.SetCell(map, new HexCoord(3, 3), "water");
        var sim = TestUtil.Sim(db, map);
        var cell = sim.Map.Get(new HexCoord(3, 3))!;
        sim.CellEffects.Add(cell, db.CellEffect("frost")!, 1, 0, -1, 0, 0);
        Assert.Equal("ice_floor", cell.Terrain.Id);
        Assert.Empty(cell.Effects);
    }

    [Fact]
    public void Spread_FollowsWaterOnly_AndIsGenerationLimited()
    {
        var db = Db();
        var map = TestUtil.Field(12, 3);
        for (int q = 0; q < 12; q++) TestUtil.SetCell(map, new HexCoord(q, 1), "water");
        var sim = TestUtil.Sim(db, map);
        sim.CellEffects.Add(sim.Map.Get(new HexCoord(0, 1))!, db.CellEffect("spark")!, 1, 0, -1, 0, 0);
        sim.RunSeconds(2);
        var electrified = sim.Map.Cells.Where(c => c.HasEffect("spark")).Select(c => c.Coord).ToList();
        Assert.All(electrified, c => Assert.Equal(1, c.R));
        Assert.True(electrified.Count <= 4, $"{electrified.Count} cells");
        Assert.Contains(new HexCoord(3, 1), electrified);
    }

    [Fact]
    public void SelfFeedingSynergy_IsBounded()
    {
        var db = Db();
        // Pathological synergy: fire + fire tag re-adds fire in radius forever without cooldown protection.
        db.Add(new SynergyDef
        {
            Id = "loop", Name = "Петля", A = new() { Kind = SynergyOperandKind.CellEffectTag, Value = "fire" }, B = new() { Kind = SynergyOperandKind.CellEffect, Value = "gas" },
            RemoveA = false, RemoveB = false, Radius = 2, RadiusCellEffects = { "gas", "fire" }, CooldownPerCell = 0,
        });
        db.Table(DefCategory.Synergy).Remove("boom");
        var sim = TestUtil.Sim(db, TestUtil.Field(20, 20));
        var cell = sim.Map.Get(new HexCoord(10, 10))!;
        sim.CellEffects.Add(cell, db.CellEffect("gas")!, 1, 0, -1, 0, 0);
        sim.CellEffects.Add(cell, db.CellEffect("fire")!, 1, 0, -1, 0, 0);
        Assert.True(sim.CellEffects.SynergiesTriggered < 5000);
        sim.Step();
    }

    [Fact]
    public void DestructibleObstacle_TurnsIntoTerrain()
    {
        var db = Db();
        var map = TestUtil.Field();
        TestUtil.SetCell(map, new HexCoord(3, 3), "crate");
        var sim = TestUtil.Sim(db, map);
        var cell = sim.Map.Get(new HexCoord(3, 3))!;
        int v = sim.Map.Version;
        sim.CellEffects.DamageObstacle(cell, 5, null, "тест");
        Assert.Equal("crate", cell.Terrain.Id);
        sim.CellEffects.DamageObstacle(cell, 5, null, "тест");
        Assert.Equal("grass", cell.Terrain.Id);
        Assert.True(sim.Map.Version > v);
    }
}
