using System;
using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;

namespace HexTactics.Tests;

/// <summary>Small hand-built content for isolated tests + access to the shipped content pack.</summary>
public static class TestUtil
{
    public static string ContentDir => Path.Combine(AppContext.BaseDirectory, "content");

    private static ContentDatabase? _shipped;

    /// <summary>The shipped content pack (game/content), loaded once and cloned per call.</summary>
    public static ContentDatabase Shipped()
    {
        if (_shipped == null)
        {
            var res = ContentIO.Load(new FileSystemStorage(ContentDir, readOnly: true));
            if (res.HasErrors) throw new InvalidOperationException(string.Join("\n", res.Issues.Where(i => i.Severity == IssueSeverity.Error)));
            _shipped = res.Db;
        }
        var c = _shipped.Clone();
        ContentValidator.Validate(c);
        return c;
    }

    public static ContentDatabase Minimal()
    {
        var db = new ContentDatabase { Rules = new RulesDef() };
        db.Rules.Time.StartPaused = false;
        foreach (var s in Stats.All) db.Add(new StatDef { Id = s, Name = s, Default = DefaultStat(s), Min = -1000, Max = 100000 });
        foreach (var t in new[] { "physical", "fire", "poison", "electric", "ice" })
        {
            db.Add(new DamageTypeDef { Id = t, Name = t, ArmorApplies = t == "physical" });
            db.Add(new StatDef { Id = "resist_" + t, Name = "resist " + t, Min = -1, Max = 1 });
        }
        db.Add(new ResourceDef { Id = "mana", Name = "Мана" });
        db.Add(new FactionDef { Id = "player", Name = "P", HostileTo = { "enemy" } });
        db.Add(new FactionDef { Id = "enemy", Name = "E", HostileTo = { "player" } });
        db.Add(new FactionDef { Id = "neutral", Name = "N" });
        db.Add(new TerrainDef { Id = "grass", Name = "Трава", MoveCost = 1, Tags = { "ground", "grass" } });
        db.Add(new TerrainDef { Id = "mud", Name = "Грязь", MoveCost = 3, Tags = { "ground" } });
        db.Add(new TerrainDef { Id = "water", Name = "Вода", MoveCost = 2, Tags = { "water", "shallow" } });
        db.Add(new TerrainDef { Id = "wall", Name = "Стена", PassableFor = new(), BlocksLoS = true, ObstacleHeight = 3, AllowsPlacement = false, Tags = { "wall" } });
        db.Add(new TerrainDef { Id = "ice_floor", Name = "Лёд", MoveCost = 1, Tags = { "ice" } });
        db.Add(new TerrainDef { Id = "crate", Name = "Ящик", PassableFor = new(), BlocksLoS = true, ObstacleHeight = 1, DestructibleHp = 10, DestroyedTerrain = "grass" });
        db.Add(new AiProfileDef { Id = "aggressive", Name = "A" });
        db.Add(new SkillDef
        {
            Id = "hit", Name = "Удар", Targeting = TargetingMode.Unit, ApCost = 2, PrepTime = 0.2, RecoveryTime = 0.1,
            Stages = new() { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 10 } } } },
        });
        db.Add(new UnitDef
        {
            Id = "soldier", Name = "Солдат", Faction = "player", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        db.Add(new UnitDef
        {
            Id = "dummy", Name = "Манекен", Faction = "enemy", BasicAttack = "hit", AiProfile = "aggressive",
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        return db;
    }

    private static double DefaultStat(string s) => s switch
    {
        Stats.HpMax => 100,
        Stats.ApMax => 10,
        Stats.ApRegen => 1,
        Stats.MoveSpeed => 2,
        Stats.DetectionRange => 8,
        Stats.CritMult => 1.5,
        Stats.BlockReduction => 0.5,
        _ => 0,
    };

    public static MapDef Field(int w = 12, int h = 8, string terrain = "grass") => MapDef.CreateRhombus("field", w, h, terrain);

    /// <summary>Simulation with no AI factions (the enemy is passive unless a test enables AI).</summary>
    public static Simulation Sim(ContentDatabase db, MapDef map, ulong seed = 42, bool ai = false)
    {
        return new Simulation(db, map, new SimOptions
        {
            Seed = seed,
            PlayerFactions = new() { "player" },
            AiFactions = ai ? new() { "enemy" } : new(),
            FogOfWar = false,
        });
    }

    public static void SetCell(MapDef m, HexCoord c, string terrain, int h = 0)
    {
        var cell = m.Cells.First(x => x.C == c);
        cell.T = terrain;
        cell.H = h;
    }

    public static Unit RunUntil(Simulation sim, Func<bool> cond, double maxSeconds, Unit? u = null)
    {
        long max = sim.TicksFor(maxSeconds);
        for (long i = 0; i < max && !cond(); i++) sim.Step();
        return u!;
    }
}
