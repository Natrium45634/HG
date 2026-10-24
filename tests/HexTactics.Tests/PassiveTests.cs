using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 3, stage 6: passives and talents, modifiers and their summing (docs/DECISIONS_3.md §46).</summary>
public class PassiveTests
{
    private static ModifierDef Stat(string stat, double v, ModifierMode mode = ModifierMode.Percent, int priority = 0, string category = "") =>
        new() { Target = ModifierTarget.Stat, Stat = stat, Value = v, Mode = mode, Priority = priority, Category = category };

    private static PassiveDef Passive(string id, params ModifierDef[] mods)
    {
        var p = new PassiveDef { Id = id, Name = id };
        p.Modifiers.AddRange(mods);
        return p;
    }

    private static double Hit(Simulation sim, Unit attacker, Unit target, double amount, string type, SkillDef? skill = null)
    {
        double before = target.Hp;
        sim.Damage.Apply(new DamageRequest { Attacker = attacker, Target = target, Amount = amount, DamageType = type, Source = DamageSource.Skill, Skill = skill,
            Roll = new AttackRoll { Hit = true, Noticed = true } });
        return before - target.Hp;
    }

    [Fact]
    public void SummingModes_AsInTheSpec()
    {
        double Pct(params Contribution[] c) => ModifierMath.Combine(c).Percent;
        Assert.Equal(45, Pct(new(ModifierMode.Percent, 20, 0, "", "a"), new(ModifierMode.Percent, 25, 0, "", "b")), 6);
        Assert.Equal(1.5, ModifierMath.Combine(new[] { new Contribution(ModifierMode.Multiplier, 1.2, 0, "", "a"), new Contribution(ModifierMode.Multiplier, 1.25, 0, "", "b") }).Multiplier, 6);
        Assert.Equal(30, Pct(new(ModifierMode.Max, 10, 0, "", "a"), new(ModifierMode.Max, 30, 0, "", "b")), 6);
        Assert.Equal(-40, Pct(new(ModifierMode.Min, -10, 0, "", "a"), new(ModifierMode.Min, -40, 0, "", "b")), 6);
        Assert.Equal(5, Pct(new(ModifierMode.Priority, 50, 1, "", "low"), new(ModifierMode.Priority, 5, 9, "", "high")), 6);
        var ex = ModifierMath.Combine(new[]
        {
            new Contribution(ModifierMode.Exclusive, 15, 0, "blessing", "a"), new Contribution(ModifierMode.Exclusive, 25, 0, "blessing", "b"),
            new Contribution(ModifierMode.Exclusive, 10, 0, "potion", "c"),
        });
        Assert.Equal(35, ex.Percent, 6);
        Assert.Contains(ex.Items, i => i.C.Source == "a" && !i.Applied);
        // +10 before or after the percents.
        var c = ModifierMath.Combine(new[] { new Contribution(ModifierMode.FlatBefore, 10, 0, "", "x"), new Contribution(ModifierMode.Percent, 50, 0, "", "y"), new Contribution(ModifierMode.FlatAfter, 10, 0, "", "z") });
        Assert.Equal((100 + 10) * 1.5 + 10, ModifierMath.Apply(100, c, StatCalcOrder.Standard), 6);
        Assert.Equal(100 * 1.5 + 20, ModifierMath.Apply(100, c, StatCalcOrder.PercentOfBase), 6);
        Assert.Equal(120, ModifierMath.Apply(100, c, StatCalcOrder.FlatOnly), 6);
    }

    [Fact]
    public void Stats_OrderPerStat_AndTheShareOfEverySource()
    {
        var db = TestUtil.Minimal();
        db.Stat(Stats.Armor)!.CalcOrder = StatCalcOrder.PercentOfBase;
        db.Stat(Stats.Power)!.Max = 1000;
        db.Add(Passive("tough", Stat(Stats.Armor, 50), Stat(Stats.Armor, 4, ModifierMode.FlatBefore), Stat(Stats.Power, 50), Stat(Stats.Power, 4, ModifierMode.FlatBefore)));
        db.Add(Passive("lucky", Stat(Stats.Power, 1.2, ModifierMode.Multiplier)));
        var def = db.Unit("soldier")!;
        def.Stats[Stats.Armor] = 10;
        def.Stats[Stats.Power] = 10;
        def.Passives.AddRange(new[] { "tough", "lucky" });
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(def, null, new HexCoord(2, 2), HexDirection.E);
        Assert.Equal(10 * 1.5 + 4, u.Stat(Stats.Armor), 6);              // armor: percents of the base only
        Assert.Equal((10 + 4) * 1.5 * 1.2, u.Stat(Stats.Power), 6);      // power: (base + flat) × % × ×
        var b = u.Stats.Explain(Stats.Power);
        Assert.Equal(3, b.Items.Count);
        Assert.All(b.Items, i => Assert.Contains("юнит", i.C.Source));
        Assert.Contains(b.Items, i => i.C.Source.StartsWith("lucky") && i.C.Mode == ModifierMode.Multiplier);
    }

