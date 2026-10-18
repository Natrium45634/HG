using System;
using System.Linq;
using System.Numerics;
using HexTactics.Core.Content;
using HexTactics.Core.Geometry;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

public class GeometryTests
{
    private static void AssertWellFormed(System.Collections.Generic.List<TileTri> tris)
    {
        foreach (var t in tris)
        {
            Assert.InRange(t.Normal.Length(), 0.999f, 1.001f);
            // Counter-clockwise around the normal (the renderer flips for Godot's clockwise front faces).
            var cross = Vector3.Cross(t.B - t.A, t.C - t.A);
            Assert.True(Vector3.Dot(cross, t.Normal) >= -1e-6f, $"треугольник {t.Part} закручен против нормали");
        }
    }

    [Fact]
    public void Floor_HasTop_Bevel_AndSidesDownToBase()
    {
        var shape = new TileShape { Coord = new HexCoord(2, 1), TopY = 0, BaseY = -0.45f, Bevel = 0.07f };
        var tris = HexGeometry.Build(shape);
        AssertWellFormed(tris);
        var top = tris.Where(t => t.Part == TilePart.Top).ToList();
        Assert.Equal(6, top.Count);
        Assert.All(top, t => Assert.True(t.Normal.Y > 0.999f && Math.Abs(t.A.Y) < 1e-5f));
        var bevel = tris.Where(t => t.Part == TilePart.Bevel).ToList();
        Assert.Equal(12, bevel.Count);
        Assert.All(bevel, t => Assert.True(t.Normal.Y > 0.1f && t.Normal.Y < 0.999f, "фаска должна быть наклонной"));
        Assert.Contains(bevel, t => Math.Abs(t.C.Y + 0.07f) < 1e-4f || Math.Abs(t.B.Y + 0.07f) < 1e-4f);
        var sides = tris.Where(t => t.Part == TilePart.Side).ToList();
        Assert.Equal(12, sides.Count);
        Assert.Contains(sides, t => Math.Abs(Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) + 0.45f) < 1e-5f);
        // Side normals point away from the tile centre.
        var ctr = HexGeometry.Center(shape.Coord, 0, 1);
        foreach (var t in sides)
        {
            var mid = (t.A + t.B + t.C) / 3 - ctr;
            Assert.True(Vector3.Dot(new Vector3(mid.X, 0, mid.Z), t.Normal) > 0, "боковая грань смотрит внутрь");
        }
        Assert.DoesNotContain(tris, t => t.Part is TilePart.InnerWall or TilePart.Bottom);
    }

    [Fact]
    public void Elevation_IsTaller_WithLongerSides()
    {
        var floor = HexGeometry.Build(new TileShape { Coord = HexCoord.Zero, TopY = 0 });
        var raised = HexGeometry.Build(new TileShape { Coord = HexCoord.Zero, TopY = 0.35f });
        float FloorTop(System.Collections.Generic.List<TileTri> t) => t.Where(x => x.Part == TilePart.Top).Max(x => x.A.Y);
        Assert.True(FloorTop(raised) > FloorTop(floor) + 0.3f);
        float SideSpan(System.Collections.Generic.List<TileTri> t) => t.Where(x => x.Part == TilePart.Side).Max(x => Math.Max(x.A.Y, Math.Max(x.B.Y, x.C.Y)))
                                                                        - t.Where(x => x.Part == TilePart.Side).Min(x => Math.Min(x.A.Y, Math.Min(x.B.Y, x.C.Y)));
        Assert.True(SideSpan(raised) > SideSpan(floor) + 0.3f);
        // A wall: extra visual height above the walkable level.
        var wall = HexGeometry.Build(new TileShape { Coord = HexCoord.Zero, TopY = 0, ExtraHeight = 1.05f });
        Assert.InRange(FloorTop(wall), 1.04f, 1.06f);
    }

    [Fact]
    public void Pit_IsARecess_WithInnerWallsAndBottom_NotAFlatTile()
    {
        var shape = new TileShape { Coord = new HexCoord(1, 1), TopY = 0, Depth = 0.9f, BaseY = -0.45f };
        var tris = HexGeometry.Build(shape);
        AssertWellFormed(tris);
        Assert.DoesNotContain(tris, t => t.Part == TilePart.Top);
        var rim = tris.Where(t => t.Part == TilePart.Rim).ToList();
        Assert.Equal(12, rim.Count);
        var walls = tris.Where(t => t.Part == TilePart.InnerWall).ToList();
        Assert.Equal(12, walls.Count);
        var bottom = tris.Where(t => t.Part == TilePart.Bottom).ToList();
        Assert.Equal(6, bottom.Count);
        Assert.All(bottom, t => Assert.True(Math.Abs(t.A.Y + 0.9f) < 1e-4f, "дно на глубине"));
        // Inner walls face the hole's axis and span from the rim to the bottom.
        var ctr = HexGeometry.Center(shape.Coord, 0, 1);
        foreach (var t in walls)
        {
            var mid = (t.A + t.B + t.C) / 3 - ctr;
            Assert.True(Vector3.Dot(new Vector3(mid.X, 0, mid.Z), t.Normal) < 0, "внутренняя стенка должна смотреть внутрь ямы");
        }
        float wallMin = walls.Min(t => Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)));
        float wallMax = walls.Max(t => Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
        Assert.True(wallMax > -0.05f && wallMin < -0.85f);
        // The outer sides go below the bottom of the hole (the tile stays a solid frame).
        Assert.True(tris.Where(t => t.Part == TilePart.Side).Min(t => Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y))) < -0.9f);
    }

    [Fact]
    public void Liquid_SurfaceLiesBelowTheRim_AboveTheBasinFloor()
    {
        var tris = HexGeometry.Build(new TileShape { Coord = HexCoord.Zero, TopY = 0, Depth = 0.6f, LiquidDrop = 0.12f });
        var surf = tris.Where(t => t.Part == TilePart.LiquidSurface).ToList();
        Assert.Equal(6, surf.Count);
        Assert.All(surf, t => Assert.InRange(t.A.Y, -0.121f, -0.119f));
        Assert.All(surf, t => Assert.True(t.Normal.Y > 0.999f));
        Assert.True(tris.Where(t => t.Part == TilePart.Bottom).All(t => t.A.Y < -0.5f));
    }
}

