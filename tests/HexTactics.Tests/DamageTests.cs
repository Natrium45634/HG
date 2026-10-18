using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Shared setup: minimal content with elements, a damage type hierarchy and a duel on a field.</summary>
internal static class DamageSetup
{
    public static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new ElementDef { Id = "water", Name = "Вода", Color = "#3f8cff", Statuses = { new ElementStatusDef { Status = "wet", Amount = 1, Duration = 6 } } });
        db.Add(new ElementDef { Id = "wind", Name = "Ветер", Color = "#9fe0d0" });
        db.Add(new ElementDef
        {
            Id = "lightning", Name = "Молния", Parent = "wind",
            Interactions = { new ElementInteractionDef { With = "water", Partner = InteractionPartner.Either, DamagePercent = 50, LogText = "проводимость" } },
        });
        db.Add(new ElementDef { Id = "ice", Name = "Лёд", Parent = "water" });
        db.Add(new ElementDef
        {
            Id = "fire", Name = "Огонь", Color = "#ff7a2e",
            Interactions = { new ElementInteractionDef { With = "water", Partner = InteractionPartner.Either, DamagePercent = -50, CreateCellEffect = "steam", Consume = true } },
        });
        db.Get<DamageTypeDef>("fire")!.Element = "fire";
        db.Get<DamageTypeDef>("ice")!.Element = "ice";
        db.Get<DamageTypeDef>("electric")!.Element = "lightning";
        db.Add(new DamageTypeDef { Id = "water", Name = "Водный", Kind = DamageCategory.Elemental, Element = "water" });
        db.Add(new DamageTypeDef { Id = "slashing", Name = "Режущий", Parent = "physical", ArmorApplies = true });
        db.Add(new DamageTypeDef { Id = "true", Name = "Чистый", Kind = DamageCategory.Custom, CustomCategory = "чистый" });
        db.Add(new StatusDef { Id = "wet", Name = "Мокрый", Element = "water", Duration = 6, Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new CellEffectDef { Id = "steam", Name = "Пар", Element = "water", Duration = 4 });
        db.Add(new CellEffectDef { Id = "lava_heat", Name = "Жар лавы", Element = "fire", Duration = 0 });
        foreach (var t in new[] { "fire", "ice", "electric", "poison" }) db.Get<DamageTypeDef>(t)!.Kind = DamageCategory.Elemental;
        db.Rules.Combat.MinDamageOnHit = 0;
        db.Unit("dummy")!.Stats[Stats.HpMax] = 1000;
        return db;
    }

    public static (Simulation Sim, Unit Att, Unit Def) Duel(ContentDatabase db)
    {
        var sim = TestUtil.Sim(db, TestUtil.Field(), seed: 3);
        var att = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var def = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        def.Hp = 1000;
        return (sim, att, def);
    }

    public static DamageResult Hit(Simulation sim, Unit? att, Unit def, double amount, string type, DamageSource src = DamageSource.Other, bool keepHp = true)
    {
        double hp = def.Hp;
        var r = sim.Damage.Apply(new DamageRequest { Attacker = att, Target = def, Amount = amount, DamageType = type, Source = src, Cause = "тест" });
        if (keepHp) def.Hp = hp;
        return r;
    }

    public static void AddMod(Unit u, string match, double percent)
    {
        u.Def.Defense.Modifiers.Add(new DamageModDef { Match = match, Percent = percent });
        u.Defenses.Clear();
        u.Defenses.Add(u.Def.Defense);
    }
}

