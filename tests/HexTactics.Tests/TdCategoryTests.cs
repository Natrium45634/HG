using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 4, stage 4: the category tree with inheritance modes, several categories with priorities and conflicts,
/// forbidden actions, tags and default AI of categories, new conditions, «за каждого», aura filters, stacking by
/// source and without duplicates, scenario variables, extra move types, narrow passages, mass (docs/DECISIONS_4.md §60–62).
/// </summary>
public class TdCategoryTests
{
    private static PassiveDef HpPassive(string id, double add) => new()
    {
        Id = id, Name = id, Modifiers = { new ModifierDef { Target = ModifierTarget.Stat, Stat = Stats.HpMax, Mode = ModifierMode.FlatBefore, Value = add } },
    };

    /// <summary>
    /// Сущность → Живые → Звери → Волк. «Сущность»: тег creature, «стойкость» +10; «Живые»: «регенерация» +3;
    /// «Звери»: переопределяет «стойкость» (+20), исключает «регенерацию»; «Волк»: дополняет «стойкость» (+5),
    /// условно (ночью) — «ночной охотник» +7 вместо «следопыта» +1 у зверей.
    /// </summary>
    private static ContentDatabase Tree()
    {
        var db = TestUtil.Minimal();
        db.Add(HpPassive("p_tough", 10));
        db.Add(HpPassive("p_regen", 3));
        db.Add(HpPassive("p_tough_beast", 20));
        db.Add(HpPassive("p_extra", 5));
        db.Add(HpPassive("p_tracker", 1));
        db.Add(HpPassive("p_night", 7));
        db.Add(new AiProfileDef { Id = "pack_ai", Name = "Стая" });
        db.Add(new OriginDef
        {
            Id = "entity", Name = "Сущность", Kind = OriginKind.Species,
            Rules =
            {
                new CategoryRuleDef { Id = "creature_tag", Kind = CategoryRuleKind.Tag, Tag = "creature" },
                new CategoryRuleDef { Id = "tough", Kind = CategoryRuleKind.Passive, Passive = "p_tough" },
            },
        });
        db.Add(new OriginDef { Id = "living", Name = "Живые", Parent = "entity", Rules = { new CategoryRuleDef { Id = "regen", Kind = CategoryRuleKind.Passive, Passive = "p_regen" } } });
        db.Add(new OriginDef
        {
            Id = "beasts", Name = "Звери", Parent = "living",
            Rules =
            {
                new CategoryRuleDef { Id = "tough", Kind = CategoryRuleKind.Passive, Passive = "p_tough_beast", Mode = CategoryInherit.Override },
                new CategoryRuleDef { Id = "regen", Mode = CategoryInherit.Exclude },
                new CategoryRuleDef { Id = "sense", Kind = CategoryRuleKind.Passive, Passive = "p_tracker" },
                new CategoryRuleDef { Id = "ai", Kind = CategoryRuleKind.AiProfile, AiProfile = "pack_ai" },
            },
        });
        db.Add(new OriginDef
        {
            Id = "wolf_cat", Name = "Волк", Parent = "beasts",
            Rules =
            {
                new CategoryRuleDef { Id = "tough", Kind = CategoryRuleKind.Passive, Passive = "p_extra", Mode = CategoryInherit.Add },
                new CategoryRuleDef
                {
                    Id = "sense", Kind = CategoryRuleKind.Passive, Passive = "p_night", Mode = CategoryInherit.Conditional,
                    Condition = new UnitConditionDef { Type = UnitConditionType.Environment, Variable = "time_of_day", VariableValue = "night" },
                },
                new CategoryRuleDef { Id = "pack_tag", Kind = CategoryRuleKind.Tag, Tag = "pack" },
            },
        });
        db.Add(new UnitDef
        {
            Id = "wolf_t", Name = "Волк", Faction = "enemy", BasicAttack = "hit", AiProfile = "", MainCategory = "wolf_cat",
            Stats = { [Stats.HpMax] = 100, [Stats.MoveSpeed] = 2 },
        });
        return db;
    }