public class PitTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new TerrainDef
        {
            Id = "pit", Name = "Яма", Kind = TerrainKind.Pit, PassableFor = new() { "fly" },
            Pit = new PitRulesDef { Consequence = FallConsequence.Death, RescueStatuses = { "levitation" } },
        });
        db.Add(new TerrainDef
        {
            Id = "soft_pit", Name = "Мягкая яма", Kind = TerrainKind.Pit, PassableFor = new() { "fly" },
            Pit = new PitRulesDef { Consequence = FallConsequence.DamageAndReturn, FallDamage = 25, DamageType = "physical" },
        });
        db.Add(new StatusDef { Id = "levitation", Name = "Левитация", Duration = 3, Negative = false, Levels = new() { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new StatusDef { Id = "stun", Name = "Оглушение", Duration = 5, Levels = new() { new StatusLevelDef { Threshold = 1, Flags = { BlocksActions = true } } } });
        db.Add(new SkillDef
        {
            Id = "shove", Name = "Толчок", Targeting = TargetingMode.Unit, RangeMax = 1, ApCost = 1, PrepTime = 0, RecoveryTime = 0, HitMode = HitMode.AlwaysHit,
            Stages = new() { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Push, Distance = 2, RequiresHit = false } } } },
        });
        db.Add(new SkillDef
        {
            Id = "long_cast", Name = "Долгое заклинание", Targeting = TargetingMode.Self, ApCost = 1, PrepTime = 10,
            Stages = new() { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Heal, Amount = 1, Filter = TargetFilterDef.AlliesAndSelf() } } } },
        });
        db.Unit("dummy")!.Skills.Add("long_cast");
        db.Add(new UnitDef
        {
            Id = "flyer", Name = "Летун", Faction = "enemy", MoveCategory = "fly", Layer = Layers.Air, BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 50, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 3, [Stats.DetectionRange] = 8 },
        });
        return db;
    }

    [Fact]
    public void PushIntoPit_Kills_AndClearsQueueCastReservationsAndOccupancy()
    {
        var db = Db();
        var map = TestUtil.Field(8, 3);
        TestUtil.SetCell(map, new HexCoord(4, 1), "pit");
        db.Unit("soldier")!.Skills.Add("shove");
        var sim = TestUtil.Sim(db, map);
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 1), HexDirection.E);
        var foe = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 1), HexDirection.W);
        // The victim is preparing a long skill and has orders queued.
        Assert.Null(sim.Enqueue(foe, Command.Skill("long_cast", null, -1, null)));
        Assert.Null(sim.Enqueue(foe, Command.Move(new HexCoord(5, 0))));
        sim.Step();
        Assert.NotNull(foe.Casting);
        var hero2 = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        Assert.Null(sim.Enqueue(hero, Command.Skill("shove", null, foe.Id, null)));
        TestUtil.RunUntil(sim, () => !foe.Alive, 3);

        Assert.False(foe.Alive);
        Assert.Equal(DeathCause.Fall, foe.DeathCause);
        Assert.Null(foe.Casting);
        Assert.Equal(0, foe.Queue.Count);
        Assert.Null(foe.StepTo);
        Assert.All(sim.Map.Cells, c => Assert.DoesNotContain(foe, c.Occupants));
        Assert.All(sim.Map.Cells, c => Assert.DoesNotContain(foe, c.Reservations));
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.Fall && e.TargetUnit == foe.Id);
        // The battle goes on normally; the dead unit is not a valid target any more.
        sim.RunSeconds(1);
        Assert.False(sim.Skills.Validate(hero, db.Skill("hit")!, null, foe.Id, null, hero.Pos, hero.Facing).Ok);
        Assert.True(hero2.Alive);
    }

    [Fact]
    public void Walking_NeverRoutesThroughPits()
    {
        var db = Db();
        var map = TestUtil.Field(9, 3);
        TestUtil.SetCell(map, new HexCoord(4, 0), "pit");
        TestUtil.SetCell(map, new HexCoord(4, 1), "pit");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        var p = sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(7, 1));
        Assert.True(p.Found);
        Assert.DoesNotContain(p.Path, c => sim.Map.Get(c)!.Terrain.Kind == TerrainKind.Pit);
        TestUtil.SetCell(map, new HexCoord(4, 2), "pit");
        var sim2 = TestUtil.Sim(db, map);
        var u2 = sim2.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
        Assert.False(sim2.Pathfinder.Find(sim2, u2, u2.Pos, new HexCoord(7, 1)).Found);
        Assert.Null(sim2.Enqueue(u2, Command.Move(new HexCoord(3, 1))));
        sim2.RunSeconds(5);
        Assert.True(u2.Alive);
    }

    [Fact]
    public void Levitation_CrossesPits_AndFallsWhenItEndsAbovePit()
    {
        var db = Db();
        var map = TestUtil.Field(9, 1);
        TestUtil.SetCell(map, new HexCoord(3, 0), "pit");
        TestUtil.SetCell(map, new HexCoord(4, 0), "pit");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 0), HexDirection.E);
        sim.Statuses.Apply(u, db.Status("levitation")!, 1, 3, -1);
        Assert.True(sim.Pathfinder.Find(sim, u, u.Pos, new HexCoord(6, 0)).Found);
        Assert.Null(sim.Enqueue(u, Command.Move(new HexCoord(6, 0))));
        TestUtil.RunUntil(sim, () => u.Pos == new HexCoord(6, 0), 6);
        Assert.True(u.Alive);
        Assert.Equal(new HexCoord(6, 0), u.Pos);

        // A second walker whose levitation ends right above the pit falls.
        var v = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 0), HexDirection.E);
        sim.Statuses.Apply(v, db.Status("levitation")!, 1, 0.6, -1);
        Assert.Null(sim.Enqueue(v, Command.Move(new HexCoord(3, 0))));
        TestUtil.RunUntil(sim, () => !v.Alive, 4);
        Assert.False(v.Alive);
        Assert.Equal(DeathCause.Fall, v.DeathCause);
    }

    [Fact]
    public void SoftPit_DamagesAndReturnsToLastSafeCell()
    {
        var db = Db();
        var map = TestUtil.Field(8, 1);
        TestUtil.SetCell(map, new HexCoord(4, 0), "soft_pit");
        db.Unit("soldier")!.Skills.Add("shove");
        var sim = TestUtil.Sim(db, map);
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 0), HexDirection.E);
        var foe = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 0), HexDirection.W);
        Assert.Null(sim.Enqueue(hero, Command.Skill("shove", null, foe.Id, null)));
        TestUtil.RunUntil(sim, () => foe.Hp < foe.HpMax, 3);
        Assert.True(foe.Alive);
        Assert.Equal(new HexCoord(3, 0), foe.Pos);
        Assert.Equal(foe.HpMax - 25, foe.Hp, 3);
    }

    [Fact]
    public void FlyingUnits_IgnorePits_AndFloorCollapseDropsWalkers()
    {
        var db = Db();
        var map = TestUtil.Field(8, 1);
        TestUtil.SetCell(map, new HexCoord(4, 0), "pit");
        var sim = TestUtil.Sim(db, map);
        var fly = sim.Spawn(db.Unit("flyer")!, null, new HexCoord(2, 0), HexDirection.E);
        Assert.Null(sim.Enqueue(fly, Command.Move(new HexCoord(6, 0))));
        TestUtil.RunUntil(sim, () => fly.Pos == new HexCoord(6, 0), 6);
        Assert.True(fly.Alive);

        var walker = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 0), HexDirection.E);
        sim.CellEffects.ChangeTerrain(sim.Map.Get(walker.Pos)!, db.Terrain("pit")!, "обрушение");
        Assert.False(walker.Alive);
        Assert.Equal(DeathCause.Fall, walker.DeathCause);
    }
}