/// <summary>TZ part 2 §16 test 15: resistances, vulnerabilities and modifiers from −500 % to +500 %.</summary>
public class ResistanceTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 200)]
    [InlineData(500, 600)]
    [InlineData(-50, 50)]
    [InlineData(-100, 0)]
    [InlineData(-300, 0)]
    [InlineData(-500, 0)]
    public void Formula_D_Equals_D0_Times_OnePlusV(double v, double expected)
    {
        var db = DamageSetup.Db();
        var (sim, att, def) = DamageSetup.Duel(db);
        DamageSetup.AddMod(def, "magic", v);
        var r = DamageSetup.Hit(sim, att, def, 100, "magic");
        Assert.Equal(expected, r.HealthLost + r.Overkill, 3);
    }

    [Fact]
    public void BelowMinus100_NeverHeals()
    {
        var db = DamageSetup.Db();
        var (sim, att, def) = DamageSetup.Duel(db);
        DamageSetup.AddMod(def, "magic", -500);
        def.Hp = 500;
        DamageSetup.Hit(sim, att, def, 100, "magic", keepHp: false);
        Assert.Equal(500, def.Hp);
    }

    [Fact]
    public void Modifiers_Sum_OverType_Ancestors_Element_Category_Any_AndAreClamped()
    {
        var db = DamageSetup.Db();
        var (sim, att, def) = DamageSetup.Duel(db);
        // slashing → physical (parent) → cat:Physical → "*".
        DamageSetup.AddMod(def, "physical", 50);
        DamageSetup.AddMod(def, "slashing", 25);
        DamageSetup.AddMod(def, "cat:Physical", 25);
        DamageSetup.AddMod(def, "*", -50);
        Assert.Equal(50, sim.Damage.ModifierPercent(def, "slashing"), 3);
        Assert.Equal(25, sim.Damage.ModifierPercent(def, "physical"), 3);
        // Element and its ancestors: ice is a child of water.
        DamageSetup.AddMod(def, "el:water", 100);
        Assert.Equal(50, sim.Damage.ModifierPercent(def, "ice"), 3);
        Assert.Equal(-50, sim.Damage.ModifierPercent(def, "fire"), 3);
        // Sum beyond +500 is clamped by the rules.
        DamageSetup.AddMod(def, "ice", 500);
        DamageSetup.AddMod(def, "ice", 500);
        var r = DamageSetup.Hit(sim, att, def, 10, "ice");
        Assert.Equal(500, r.ModifierPercent, 3);
        Assert.Equal(60, r.HealthLost, 3);
    }

    [Fact]
    public void LegacyResistStat_IsMinus100TimesFraction()
    {
        var db = DamageSetup.Db();
        db.Get<StatDef>("resist_fire")!.Min = -5;
        db.Get<StatDef>("resist_fire")!.Max = 5;
        db.Unit("dummy")!.Stats["resist_fire"] = 0.5;
        var (sim, att, def) = DamageSetup.Duel(db);
        Assert.Equal(-50, sim.Damage.ModifierPercent(def, "fire"), 3);
        Assert.Equal(50, DamageSetup.Hit(sim, att, def, 100, "fire").HealthLost, 3);
    }

    [Fact]
    public void Migration_V3_Fractions_BecomePercent_AndImmunity()
    {
        var json = """
        { "formatVersion": 3, "kind": "units", "items": [ { "id": "x", "resistances": { "fire": 0.3, "electric": -0.5, "poison": 1 } } ] }
        """;
        var root = ContentMigrator.Migrate(JsonNode.Parse(json)!, "units", out var notes, out var error)!;
        Assert.Null(error);
        var u = root["items"]![0]!.AsObject();
        Assert.False(u.ContainsKey("resistances"));
        var mods = u["defense"]!["modifiers"]!.AsArray();
        Assert.Contains(mods, m => (string?)m!["match"] == "fire" && (double)m["percent"]! == -30);
        Assert.Contains(mods, m => (string?)m!["match"] == "electric" && (double)m["percent"]! == 50);
        Assert.Contains(u["defense"]!["immune"]!.AsArray(), x => (string?)x == "poison");
        Assert.Contains(notes, n => n.Contains("проценты"));
    }

    [Fact]
    public void Validator_RejectsUnknownMatch_AndParentCycles()
    {
        var db = DamageSetup.Db();
        db.Unit("dummy")!.Defense.Modifiers.Add(new DamageModDef { Match = "el:plasma", Percent = 10 });
        db.Element("wind")!.Parent = "lightning";
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("el:plasma"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("Цикл наследования"));
    }
}