    [Fact]
    public void Slime_TakesQuarterOfWaterMagic_NotOfFire()
    {
        var db = TestUtil.Shipped();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var slime = sim.Spawn(db.Unit("slime")!, "enemy", new HexCoord(3, 2), HexDirection.W);
        var mage = sim.Spawn(db.Unit("mage")!, "player", new HexCoord(2, 2), HexDirection.E);
        slime.Stats.SetBase(Stats.Armor, 0);
        var goblin = sim.Spawn(db.Unit("goblin")!, "enemy", new HexCoord(3, 3), HexDirection.W);
        goblin.Stats.SetBase(Stats.Armor, 0);
        // The same body without the species passive: water magic ×4 more, fire the same.
        Assert.Equal(20, Hit(sim, mage, goblin, 20, "water"));
        Assert.Equal(5, Hit(sim, mage, slime, 20, "water"));
        Assert.Equal(Hit(sim, mage, goblin, 20, "fire"), Hit(sim, mage, slime, 20, "fire"));
        var p = db.Get<PassiveDef>("water_amorphous")!;
        string text = PassiveText.Lines(p, db)[0];
        Assert.Contains("на 75 % меньше урона", text);
        Assert.Contains("Получает", text);
        Assert.DoesNotContain("-75", text);
        Assert.Contains(slime.PassiveInstances, x => x.Def.Id == "water_amorphous" && x.Source.Contains("Кислотная слизь"));
    }

    [Fact]
    public void ElfTalent_OnlyNatureMagicSkills_AndScenarioCanDisableOrReplaceIt()
    {
        var db = TestUtil.Shipped();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var elf = sim.Spawn(db.Unit("elf_ranger")!, "player", new HexCoord(2, 2), HexDirection.E);
        var dummy = sim.Spawn(db.Unit("goblin")!, "enemy", new HexCoord(3, 2), HexDirection.W);
        dummy.Stats.SetBase(Stats.Armor, 0);
        dummy.Stats.SetBase(Stats.HpMax, 10000);
        dummy.Hp = 10000;
        double nature = Hit(sim, elf, dummy, 20, "earth", db.Skill("thorn_lash"));
        double plain = Hit(sim, elf, dummy, 20, "earth", db.Skill("shot"));
        Assert.Equal(25, nature);
        Assert.Equal(20, plain);

        // The scenario switches the race talent off (or replaces it).
        db.Add(Passive("weak_nature", new ModifierDef { Target = ModifierTarget.OutgoingDamage, SkillTag = "nature_magic", Value = 10 }));
        var scen = ContentJson.Clone(db.Get<ScenarioDef>("arena_duel")!);
        scen.Id = "test_override";
        scen.PassiveOverrides.Add(new PassiveOverrideDef { Passive = "nature_affinity", ReplaceWith = "weak_nature" });
        db.Add(scen);
        var sim2 = ScenarioSystem.Create(db, scen, 1);
        var elf2 = sim2.Spawn(db.Unit("elf_ranger")!, "player", new HexCoord(0, 0), HexDirection.E);
        Assert.DoesNotContain(elf2.PassiveInstances, x => x.Def.Id == "nature_affinity");
        Assert.Contains(elf2.PassiveInstances, x => x.Def.Id == "weak_nature" && x.Source.Contains("заменено"));

        // Who uses the talent: the origin «Эльф» (and through it the elf units).
        Assert.Contains(ContentDependencies.UsedBy(db, db.Get<PassiveDef>("nature_affinity")!), d => d.Id == "elf");
    }