    [Fact]
    public void Tree_InheritOverrideExcludeAddConditional_ResolveWithSources()
    {
        var db = Tree();
        var res = CategoryTree.Resolve(db, db.Unit("wolf_t")!);
        Assert.Equal(new[] { "entity", "living", "beasts", "wolf_cat" }, res.All.Select(c => c.Id));
        string Of(string passive) => res.Rules.Single(r => r.Rule.Passive == passive).ToString();
        Assert.Contains(res.Acting, r => r.Rule.Passive == "p_tough_beast" && r.From.Id == "beasts");
        Assert.Contains(res.Acting, r => r.Rule.Passive == "p_extra" && r.From.Id == "wolf_cat");
        Assert.Contains("переопределено в «Звери»", Of("p_tough"));
        Assert.Contains("исключено в «Звери»", Of("p_regen"));
        Assert.Contains("условно заменено в «Волк»", Of("p_tracker"));
        var cond = res.Acting.Single(r => r.Rule.Passive == "p_night");
        Assert.Equal("p_tracker", cond.Otherwise!.Passive);
        Assert.Contains("creature", res.Tags);
        Assert.Contains("pack", res.Tags);
        Assert.Equal("pack_ai", res.AiProfile);
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Source.Contains("Origin"));

        // In battle: the passives that act, the tags and the category membership; the template is not changed.
        var map = TestUtil.Field(8, 6);
        var sim = TestUtil.Sim(db, map);
        var u = sim.Spawn(db.Unit("wolf_t")!, "enemy", new HexCoord(2, 2), HexDirection.E);
        Assert.Equal(100 + 20 + 5 + 1, u.HpMax, 3); // day: the beasts' tracker
        Assert.True(u.HasTag("creature") && u.HasTag("pack"));
        Assert.True(u.InCategory("living") && u.InCategory("wolf_cat"));
        Assert.Equal("pack_ai", u.AiProfile!.Id);
        Assert.Equal(100, db.Unit("wolf_t")!.Stats[Stats.HpMax]);
        Assert.Contains(u.Stats.Explain(Stats.HpMax).Items, i => i.C.Source.Contains("«Звери»"));
        // Night: the conditional rule acts instead of the parent's.
        var sc = new ScenarioDef { Id = "night", Map = map.Id, Environment = { new EnvironmentVarDef { Name = "time_of_day", Value = "day" } } };
        db.Add(map);
        db.Add(sc);
        var sim2 = ScenarioSystem.Create(db, sc, 1);
        var w = sim2.Spawn(db.Unit("wolf_t")!, "enemy", new HexCoord(2, 2), HexDirection.E);
        Assert.Equal(126, w.HpMax, 3);
        sim2.Scenario.SetVariable("time_of_day", "night");
        sim2.RunSeconds(1);
        Assert.Equal(100 + 20 + 5 + 7, w.HpMax, 3);
    }

    [Fact]
    public void SeveralCategories_PriorityDepthOrder_AndConflictWarnings()
    {
        var db = TestUtil.Minimal();
        db.Add(new OriginDef { Id = "slimes", Name = "Слизи", Rules = { new CategoryRuleDef { Id = "no_attack", Kind = CategoryRuleKind.Forbid, What = ForbidWhat.BasicAttack } } });
        db.Add(new OriginDef { Id = "trained", Name = "Обученные", Rules = { new CategoryRuleDef { Id = "attack_ok", Kind = CategoryRuleKind.Allow, What = ForbidWhat.BasicAttack } } });
        var u = new UnitDef { Id = "mix", Name = "Смесь", Faction = "enemy", BasicAttack = "hit", MainCategory = "slimes", Origins = { "trained" } };
        db.Add(u);
        var res = CategoryTree.Resolve(db, u);
        // Equal priority: a warning; the order on the template decides (the main category first).
        Assert.Contains(res.Problems, p => p.Contains("противоречие") && p.Contains("одинаковым приоритетом"));
        Assert.Single(res.Forbids);
        Assert.Contains(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Warning && i.Message.Contains("противоречие"));
        // A higher priority wins without a warning.
        db.Get<OriginDef>("trained")!.Rules[0].Priority = 1;
        res = CategoryTree.Resolve(db, u);
        Assert.Empty(res.Forbids);
        Assert.DoesNotContain(res.Problems, p => p.Contains("противоречие"));
        // A deeper category is stronger than a root one with the same key.
        db.Add(new OriginDef { Id = "slime_child", Name = "Слизь-потомок", Parent = "trained", Rules = { new CategoryRuleDef { Id = "attack_ok", Kind = CategoryRuleKind.Forbid, What = ForbidWhat.BasicAttack, Mode = CategoryInherit.Override, Priority = 1 } } });
        u.Origins = new() { "slime_child" };
        res = CategoryTree.Resolve(db, u);
        Assert.Contains(res.Forbids, r => r.From.Id == "slime_child");
        // «Inherit» with the parent's ID and «exclude» of nothing are reported.
        db.Add(new OriginDef { Id = "odd", Name = "Странные", Parent = "slimes", Rules = { new CategoryRuleDef { Id = "no_attack", Kind = CategoryRuleKind.Forbid, What = ForbidWhat.BasicAttack }, new CategoryRuleDef { Id = "ghost", Mode = CategoryInherit.Exclude } } });
        var odd = new UnitDef { Id = "odd_u", Name = "Странный", MainCategory = "odd" };
        db.Add(odd);
        var r2 = CategoryTree.Resolve(db, odd);
        Assert.Contains(r2.Problems, p => p.Contains("«наследовать» совпадает"));
        Assert.Contains(r2.Problems, p => p.Contains("исключает то, чего нет"));
        // Cycles and missing parents are errors of the category.
        db.Get<OriginDef>("slimes")!.Parent = "odd";
        db.Add(new OriginDef { Id = "orphan", Name = "Сирота", Parent = "nobody" });
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("цикл"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("«nobody» не найден"));
    }

    [Fact]
    public void Forbids_TakeAwayActions_WithReasons()
    {
        var db = TestUtil.Minimal();
        db.Add(new SkillDef { Id = "bite", Name = "Укус", Tags = { "melee" }, Targeting = TargetingMode.Unit, ApCost = 1, Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, Amount = 5 } } } } });
        db.Add(new SkillDef { Id = "spit", Name = "Плевок", Targeting = TargetingMode.Unit, ApCost = 1, RangeMax = 3, Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, Amount = 5 } } } } });
        db.Add(new OriginDef
        {
            Id = "slimes", Name = "Слизи",
            Rules =
            {
                new CategoryRuleDef { Id = "no_attack", Kind = CategoryRuleKind.Forbid, What = ForbidWhat.BasicAttack },
                new CategoryRuleDef { Id = "no_melee", Kind = CategoryRuleKind.Forbid, What = ForbidWhat.SkillsWithTag, SkillTag = "melee" },
            },
        });
        db.Add(new OriginDef { Id = "rooted", Name = "Корни", Rules = { new CategoryRuleDef { Id = "no_move", Kind = CategoryRuleKind.Forbid, What = ForbidWhat.Movement } } });
        db.Add(new UnitDef { Id = "mimic", Name = "Мимик", Faction = "enemy", BasicAttack = "hit", Skills = { "bite", "spit" }, MainCategory = "slimes", Origins = { "rooted" }, Stats = { [Stats.MoveSpeed] = 2 } });
        var sim = TestUtil.Sim(db, TestUtil.Field(8, 6));
        var m = sim.Spawn(db.Unit("mimic")!, "enemy", new HexCoord(1, 1), HexDirection.E);
        var foe = sim.Spawn(db.Unit("soldier")!, "player", new HexCoord(2, 1), HexDirection.W);
        Assert.Null(m.BasicAttack);
        var noAttack = sim.Enqueue(m, Command.Attack(foe.Id)) ?? "";
        Assert.True(noAttack.Contains("запрещено: стандартная атака (категория «Слизи»)"), noAttack);
        Assert.DoesNotContain(m.Skills, s => s.Id == "bite");
        Assert.Contains(m.Skills, s => s.Id == "spit");
        Assert.Contains("навыки с тегом «melee»", sim.Enqueue(m, Command.Skill("bite", null, foe.Id)) ?? "");
        Assert.Equal(0, m.Stat(Stats.MoveSpeed), 3);
        Assert.Contains("запрещено: движение (категория «Корни»)", sim.Enqueue(m, Command.Move(new HexCoord(4, 4))) ?? "");
        Assert.Null(sim.Enqueue(m, Command.Skill("spit", null, foe.Id)));
    }

    [Fact]
    public void PerEach_CountsPackMembers_WithCap_AndAuraFiltersByTag()
    {
        var db = TestUtil.Minimal();
        db.Add(new PassiveDef
        {
            Id = "pack_leader", Name = "Вожак",
            Modifiers = { new ModifierDef { Target = ModifierTarget.Stat, Stat = Stats.Power, Mode = ModifierMode.FlatBefore, Value = 1, PerEach = new PerEachDef { Types = { "tag:pack" }, Radius = 3, MaxValue = 3 } } },
        });
        db.Add(new PassiveDef
        {
            Id = "pack_aura", Name = "Аура стаи", AuraRadius = 3, AuraTypes = { "tag:pack" }, AuraAffects = AuraAffects.Allies,
            Modifiers = { new ModifierDef { Target = ModifierTarget.Stat, Stat = Stats.Armor, Mode = ModifierMode.FlatBefore, Value = 2 } },
        });
        db.Add(new UnitDef { Id = "leader", Name = "Вожак", Faction = "enemy", Passives = { "pack_leader", "pack_aura" }, Tags = { "pack" } });
        db.Add(new UnitDef { Id = "pup", Name = "Волчонок", Faction = "enemy", Tags = { "pack" } });
        db.Add(new UnitDef { Id = "crow", Name = "Ворона", Faction = "enemy" });
        var sim = TestUtil.Sim(db, TestUtil.Field(12, 8));
        var l = sim.Spawn(db.Unit("leader")!, "enemy", new HexCoord(4, 3), HexDirection.E);
        var p1 = sim.Spawn(db.Unit("pup")!, "enemy", new HexCoord(5, 3), HexDirection.E);
        var p2 = sim.Spawn(db.Unit("pup")!, "enemy", new HexCoord(4, 4), HexDirection.E);
        var crow = sim.Spawn(db.Unit("crow")!, "enemy", new HexCoord(3, 3), HexDirection.E);
        sim.RunSeconds(0.6);
        double basePower = db.Stat(Stats.Power)!.Default;
        Assert.Equal(basePower + 2, l.Stat(Stats.Power), 3); // two pups in radius
        sim.Spawn(db.Unit("pup")!, "enemy", new HexCoord(5, 2), HexDirection.E);
        sim.Spawn(db.Unit("pup")!, "enemy", new HexCoord(3, 4), HexDirection.E);
        sim.RunSeconds(0.6);
        Assert.Equal(basePower + 3, l.Stat(Stats.Power), 3); // four pups, capped at +3
        // A pup far away is not counted; the dead are counted only when allowed.
        sim.Combat.Kill(p1, null, "тест");
        sim.Combat.Kill(p2, null, "тест");
        sim.RunSeconds(0.6);
        Assert.Equal(basePower + 2, l.Stat(Stats.Power), 3);
        // The aura gives armour to the pack only.
        double baseArmor = db.Stat(Stats.Armor)!.Default;
        var pupAlive = sim.Units.First(u => u.Alive && u.Def.Id == "pup");
        Assert.Equal(baseArmor + 2, pupAlive.Stat(Stats.Armor), 3);
        Assert.Equal(baseArmor, crow.Stat(Stats.Armor), 3);
    }

    [Fact]
    public void Stacking_BySource_AndNoDuplicates()
    {
        var db = TestUtil.Minimal();
        db.Add(new PassiveDef
        {
            Id = "war_cry", Name = "Боевой клич", AuraRadius = 4, AuraAffects = AuraAffects.Allies, Stacking = PassiveStacking.BySource,
            Modifiers = { new ModifierDef { Target = ModifierTarget.Stat, Stat = Stats.Armor, Mode = ModifierMode.FlatBefore, Value = 1 } },
        });
        db.Add(new PassiveDef
        {
            Id = "banner", Name = "Знамя", AuraRadius = 4, AuraAffects = AuraAffects.Allies, Stacking = PassiveStacking.NoDuplicates,
            Modifiers = { new ModifierDef { Target = ModifierTarget.Stat, Stat = Stats.Evasion, Mode = ModifierMode.FlatBefore, Value = 0.1 } },
        });
        db.Add(new UnitDef { Id = "captain", Name = "Капитан", Faction = "player", Passives = { "war_cry", "banner" } });
        var sim = TestUtil.Sim(db, TestUtil.Field(10, 6));
        sim.Spawn(db.Unit("captain")!, "player", new HexCoord(2, 2), HexDirection.E);
        sim.Spawn(db.Unit("captain")!, "player", new HexCoord(4, 2), HexDirection.E);
        var s = sim.Spawn(db.Unit("soldier")!, "player", new HexCoord(3, 2), HexDirection.E);
        sim.RunSeconds(0.6);
        Assert.Equal(2, s.PassiveInstances.Count(pi => pi.Def.Id == "war_cry"));
        Assert.Equal(db.Stat(Stats.Armor)!.Default + 2, s.Stat(Stats.Armor), 3);
        var banner = s.PassiveInstances.Single(pi => pi.Def.Id == "banner");
        Assert.Single(banner.Duplicates);
        Assert.Equal(db.Stat(Stats.Evasion)!.Default + 0.1, s.Stat(Stats.Evasion), 3);
    }

    [Fact]
    public void NewConditions_Layer_Distance_Category_Environment()
    {
        var db = Tree();
        db.Add(new ScenarioDef { Id = "env", Map = "field", Environment = { new EnvironmentVarDef { Name = "weather", Value = "rain" } },
            Triggers = { new TriggerDef { Name = "Снег", Once = true, Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 1 },
                Actions = { new TriggerActionDef { Type = TriggerActionType.SetEnvironment, Variable = "weather", VariableValue = "snow" } } } } });
        var map = TestUtil.Field(10, 6);
        db.Add(map);
        var sim = ScenarioSystem.Create(db, db.Get<ScenarioDef>("env")!, 1);
        var w = sim.Spawn(db.Unit("wolf_t")!, "enemy", new HexCoord(2, 2), HexDirection.E);
        var s = sim.Spawn(db.Unit("soldier")!, "player", new HexCoord(5, 2), HexDirection.W);
        bool E(UnitConditionDef c) => sim.Conditions.Evaluate(c, new ConditionContext(w, s, w.Pos, null));
        Assert.True(E(new UnitConditionDef { Type = UnitConditionType.Category, Subject = ConditionSubject.Self, Categories = { "living" } }));
        Assert.True(E(new UnitConditionDef { Type = UnitConditionType.Category, Subject = ConditionSubject.Self, Categories = { "tag:pack" } }));
        Assert.False(E(new UnitConditionDef { Type = UnitConditionType.Category, Subject = ConditionSubject.Target, Categories = { "living" } }));
        Assert.True(E(new UnitConditionDef { Type = UnitConditionType.Layer, Subject = ConditionSubject.Self, Layer = w.Layer }));
        Assert.True(E(new UnitConditionDef { Type = UnitConditionType.Distance, Compare = CompareMode.AtLeast, Radius = 3 }));
        Assert.False(E(new UnitConditionDef { Type = UnitConditionType.Distance, Compare = CompareMode.AtMost, Radius = 2 }));
        var snow = new UnitConditionDef { Type = UnitConditionType.Environment, Variable = "weather", VariableValue = "snow" };
        Assert.False(E(snow));
        Assert.Equal("rain", sim.Scenario.Variable("weather"));
        sim.RunSeconds(1.5);
        Assert.True(E(snow));
        Assert.True(sim.Scenario.Evaluate(new ConditionDef { Type = ConditionType.Environment, Variable = "weather", VariableValue = "snow" }));
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("weather: snow"));
    }

    [Fact]
    public void MoveTypes_FromCategories_AndConditional_NarrowPassages_AndMass()
    {
        var db = TestUtil.Minimal();
        db.Add(new TerrainDef { Id = "deep", Name = "Глубина", MoveCost = 1, PassableFor = new() { "swim" } });
        db.Add(new OriginDef { Id = "amphibians", Name = "Земноводные", Rules = { new CategoryRuleDef { Id = "swim", Kind = CategoryRuleKind.MoveCategory, MoveCategory = "swim" } } });
        db.Add(new StatusDef { Id = "wet", Name = "Мокрый", Duration = 30, Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new UnitDef { Id = "frog", Name = "Лягушка", Faction = "player", MainCategory = "amphibians", Stats = { [Stats.MoveSpeed] = 3 } });
        db.Add(new UnitDef
        {
            Id = "diver", Name = "Ныряльщик", Faction = "player", Stats = { [Stats.MoveSpeed] = 3 },
            MoveTypes = { new MoveTypeDef { Category = "swim", Condition = new UnitConditionDef { Type = UnitConditionType.HasStatus, Subject = ConditionSubject.Self, Status = "wet" } } },
        });
        db.Add(new UnitDef { Id = "giant", Name = "Великан", Faction = "player", Stats = { [Stats.MoveSpeed] = 3, [Stats.Size] = 2, [Stats.Weight] = 300 } });
        var map = TestUtil.Field(10, 6);
        TestUtil.SetCell(map, new HexCoord(3, 2), "deep");
        var sim = TestUtil.Sim(db, map);
        var frog = sim.Spawn(db.Unit("frog")!, "player", new HexCoord(2, 2), HexDirection.E);
        var diver = sim.Spawn(db.Unit("diver")!, "player", new HexCoord(2, 3), HexDirection.E);
        var deep = sim.Map.Get(new HexCoord(3, 2))!;
        Assert.Equal(StepCheck.Ok, MovementRules.Check(MoveContext.For(sim, frog), sim.Map.Get(frog.Pos), deep, true, out _));
        Assert.Equal(StepCheck.Impassable, MovementRules.Check(MoveContext.For(sim, diver), sim.Map.Get(diver.Pos), deep, true, out _));
        sim.Statuses.Apply(diver, db.Status("wet")!, 1, 30, -1, force: true);
        Assert.Equal(StepCheck.Ok, MovementRules.Check(MoveContext.For(sim, diver), sim.Map.Get(diver.Pos), deep, true, out _));

        // A narrow passage: walls on both sides of (6,2) along one axis.
        TestUtil.SetCell(map, new HexCoord(6, 1), "wall");
        TestUtil.SetCell(map, new HexCoord(6, 3), "wall");
        var sim2 = TestUtil.Sim(db, map);
        var giant = sim2.Spawn(db.Unit("giant")!, "player", new HexCoord(5, 2), HexDirection.E);
        var small = sim2.Spawn(db.Unit("soldier")!, "player", new HexCoord(7, 2), HexDirection.W);
        var gap = sim2.Map.Get(new HexCoord(6, 2))!;
        Assert.True(MovementRules.IsNarrow(MoveContext.For(sim2, giant), gap));
        Assert.Equal(StepCheck.Impassable, MovementRules.Check(MoveContext.For(sim2, giant), sim2.Map.Get(giant.Pos), gap, true, out _));
        Assert.Equal(StepCheck.Ok, MovementRules.Check(MoveContext.For(sim2, small), sim2.Map.Get(small.Pos), gap, true, out _));

        // Mass: a push of 3 moves the giant (300 against 100) by 1 hex, a normal one by 3.
        var sim3 = TestUtil.Sim(db, TestUtil.Field(12, 6));
        var g = sim3.Spawn(db.Unit("giant")!, "player", new HexCoord(3, 2), HexDirection.E);
        var n = sim3.Spawn(db.Unit("soldier")!, "player", new HexCoord(3, 4), HexDirection.E);
        sim3.Combat.Displace(g, new HexCoord(2, 2), true, 3, null, "тест");
        sim3.Combat.Displace(n, new HexCoord(2, 4), true, 3, null, "тест");
        Assert.Equal(new HexCoord(4, 2), g.Pos);
        Assert.Equal(new HexCoord(6, 4), n.Pos);
    }

    [Fact]
    public void Budget_FiltersBy_TreeCategories_AndTheirTags()
    {
        var db = Tree();
        db.Add(new UnitDef { Id = "golem", Name = "Голем", Faction = "enemy", Stats = { [Stats.HpMax] = 100, [Stats.MoveSpeed] = 1 } });
        var w = new WaveDef { Faction = "enemy" };
        var byCat = new WaveBudgetDef { Enabled = true, Budget = 100, MinUnits = 1, MaxUnits = 5, Categories = { "beasts" } };
        Assert.All(WaveBudget.Candidates(db, w, byCat), u => Assert.Equal("wolf_t", u.Id));
        var byTag = new WaveBudgetDef { Enabled = true, Budget = 100, MinUnits = 1, MaxUnits = 5, RequiredTags = { "pack" } };
        Assert.Equal(new[] { "wolf_t" }, WaveBudget.Candidates(db, w, byTag).Select(u => u.Id));
    }
}