/// <summary>TZ part 2 §16 test 16: absorption, reflection, crits, healing, shields and periodic effects.</summary>
public class DamageMechanicsTests
{
    [Fact]
    public void Absorption_IntoHealth_WithCap_AndIntoResource()
    {
        var db = DamageSetup.Db();
        db.Unit("dummy")!.Defense.Absorptions.Add(new AbsorptionDef { Match = "electric", Percent = 50, CapPerHit = 20, Into = AbsorbInto.Health });
        db.Unit("dummy")!.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 100, Start = 0 });
        db.Unit("dummy")!.Defense.Absorptions.Add(new AbsorptionDef { Match = "fire", Percent = 100, Into = AbsorbInto.Resource, Resource = "mana" });
        var (sim, att, def) = DamageSetup.Duel(db);
        def.Hp = 500;
        var r = DamageSetup.Hit(sim, att, def, 100, "electric", keepHp: false);
        Assert.Equal(20, r.Absorbed, 3); // 50 % of 100 = 50, capped at 20
        Assert.Equal(80, r.HealthLost, 3);
        Assert.Equal(500 - 80 + 20, def.Hp, 3); // absorbed part returns as health
        DamageSetup.Hit(sim, att, def, 30, "fire");
        Assert.Equal(30, def.Resources["mana"].Current, 3);
    }

    [Fact]
    public void Reflection_HitsAttacker_AndDoesNotLoopForever()
    {
        var db = DamageSetup.Db();
        db.Unit("dummy")!.Defense.Reflections.Add(new ReflectionDef { Match = "*", Percent = 50 });
        db.Unit("soldier")!.Defense.Reflections.Add(new ReflectionDef { Match = "*", Percent = 50 });
        var (sim, att, def) = DamageSetup.Duel(db);
        att.Hp = 1000;
        var r = DamageSetup.Hit(sim, att, def, 40, "magic", DamageSource.Skill, keepHp: false);
        Assert.Equal(40, r.HealthLost, 3);
        Assert.Equal(20, r.Reflected, 3);
        Assert.Equal(1000 - 20, att.Hp, 3);
        // The reflected 20 is not reflected back (depth 1): the defender took only the original hit.
        Assert.Equal(1000 - 40, def.Hp, 3);
        // Depth 0 disables reflection completely.
        db.Rules.Damage.ReflectMaxDepth = 0;
        Assert.Equal(0, DamageSetup.Hit(sim, att, def, 40, "magic", DamageSource.Skill).Reflected);
    }

    [Fact]
    public void Crits_Multiplier_Immunity_EffectsWithoutDamage_AndNoPeriodicCrits()
    {
        var db = DamageSetup.Db();
        db.Unit("soldier")!.Stats[Stats.CritChance] = 1;
        db.Unit("soldier")!.Stats[Stats.CritMult] = 2;
        var (sim, att, def) = DamageSetup.Duel(db);
        var skill = db.Skill("hit")!;
        var roll = sim.Combat.Roll(att, def, skill, att.Pos);
        Assert.True(roll.Crit);
        var r = sim.Damage.Apply(new DamageRequest { Attacker = att, Target = def, Amount = 10, DamageType = "magic", Source = DamageSource.Skill, Skill = skill, Roll = roll });
        Assert.True(r.Crit);
        Assert.Equal(20, r.HealthLost, 3);
        // A skill that cannot crit.
        skill.CanCrit = false;
        Assert.False(sim.Combat.Roll(att, def, skill, att.Pos).Crit);
        skill.CanCrit = true;
        // Crit without extra damage.
        skill.CritNoExtraDamage = true;
        r = sim.Damage.Apply(new DamageRequest { Attacker = att, Target = def, Amount = 10, DamageType = "magic", Source = DamageSource.Skill, Skill = skill, Roll = sim.Combat.Roll(att, def, skill, att.Pos) });
        Assert.True(r.Crit);
        Assert.Equal(10, r.HealthLost, 3);
        skill.CritNoExtraDamage = false;
        // Periodic damage does not crit by default (rules table), even with 100 % chance.
        var p = DamageSetup.Hit(sim, att, def, 10, "magic", DamageSource.Periodic);
        Assert.False(p.Crit);
        // Crit immunity of the target.
        def.Defenses.Add(new DefenseDef { CritImmune = true });
        Assert.False(sim.Combat.Roll(att, def, skill, att.Pos).Crit);
    }

    [Fact]
    public void Healing_Modes_HealReceived_NoHealing_AndOverhealShield()
    {
        var db = DamageSetup.Db();
        db.Add(new StatusDef { Id = "grievous", Name = "Запрет лечения", Duration = 5, Levels = { new StatusLevelDef { Threshold = 1, Flags = { BlocksHealing = true } } } });
        var (sim, att, def) = DamageSetup.Duel(db);
        def.Stats.SetBase(Stats.HpMax, 100);
        def.Hp = 40;
        Assert.Equal(10, sim.Combat.Heal(null, def, 10, "т"), 3);
        Assert.Equal(20, sim.Combat.Heal(null, def, 20, "т", HealMode.PercentMax), 3);
        Assert.Equal(15, sim.Combat.Heal(null, def, 50, "т", HealMode.PercentMissing), 3); // missing 30 → 15
        def.Hp = 40;
        def.Stats.SetBase(Stats.HealReceived, 0.5);
        Assert.Equal(15, sim.Combat.Heal(null, def, 10, "т"), 3);
        def.Stats.SetBase(Stats.HealReceived, 0);
        sim.Statuses.Apply(def, db.Status("grievous")!, 1, 5, -1);
        Assert.Equal(0, sim.Combat.Heal(null, def, 10, "т"), 3);
        sim.Statuses.Remove(def, def.FindStatus("grievous")!, "т");
        // Overheal → shield by the rules.
        db.Rules.Damage.OverhealToShieldPercent = 50;
        def.Hp = 90;
        sim.Combat.Heal(null, def, 30, "т");
        Assert.Equal(100, def.Hp, 3);
        Assert.Equal(10, def.ShieldTotal, 3); // 20 over × 50 %
    }

    [Fact]
    public void Shields_AbsorbByPriority_OnlyProtectedTypes_Pierce_Regen_Expiry()
    {
        var db = DamageSetup.Db();
        var (sim, att, def) = DamageSetup.Duel(db);
        def.Hp = 500;
        sim.Damage.AddShield(def, new ShieldSpecDef { Name = "Огнеупорный", Protects = { "el:fire" }, Priority = 5 }, 30, 0, -1);
        sim.Damage.AddShield(def, new ShieldSpecDef { Name = "Общий", Priority = 1 }, 20, 2, -1);
        // Physical: only the general shield protects.
        var r = DamageSetup.Hit(sim, att, def, 15, "physical", keepHp: false);
        Assert.Equal(15, r.Shielded, 3);
        Assert.Equal(0, r.HealthLost, 3);
        // Fire: the higher-priority fire shield goes first.
        r = DamageSetup.Hit(sim, att, def, 40, "fire", keepHp: false);
        Assert.Equal(35, r.Shielded, 3); // 30 fire shield + 5 left of the general one
        Assert.Equal(5, r.HealthLost, 3);
        Assert.Empty(def.Shields);
        // Piercing type ignores shields.
        sim.Damage.AddShield(def, new ShieldSpecDef { Name = "Общий" }, 50, 0, -1);
        db.Get<DamageTypeDef>("true")!.IgnoresShields = true;
        Assert.Equal(0, DamageSetup.Hit(sim, att, def, 10, "true").Shielded);
        // Regeneration after the delay; expiry.
        sim.Damage.RemoveShields(def, "", "т");
        var s = sim.Damage.AddShield(def, new ShieldSpecDef { Name = "Реген", RegenPerSecond = 10, RegenDelay = 1 }, 30, 3, -1)!;
        DamageSetup.Hit(sim, att, def, 20, "physical");
        Assert.Equal(10, s.Amount, 3);
        TestUtil.RunUntil(sim, () => false, 2);
        Assert.True(s.Amount > 10, "щит должен восстанавливаться после задержки");
        TestUtil.RunUntil(sim, () => false, 2);
        Assert.Empty(def.Shields); // expired after 3 s
    }

    [Fact]
    public void Periodic_TickCount_SnapshotPower_RemovalConditions_AndPause()
    {
        var db = DamageSetup.Db();
        db.Add(new StatusDef
        {
            Id = "dot", Name = "Яд", Duration = 0, TickCount = 3, Power = PeriodicPower.Snapshot,
            Levels = { new StatusLevelDef { Threshold = 1, TickInterval = 0.5, OnTick = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "magic", Amount = 0, ScalingStat = Stats.Power, ScalingFactor = 1 } } } },
            RemoveOn = { new StatusRemoveDef { When = StatusRemoveTrigger.DamagedBy, Damage = "el:water" } },
        });
        db.Unit("soldier")!.Stats[Stats.Power] = 10;
        var (sim, att, def) = DamageSetup.Duel(db);
        def.Hp = 1000;
        sim.Statuses.Apply(def, db.Status("dot")!, 1, 0, att.Id);
        // Power changes after application: the snapshot keeps 10 per tick.
        att.Stats.SetBase(Stats.Power, 100);
        // Paused (no steps) → no ticks.
        Assert.Equal(1000, def.Hp, 3);
        TestUtil.RunUntil(sim, () => def.FindStatus("dot") == null, 5);
        Assert.Null(def.FindStatus("dot"));
        Assert.Equal(1000 - 30, def.Hp, 3); // exactly 3 ticks × 10

        // Current power: the same status without snapshot uses the new value.
        db.Status("dot")!.Power = PeriodicPower.Current;
        sim.Statuses.Apply(def, db.Status("dot")!, 1, 0, att.Id);
        double hp = def.Hp;
        TestUtil.RunUntil(sim, () => def.Hp < hp, 1);
        Assert.Equal(hp - 100, def.Hp, 3);
        // Removal on water damage.
        DamageSetup.Hit(sim, null, def, 1, "water");
        Assert.Null(def.FindStatus("dot"));
    }

    [Fact]
    public void SourceRules_TerrainHasNoAttackerBonuses_FallIgnoresArmor()
    {
        var db = DamageSetup.Db();
        db.Unit("soldier")!.Offense.Bonuses.Add(new DamageModDef { Match = "*", Percent = 100 });
        db.Unit("dummy")!.Stats[Stats.Armor] = 5;
        var (sim, att, def) = DamageSetup.Duel(db);
        sim.Statuses.RecalculateModifiers(att);
        Assert.Equal(20, DamageSetup.Hit(sim, att, def, 10, "magic", DamageSource.Skill).HealthLost, 3);
        Assert.Equal(10, DamageSetup.Hit(sim, att, def, 10, "magic", DamageSource.Terrain).HealthLost, 3);
        Assert.Equal(5, DamageSetup.Hit(sim, null, def, 10, "physical", DamageSource.Terrain).HealthLost, 3);
        Assert.Equal(10, DamageSetup.Hit(sim, null, def, 10, "physical", DamageSource.Fall).HealthLost, 3);
        // The breakdown lists the steps.
        var r = DamageSetup.Hit(sim, att, def, 10, "physical", DamageSource.Skill);
        Assert.Contains(r.Steps, s => s.Name == "бонусы атакующего");
        Assert.Contains(r.Steps, s => s.Name == "броня, снижение, блок");
        Assert.Contains("исходный урон", r.Breakdown());
    }
}