    [Fact]
    public void Conditions_TerrainAndHealth_RecheckedByInterval()
    {
        var db = TestUtil.Minimal();
        var p = Passive("ambush", Stat(Stats.Power, 20));
        p.When = new UnitConditionDef { Type = UnitConditionType.OnTerrain, Subject = ConditionSubject.Self, Terrains = { "tag:water" } };
        db.Add(p);
        var low = Passive("desperate", Stat(Stats.Evasion, 0.2, ModifierMode.FlatBefore));
        low.When = new UnitConditionDef { Type = UnitConditionType.HpFraction, Subject = ConditionSubject.Self, Compare = CompareMode.AtMost, Value = 0.5 };
        db.Add(low);
        var def = db.Unit("soldier")!;
        def.Stats[Stats.Power] = 10;
        def.Passives.AddRange(new[] { "ambush", "desperate" });
        var map = TestUtil.Field();
        TestUtil.SetCell(map, new HexCoord(3, 2), "water");
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(def, null, new HexCoord(2, 2), HexDirection.E);
        Assert.Equal(10, u.Stat(Stats.Power), 6);
        Assert.Contains(u.PassiveInstances, x => x.Def.Id == "ambush" && !x.Active && x.Inactive.Contains("условие"));
        sim.Enqueue(u, Command.Move(new HexCoord(3, 2)));
        TestUtil.RunUntil(sim, () => u.Pos == new HexCoord(3, 2), 5);
        sim.RunSeconds(0.3);
        Assert.Equal(12, u.Stat(Stats.Power), 6);
        u.Hp = u.HpMax * 0.4;
        sim.RunSeconds(0.3);
        Assert.Equal(0.2, u.Stat(Stats.Evasion), 6);
    }