public class LayerTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new SkillDef
        {
            Id = "bolt", Name = "Выстрел", Kind = SkillKind.Attack, Ranged = true, Targeting = TargetingMode.Unit, RangeMax = 5, ApCost = 1, PrepTime = 0, HitMode = HitMode.AlwaysHit,
            Stages = new() { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, Amount = 10 } } } },
        });
        db.Add(new SkillDef
        {
            Id = "tremor", Name = "Толчок земли", Kind = SkillKind.Spell, Targeting = TargetingMode.Self, ApCost = 1, PrepTime = 0, HitMode = HitMode.AlwaysHit,
            TargetLayers = new() { Layers.Ground, Layers.Underground },
            Stages = new() { new SkillStageDef { Area = new AreaDef { Shape = AreaShape.Radius, Size = 2 }, Effects = { new SkillEffectDef { Type = EffectType.Damage, Amount = 10 } } } },
        });
        db.Add(new UnitDef
        {
            Id = "bat", Name = "Летучая", Faction = "enemy", MoveCategory = "fly", Layer = Layers.Air, BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 50, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 3, [Stats.DetectionRange] = 8 },
        });
        db.Add(new UnitDef
        {
            Id = "mole", Name = "Крот", Faction = "enemy", Layer = Layers.Underground, BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 50, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        var s = db.Unit("soldier")!;
        s.Skills.Add("bolt");
        s.Skills.Add("tremor");
        return db;
    }

    [Fact]
    public void Melee_CannotHitAir_RangedCan()
    {
        var db = Db();
        var sim = TestUtil.Sim(db, TestUtil.Field(8, 3));
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 1), HexDirection.E);
        var bat = sim.Spawn(db.Unit("bat")!, null, new HexCoord(3, 1), HexDirection.W);
        var melee = sim.Skills.Validate(hero, db.Skill("hit")!, null, bat.Id, null, hero.Pos, hero.Facing);
        Assert.False(melee.Ok);
        Assert.Contains("слое", melee.Message);
        Assert.True(sim.Skills.Validate(hero, db.Skill("bolt")!, null, bat.Id, null, hero.Pos, hero.Facing).Ok);
        Assert.Null(sim.Enqueue(hero, Command.Skill("bolt", null, bat.Id, null)));
        TestUtil.RunUntil(sim, () => bat.Hp < bat.HpMax, 2);
        Assert.True(bat.Hp < bat.HpMax);
    }

    [Fact]
    public void Underground_IsHit_OnlyBySkillsIncludingIts_Layer_AndHiddenWithoutDetection()
    {
        var db = Db();
        var sim = new Simulation(db, TestUtil.Field(9, 3), new SimOptions { FogOfWar = true, PlayerFactions = { "player" }, AiFactions = new() });
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 1), HexDirection.E);
        var mole = sim.Spawn(db.Unit("mole")!, null, new HexCoord(4, 1), HexDirection.W);
        sim.Vision.Recompute();
        Assert.False(sim.Vision.CanSee(hero, mole), "подземный виден без обнаружения");
        Assert.False(sim.Skills.Validate(hero, db.Skill("bolt")!, null, mole.Id, null, hero.Pos, hero.Facing).Ok);
        // Area: the earth tremor hits the underground layer, a normal burst would not.
        Assert.Null(sim.Enqueue(hero, Command.Skill("tremor", null, -1, HexDirection.E)));
        TestUtil.RunUntil(sim, () => mole.Hp < mole.HpMax, 2);
        Assert.True(mole.Hp < mole.HpMax);
        // Adjacent → revealed; tremor sense → seen at range.
        hero.Stats.SetBase("tremor_sense", 1);
        Assert.True(sim.Vision.CanSee(hero, mole));
    }

    [Fact]
    public void Layers_DoNotCollide_AndCellEffectsRespectLayers()
    {
        var db = Db();
        db.Add(new CellEffectDef { Id = "burn", Name = "Огонь", Duration = 0, TickInterval = 0.5, OnTick = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 5, Filter = TargetFilterDef.Everyone() } } });
        var sim = TestUtil.Sim(db, TestUtil.Field(6, 2));
        var hero = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 0), HexDirection.E);
        var bat = sim.Spawn(db.Unit("bat")!, null, new HexCoord(3, 0), HexDirection.W);
        // The flyer can hover over the hero's cell.
        Assert.Null(sim.Enqueue(bat, Command.Move(hero.Pos)));
        TestUtil.RunUntil(sim, () => bat.Pos == hero.Pos, 3);
        Assert.Equal(hero.Pos, bat.Pos);
        // Ground fire burns the hero but not the bat above it.
        sim.CellEffects.Add(sim.Map.Get(hero.Pos)!, db.CellEffect("burn")!, 1, 0, -1, 0, 0);
        sim.RunSeconds(1.2);
        Assert.True(hero.Hp < hero.HpMax);
        Assert.Equal(bat.HpMax, bat.Hp);
    }
}