/// <summary>TZ part 2 §16 test 17: element synergies and hazardous terrain.</summary>
public class ElementTests
{
    [Fact]
    public void Inheritance_ChildTakesParentFields_AndEditorShowsOrigin()
    {
        var db = DamageSetup.Db();
        var ice = DamageTaxonomy.Resolve(db, "ice")!;
        Assert.Equal("#3f8cff", ice.Color); // from water
        Assert.Contains(ice.Statuses, s => s.Status == "wet");
        Assert.Equal(new[] { "ice", "water" }, ice.Chain);
        Assert.Contains("унаследовано от «Вода»", db.Element("ice")!.InheritedHint(db, nameof(ElementDef.Color))!);
        db.Element("ice")!.Color = "#9ad8ff";
        Assert.Equal("задано здесь", db.Element("ice")!.InheritedHint(db, nameof(ElementDef.Color)));
        // No list inheritance: an empty list stays empty.
        db.Element("ice")!.NoListInheritance = true;
        Assert.Empty(DamageTaxonomy.Resolve(db, "ice")!.Statuses);
    }

    [Fact]
    public void WaterWets_LightningConducts_FireSteams()
    {
        var db = DamageSetup.Db();
        var (sim, att, def) = DamageSetup.Duel(db);
        Assert.Equal(20, DamageSetup.Hit(sim, att, def, 20, "electric").HealthLost, 3);
        DamageSetup.Hit(sim, att, def, 5, "water");
        Assert.NotNull(def.FindStatus("wet"));
        var r = DamageSetup.Hit(sim, att, def, 20, "electric");
        Assert.Equal(30, r.HealthLost, 3); // +50 % conduction
        Assert.Contains(r.Effects, e => e.Contains("проводимость"));
        // Fire on a wet target: −50 %, the wetness is consumed, steam appears.
        r = DamageSetup.Hit(sim, att, def, 20, "fire");
        Assert.Equal(10, r.HealthLost, 3);
        Assert.Null(def.FindStatus("wet"));
        Assert.Contains(sim.Map.Get(def.Pos)!.Effects, e => e.Def.Id == "steam");
    }