    [Fact]
    public void Aura_GivesAlliesInRadius_OneInstanceFromSeveralOwners()
    {
        var db = TestUtil.Minimal();
        var aura = Passive("guard", Stat(Stats.Armor, 2, ModifierMode.FlatBefore));
        aura.AuraRadius = 2;
        db.Add(aura);
        var guardDef = ContentJson.Clone(db.Unit("soldier")!);
        guardDef.Id = "guard";
        guardDef.Passives.Add("guard");
        db.Add(guardDef);
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var g1 = sim.Spawn(guardDef, null, new HexCoord(2, 2), HexDirection.E);
        var g2 = sim.Spawn(guardDef, null, new HexCoord(2, 4), HexDirection.E);
        var near = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 3), HexDirection.E);
        var far = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(8, 3), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 3), HexDirection.W);
        sim.RunSeconds(0.6);
        Assert.True(2 == near.Stat(Stats.Armor), string.Join("; ", near.PassiveInstances.Select(x => $"{x.Def.Id} {x.Active} {x.AuraOwner} {x.Source}")) + " | g1: " +
            string.Join("; ", g1.PassiveInstances.Select(x => $"{x.Def.Id} {x.Active} {x.AuraOwner}")) + $" | rel {sim.RelationOf(g1, near)}");
        Assert.Equal(0, far.Stat(Stats.Armor), 6);
        Assert.Equal(0, enemy.Stat(Stats.Armor), 6);     // allies only
        Assert.Equal(2, g1.Stat(Stats.Armor), 6);        // the other guard's aura (not its own)
        var inst = near.PassiveInstances.Single(x => x.Def.Id == "guard" && x.AuraOwner >= 0);
        Assert.Equal(2, inst.AllSources.Count);
    }

    [Fact]
    public void TriggeredPassive_ActsForItsTime_LimitPerBattle()
    {
        var db = TestUtil.Minimal();
        var focus = Passive("focus", Stat(Stats.CritChance, 0.1, ModifierMode.FlatBefore));
        focus.Trigger = PassiveTrigger.OnCrit;
        focus.ActiveSeconds = 2;
        db.Add(focus);
        db.Add(new PassiveDef
        {
            Id = "last", Name = "Заслон", Trigger = PassiveTrigger.OnHpLost, HpLoss = 10, Action = PassiveAction.EffectsOnSelf, MaxTriggers = 1,
            Effects = { new SkillEffectDef { Type = EffectType.Shield, Amount = 5, RequiresHit = false, Filter = TargetFilterDef.Everyone() } },
        });
        var def = db.Unit("soldier")!;
        def.Passives.AddRange(new[] { "focus", "last" });
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(def, null, new HexCoord(2, 2), HexDirection.E);
        var e = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        Assert.Equal(0, u.Stat(Stats.CritChance), 6);
        sim.Reactions.Enqueue(PassiveTrigger.OnCrit, u, e);
        sim.Reactions.Process();
        Assert.Equal(0.1, u.Stat(Stats.CritChance), 6);
        sim.RunSeconds(2.5);
        Assert.Equal(0, u.Stat(Stats.CritChance), 6);
        Hit(sim, e, u, 15, "physical");
        sim.Reactions.Process();
        Hit(sim, e, u, 15, "physical");
        sim.Reactions.Process();
        Assert.Equal(1, u.PassiveFired["last"]);
    }

    [Fact]
    public void Immunity_HitPower_ExtraHits_Cost_TargetChoice_Terrain_Resources()
    {
        var db = TestUtil.Minimal();
        db.Add(new StatusDef { Id = "poison", Name = "Яд", Duration = 5, Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new SkillDef
        {
            Id = "combo", Name = "Серия", Targeting = TargetingMode.Unit, RangeMax = 1, ApCost = 4, HitMode = HitMode.AlwaysHit, CanCrit = false, Tags = { "combo" },
            Stages =
            {
                new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 10 } } },
                new SkillStageDef { Delay = 0.3, Tag = "finisher", Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 10 } } },
            },
        });
        db.Add(Passive("all_in",
            new ModifierDef { Target = ModifierTarget.Immunity, Status = "poison" },
            new ModifierDef { Target = ModifierTarget.HitPower, HitTag = "finisher", Value = 50 },
            new ModifierDef { Target = ModifierTarget.HitCount, SkillTag = "combo", Value = 1 },
            new ModifierDef { Target = ModifierTarget.SkillCost, SkillTag = "combo", Value = -50 },
            new ModifierDef { Target = ModifierTarget.TargetChoice, Rule = AutoTargetRule.LowestHp },
            new ModifierDef { Target = ModifierTarget.TerrainCost, Terrain = "mud", Value = -50 },
            new ModifierDef { Target = ModifierTarget.ResourceMax, Resource = "mana", Value = 20, Mode = ModifierMode.FlatBefore }));
        var def = db.Unit("soldier")!;
        def.Passives.Add("all_in");
        def.Skills.Add("combo");
        def.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 50, Start = 50 });
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(def, null, new HexCoord(2, 2), HexDirection.E);
        var t = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        Assert.False(sim.Statuses.Apply(u, db.Status("poison")!, 1, 0, t.Id));
        Assert.Equal(2, sim.Skills.ApCostOf(u, db.Skill("combo")!), 6);
        Assert.Equal(70, u.Resources["mana"].Max, 6);
        double ap = u.Ap;
        var chk = sim.Skills.Validate(u, db.Skill("combo")!, t.Pos, t.Id, null, u.Pos, u.Facing);
        var cast = sim.Skills.Begin(u, db.Skill("combo")!, chk, t.Id);
        Assert.Equal(ap - 2, u.Ap, 6);
        sim.Skills.Release(cast);
        sim.RunSeconds(1.5);
        Assert.Equal(3, cast.Series.Hits.Count);                     // 2 + 1 extra hit (repeats the last stage)
        Assert.Equal(new[] { 10.0, 15, 15 }, cast.Series.Hits.Select(h => h.Damage).ToArray());
        Assert.Equal(AutoTargetRule.LowestHp, sim.Passives.TargetRules(u)[0].Rule);
        var mud = sim.Map.Get(new HexCoord(4, 4))!;
        var mc = MoveContext.For(sim, u);
        double before = mud.Terrain.CostFor("walk");
        sim.Map.Get(new HexCoord(4, 4))!.Terrain = db.Terrain("mud")!;
        Assert.Equal(db.Terrain("mud")!.CostFor("walk") * 0.5, MovementRules.EntryCost(mc, null, mud), 6);
        Assert.True(before > 0);
    }

    [Fact]
    public void Sources_StatusTerrainSkillScenario_AreListed()
    {
        var db = TestUtil.Minimal();
        db.Add(Passive("from_status", Stat(Stats.Accuracy, 0.1, ModifierMode.FlatBefore)));
        db.Add(Passive("from_ground", Stat(Stats.Evasion, 0.1, ModifierMode.FlatBefore)));
        db.Add(Passive("from_skill", Stat(Stats.Power, 1, ModifierMode.FlatBefore)));
        db.Add(Passive("from_scenario", Stat(Stats.Armor, 1, ModifierMode.FlatBefore)));
        db.Add(new StatusDef { Id = "blessed", Name = "Благословение", Duration = 10, Levels = { new StatusLevelDef { Threshold = 1, Passives = { "from_status" } } } });
        db.Terrain("grass")!.Passives.Add("from_ground");
        db.Skill("hit")!.GrantsPassives.Add("from_skill");
        var map = TestUtil.Field();
        db.Add(map);
        var scen = new ScenarioDef { Id = "s", Name = "S", Map = map.Id, Passives = { new ScenarioPassiveDef { Passive = "from_scenario", Faction = "player" } } };
        db.Add(scen);
        var sim = ScenarioSystem.Create(db, scen, 1);
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(5, 2), HexDirection.W);
        sim.Statuses.Apply(u, db.Status("blessed")!, 1, 0, -1);
        var kinds = u.PassiveInstances.ToDictionary(x => x.Def.Id, x => x.SourceKind);
        Assert.Equal(PassiveSource.Status, kinds["from_status"]);
        Assert.Equal(PassiveSource.Terrain, kinds["from_ground"]);
        Assert.Equal(PassiveSource.Skill, kinds["from_skill"]);
        Assert.Equal(PassiveSource.Scenario, kinds["from_scenario"]);
        Assert.Equal(1, u.Stat(Stats.Armor), 6);
        Assert.DoesNotContain(enemy.PassiveInstances, x => x.Def.Id == "from_scenario");
    }

    [Fact]
    public void NewTriggers_SkillTag_EnemyEntered_Moved()
    {
        var db = TestUtil.Minimal();
        PassiveDef Heal(string id, PassiveTrigger tr)
        {
            var p = new PassiveDef { Id = id, Name = id, Trigger = tr, Action = PassiveAction.EffectsOnSelf, Effects = { new SkillEffectDef { Type = EffectType.ModifyAp, Amount = 1, RequiresHit = false, Filter = TargetFilterDef.Everyone() } } };
            db.Add(p);
            return p;
        }
        Heal("on_tagged", PassiveTrigger.OnSkillUsed).TriggerSkillTag = "fire";
        Heal("on_enter", PassiveTrigger.OnEnemyEnteredArea).TriggerRadius = 2;
        Heal("on_move", PassiveTrigger.OnMoved);
        var def = db.Unit("soldier")!;
        def.Passives.AddRange(new[] { "on_tagged", "on_enter", "on_move" });
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var u = sim.Spawn(def, null, new HexCoord(2, 2), HexDirection.E);
        var e = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        var far = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(8, 2), HexDirection.W);
        var chk = sim.Skills.Validate(u, db.Skill("hit")!, e.Pos, e.Id, null, u.Pos, u.Facing);
        sim.Skills.Release(sim.Skills.Begin(u, db.Skill("hit")!, chk, e.Id));
        sim.Reactions.Process();
        Assert.False(u.PassiveFired.ContainsKey("on_tagged"));     // «hit» has no «fire» tag
        sim.Relocate(far, new HexCoord(4, 2));
        sim.Reactions.Process();
        Assert.Equal(1, u.PassiveFired.GetValueOrDefault("on_enter"));
        sim.Relocate(u, new HexCoord(2, 3));
        sim.Reactions.Process();
        Assert.Equal(1, u.PassiveFired.GetValueOrDefault("on_move"));
    }

    [Fact]
    public void Validator_TriggeredModifiersWithoutTime_AndBadModifiers()
    {
        var db = TestUtil.Minimal();
        var p = Passive("bad", Stat("no_such_stat", 10), new ModifierDef { Target = ModifierTarget.Stat, Stat = Stats.Power, Mode = ModifierMode.Multiplier, Value = 0 });
        p.Trigger = PassiveTrigger.OnKill;
        db.Add(p);
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Source.EndsWith(":bad") && i.Message.Contains("модификаторы не действуют"));
        Assert.Contains(issues, i => i.Source.EndsWith(":bad") && i.Severity == IssueSeverity.Error && i.Message.Contains("no_such_stat"));
        Assert.Contains(issues, i => i.Source.EndsWith(":bad") && i.Severity == IssueSeverity.Error && i.Message.Contains("Множитель"));
    }

    [Fact]
    public void ShippedContent_Origins_Talents_Scenario()
    {
        var db = TestUtil.Shipped();
        Assert.Equal(OriginKind.Race, db.Get<OriginDef>("elf")!.Kind);
        Assert.Equal(PassiveKind.Talent, db.Get<PassiveDef>("nature_affinity")!.Kind);
        Assert.NotNull(db.Get<ScenarioDef>("slime_bog"));
        var issues = ContentValidator.Validate(db);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        var sim = ScenarioSystem.Create(db, db.Get<ScenarioDef>("slime_bog")!, 3);
        sim.RunSeconds(5);
        var slime = sim.Units.First(u => u.Def.Id == "slime");
        Assert.Contains(slime.PassiveInstances, x => x.Def.Id == "bog_mist" && x.SourceKind == PassiveSource.Scenario);
        Assert.Contains(slime.PassiveInstances, x => x.Def.Id == "toxic_body");
    }
}