    [Fact]
    public void HazardousTerrain_IsAnElementPartner_AndHasItsOwnSource()
    {
        var db = DamageSetup.Db();
        var (sim, att, def) = DamageSetup.Duel(db);
        var cell = sim.Map.Get(def.Pos)!;
        // A unit standing in lava heat (fire element) is hit by water → steam, the fire effect is consumed.
        sim.CellEffects.Add(cell, db.CellEffect("lava_heat")!, 1, 0, -1, 0, 0, EffectOrigin.Terrain);
        db.Element("water")!.Interactions.Add(new ElementInteractionDef { With = "fire", Partner = InteractionPartner.CellEffect, CreateCellEffect = "steam", Consume = true });
        var tax = new DamageTaxonomy(db);
        Assert.True(tax.ElementIs("lightning", "wind"));
        var p = new DamagePipeline(sim);
        var r = p.Apply(new DamageRequest { Attacker = att, Target = def, Amount = 10, DamageType = "water", Source = DamageSource.Skill, Cause = "т" });
        Assert.Equal(10, r.HealthLost, 3);
        Assert.Contains(cell.Effects, e => e.Def.Id == "steam");
        Assert.DoesNotContain(cell.Effects, e => e.Def.Id == "lava_heat");
    }

    [Fact]
    public void ShippedContent_HasBasicElements_HierarchyAndValidPipelineData()
    {
        var db = TestUtil.Shipped();
        foreach (var id in new[] { "fire", "water", "wind", "earth" }) Assert.Equal(ElementTier.Basic, db.Element(id)!.Tier);
        Assert.Equal("physical", db.DamageType("slashing")!.Parent);
        Assert.Equal("lightning", db.DamageType("electric")!.Element);
        var tax = new DamageTaxonomy(db);
        Assert.True(tax.Matches("cat:Physical", "piercing"));
        Assert.True(tax.Matches("el:wind", "electric"));
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error);
    }
}
