using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 4, stage 6: the power model with its table of contributions, the scale of level, the rank, passives by their
/// share, synergies by context, «Подогнать под уровень» with its variants and limits, the level of an instance, the
/// threat cost and its context, the warnings about too strong templates (docs/DECISIONS_4.md §65–§67, §70).
/// </summary>
public class PowerTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new UnitDef
        {
            Id = "ref", Name = "Эталон", Faction = "enemy", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 2 },
        });
        db.Rules.Power.ReferenceUnit = "ref";
        return db;
    }

    private static UnitDef Clone(ContentDatabase db, string id, string newId, Action<UnitDef> f)
    {
        var u = ContentJson.Clone(db.Unit(id)!);
        u.Id = newId;
        f(u);
        db.Add(u);
        return u;
    }

    [Fact]
    public void Reference_IsLevelOne_StrongerTemplatesHigher_AndTheTableExplainsTheTotal()
    {
        var db = Db();
        var r0 = PowerModel.Evaluate(db, db.Unit("ref")!);
        Assert.Equal(1, r0.Power, 6);
        Assert.Equal(1, r0.Level, 6);
        Assert.Equal(1, r0.H, 6);
        Assert.Equal(1, r0.D, 6);
        var big = Clone(db, "ref", "big", u =>
        {
            u.Stats[Stats.HpMax] = 400;
            u.Stats[Stats.Power] = 0;
        });
        db.Skill("hit")!.Stages[0].Effects[0].ScalingStat = Stats.Power;
        db.Skill("hit")!.Stages[0].Effects[0].ScalingFactor = 1;
        big.Stats[Stats.Power] = 30; // 10 + 30 = 40 per hit: ×4 damage
        var r = PowerModel.Evaluate(db, big);
        Assert.Equal(4, r.H, 2);
        Assert.Equal(4, r.D, 2);
        Assert.True(r.Level > 6 && r.Level < 12, $"×4 здоровья и урона — около уровня 10, получено {r.Level:0.00}");
        // The total is the sum of the rows; the display level is the rounded exact one.
        Assert.Equal(r.Power, r.Lines.Where(l => l.Key != "total").Sum(l => l.Value), 6);
        Assert.Equal((int)Math.Round(r.Level, MidpointRounding.AwayFromZero), r.DisplayLevel);
        Assert.Contains("(", r.LevelText);
        Assert.Contains(r.Lines, l => l.Key == "health" && l.Detail.Contains("эфф. здоровье"));
    }

    [Fact]
    public void EffectiveHealth_ByDamageType_FollowsResistances()
    {
        var db = Db();
        var u = Clone(db, "ref", "res", d =>
        {
            d.Defense.Modifiers.Add(new DamageModDef { Match = "fire", Percent = -50 });
            d.Defense.Modifiers.Add(new DamageModDef { Match = "ice", Percent = 50 });
        });
        var r = PowerModel.Evaluate(db, u);
        double E(string t) => r.EhpProfile.Single(e => e.Type == t).Ehp;
        Assert.Equal(2, E("fire") / E("physical"), 3);
        Assert.Equal(1 / 1.5, E("ice") / E("physical"), 3);
    }

    [Fact]
    public void ThreeHitsOfHalf_AreOneAndAHalfHits_NotOneHalf()
    {
        var db = Db();
        db.Add(new SkillDef
        {
            Id = "single", Name = "Один", Targeting = TargetingMode.Unit, ApCost = 2, PrepTime = 0.3, RecoveryTime = 0.2,
            Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 20 } } } },
        });
        var triple = new SkillDef { Id = "triple", Name = "Три по половине", Targeting = TargetingMode.Unit, ApCost = 2, PrepTime = 0.3, RecoveryTime = 0.2 };
        for (int i = 0; i < 3; i++)
            triple.Stages.Add(new SkillStageDef { DamageMultiplier = 0.5, Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 20 } } });
        db.Add(triple);
        var a = Clone(db, "ref", "a", u => u.BasicAttack = "single");
        var b = Clone(db, "ref", "b", u => u.BasicAttack = "triple");
        Assert.Equal(1.5, PowerModel.Evaluate(db, b).Dps / PowerModel.Evaluate(db, a).Dps, 2);
    }

    [Fact]
    public void Passives_CountByTheirShare_TagsAndCategoriesOnlyThroughRules()
    {
        var db = Db();
        db.Add(new PassiveDef { Id = "rage", Name = "Ярость", Modifiers = { new ModifierDef { Target = ModifierTarget.OutgoingDamage, Match = "*", Value = 50 } } });
        db.Add(new PassiveDef
        {
            Id = "despair", Name = "Последний рывок", When = new UnitConditionDef { Type = UnitConditionType.HpFraction, Subject = ConditionSubject.Self, Compare = CompareMode.AtMost, Value = 0.1 },
            Modifiers = { new ModifierDef { Target = ModifierTarget.OutgoingDamage, Match = "*", Value = 50 } },
        });
        var always = Clone(db, "ref", "always", u => u.Passives.Add("rage"));
        var low = Clone(db, "ref", "low", u => u.Passives.Add("despair"));
        double Line(UnitDef u) => PowerModel.Evaluate(db, u).Lines.Single(l => l.Key == "passives").Value;
        Assert.True(Line(always) > 0);
        Assert.Equal(0.1, Line(low) / Line(always), 1);
        var rl = PowerModel.Evaluate(db, low);
        Assert.Contains(rl.Passives, p => p.Id == "despair" && Math.Abs(p.Share - 0.1) < 1e-9);

        // Descriptive tags and a category without rules add nothing; a rule «вклад в силу» does.
        db.Add(new OriginDef { Id = "lonely", Name = "Без правил" });
        var tagged = Clone(db, "ref", "tagged", u =>
        {
            u.Tags.AddRange(new[] { "leader", "legend", "ancient", "elite_looking" });
            u.MainCategory = "lonely";
        });
        Assert.Equal(1, PowerModel.Evaluate(db, tagged).Power, 6);
        db.Add(new OriginDef { Id = "blessed", Name = "Благословлённые", Rules = { new CategoryRuleDef { Id = "pb", Kind = CategoryRuleKind.PowerBonus, Power = 20 } } });
        var blessed = Clone(db, "ref", "blessed_u", u => u.MainCategory = "blessed");
        var rb = PowerModel.Evaluate(db, blessed);
        Assert.Equal(1.2, rb.Power, 6);
        Assert.Contains(rb.Lines, l => l.Key == "manual");
    }

    [Fact]
    public void Synergy_DependsOnTheContext_SoloAssumesNoAllies()
    {
        var db = Db();
        db.Add(new PassiveDef
        {
            Id = "howl", Name = "Вой", AuraRadius = 3, AuraAffects = AuraAffects.Allies, AuraTypes = { "tag:pack" },
            Modifiers = { new ModifierDef { Target = ModifierTarget.OutgoingDamage, Match = "*", Value = 15 } },
        });
        var leader = Clone(db, "ref", "leader", u =>
        {
            u.Passives.Add("howl");
            u.Tags.Add("pack");
        });
        var pack = Clone(db, "ref", "packwolf", u => u.Tags.Add("pack"));
        var lone = Clone(db, "ref", "lonewolf", _ => { });
        Assert.Equal(0, PowerModel.Evaluate(db, leader).S, 6);
        var typical = PowerModel.Evaluate(db, leader, PowerContext.Typical(db));
        Assert.True(typical.S > 0 && typical.Power > PowerModel.Evaluate(db, leader).Power);
        var withPack = PowerModel.Evaluate(db, leader, new PowerContext { Kind = PowerContextKind.ScenarioGroup, Allies = 3, AllyDefs = { pack, pack, pack } });
        var withOthers = PowerModel.Evaluate(db, leader, new PowerContext { Kind = PowerContextKind.ScenarioGroup, Allies = 3, AllyDefs = { lone, lone, lone } });
        Assert.True(withPack.S > 0);
        Assert.Equal(0, withOthers.S, 6);
    }

    [Fact]
    public void ScenarioGroup_TakesTheWaveOrTheUnitsOfTheSide()
    {
        var db = TestUtil.Shipped();
        // A unit of the scenario: its allies are the units of its side (three goblins of the enemy in the skirmish).
        var skirmish = PowerContext.InScenario(db, db.Unit("wolf_leader")!, db.Get<ScenarioDef>("free_skirmish")!);
        Assert.Equal(new[] { "goblin", "goblin", "goblin_archer" }, skirmish.AllyDefs.Select(u => u.Id).OrderBy(x => x));
        // A group of a wave: the rest of that wave.
        var sc = db.All<ScenarioDef>().First(s => s.Waves.Any(w => w.Groups.Count > 1));
        var wave = sc.Waves.First(w => w.Groups.Count > 1);
        var unit = db.Unit(wave.Groups[0].Unit)!;
        var ctx = PowerContext.InScenario(db, unit, sc);
        Assert.Equal(PowerContextKind.ScenarioGroup, ctx.Kind);
        Assert.True(ctx.Allies > 0 && ctx.AllyDefs.Count == ctx.Allies);
    }

    [Fact]
    public void RankDoesNotChangeTheLevel_TheBossScaleIsSeparate_AndScalesInvert()
    {
        var db = Db();
        var elite = Clone(db, "ref", "elite", u => u.Rank = UnitRank.Elite);
        Assert.Equal(1, PowerModel.Evaluate(db, elite).Level, 6);
        db.Rules.Power.BossScale = new LevelScaleDef { Kind = LevelScaleKind.Linear, Step = 1 };
        var boss = Clone(db, "ref", "boss", u =>
        {
            u.Rank = UnitRank.Boss;
            u.Stats[Stats.HpMax] = 300;
        });
        var rb = PowerModel.Evaluate(db, boss);
        Assert.Equal(PowerRole.Boss, rb.Role);
        Assert.Equal(1 + (rb.Power - 1) / 1, rb.Level, 6);
        foreach (var s in new[] { new LevelScaleDef(), new LevelScaleDef { Kind = LevelScaleKind.Linear, Step = 0.3 }, new LevelScaleDef { Kind = LevelScaleKind.Power, Exponent = 0.8 } })
            foreach (var l in new[] { 1.0, 7.5, 10, 30, 47, 80 })
                Assert.Equal(l, s.LevelOf(s.PowerOf(l)), 6);
        // The default table: level 10 ≈ ×4 power, 30 ≈ ×12, 50 ≈ ×20.
        var t = new LevelScaleDef();
        Assert.Equal(10, t.LevelOf(4), 6);
        Assert.Equal(30, t.LevelOf(12), 6);
        Assert.Equal(50, t.LevelOf(20), 6);
    }

    [Fact]
    public void ChangingTheReference_ChangesEveryLevel()
    {
        var db = Db();
        var strong = Clone(db, "ref", "strong", u => u.Stats[Stats.HpMax] = 300);
        double before = PowerModel.Evaluate(db, strong).Level;
        db.Rules.Power.ReferenceUnit = "strong";
        Assert.Equal(1, PowerModel.Evaluate(db, strong).Level, 6);
        Assert.True(PowerModel.Evaluate(db, db.Unit("ref")!).Level < 1);
        Assert.True(before > 1);
    }

    [Fact]
    public void LevelFit_ReachesTheLevel_KeepsTheSource_ProportionsAndRole_OrSaysWhyNot()
    {
        var db = TestUtil.Shipped();
        var src = db.Unit("wolf_leader")!;
        string before = ContentJson.Serialize(src);
        var variants = LevelFit.Variants(db, src, 10);
        Assert.Equal(3, variants.Count);
        var stats = variants.Single(v => v.Variant == FitVariant.Stats);
        var combined = variants.Single(v => v.Variant == FitVariant.Combined);
        Assert.True(stats.Reached, stats.ToString());
        Assert.True(combined.Reached, combined.ToString());
        Assert.InRange(combined.LevelAfter, 9.5, 10.5);
        Assert.Equal(before, ContentJson.Serialize(src));
        Assert.NotSame(src, combined.Unit);
        Assert.Contains(combined.Changes, c => c.Key == "stat:" + Stats.HpMax && c.To > c.From);
        Assert.Contains(combined.Changes, c => c.Key == nameof(ScaleTarget.SkillDamage) && c.To > c.From);

        // An assassin stays an assassin: damage grows faster than health, the role is pinned.
        var scout = db.Unit("wolf_scout")!;
        var a = LevelFit.Fit(db, scout, 8, db.Get<ScalingProfileDef>("scale_assassin"), FitVariant.Combined);
        Assert.Equal(PowerRole.Assassin, a.Unit.PowerRole);
        var hp = a.Changes.Single(c => c.Key == "stat:" + Stats.HpMax);
        var dmg = a.Changes.Single(c => c.Key == nameof(ScaleTarget.SkillDamage));
        Assert.True((1 + dmg.To / 100) / (1 + dmg.From / 100) > hp.To / hp.From, $"урон ×{(1 + dmg.To / 100):0.##} должен расти быстрее здоровья ×{hp.To / hp.From:0.##}");

        // The limits of the profile stop it: the report says the nearest level and what to allow.
        var custom = db.Get<ScalingProfileDef>("scale_custom")!;
        var far = LevelFit.Fit(db, scout, 30, custom, FitVariant.Stats);
        Assert.False(far.Reached);
        Assert.Contains("недостижим", far.Message);
        Assert.Contains("Максимальная оценка", far.Message);
        Assert.Contains("Макс. здоровье", far.Message);
        Assert.True(far.Unit.Stats[Stats.HpMax] <= 1000);
    }

    [Fact]
    public void InstanceLevel_FitsTheSpawnedUnit_NotTheTemplate()
    {
        var db = TestUtil.Shipped();
        var sc = ContentJson.Clone(db.Get<ScenarioDef>("td_pass")!);
        var g = sc.Waves[0].Groups[0];
        g.Level = 8;
        string tpl = ContentJson.Serialize(db.Unit(g.Unit)!);
        var sim = ScenarioSystem.Create(db, sc, 1);
        TestUtil.RunUntil(sim, () => sim.Units.Any(u => u.Group == g.Id), 20);
        var u = sim.Units.First(x => x.Group == g.Id);
        Assert.Equal(8, u.InstanceLevel);
        Assert.True(u.HpMax > db.Unit(g.Unit)!.Stats[Stats.HpMax] * 1.5, $"экземпляр 8 уровня должен быть крепче шаблона: {u.HpMax}");
        Assert.Equal(tpl, ContentJson.Serialize(db.Unit(g.Unit)!));
        var fitted = ContentJson.Clone(db.Unit(g.Unit)!);
        fitted.PowerRole = PowerModel.Evaluate(db, db.Unit(g.Unit)!).Role;
        foreach (var (stat, m) in LevelFit.InstanceMultipliers(db, db.Unit(g.Unit)!, 8)) fitted.Stats[stat] *= m;
        Assert.InRange(PowerModel.Evaluate(db, fitted).Level, 7, 9);

        // A range of levels: by the seed, the same for the same seed.
        g.Level = 2;
        g.LevelMax = 9;
        int[] Levels(ulong seed)
        {
            var s = ScenarioSystem.Create(db, sc, seed);
            TestUtil.RunUntil(s, () => s.Units.Count(x => x.Group == g.Id) == g.Count, 30);
            return s.Units.Where(x => x.Group == g.Id).Select(x => x.InstanceLevel).ToArray();
        }
        var l1 = Levels(5);
        Assert.Equal(l1, Levels(5));
        Assert.All(l1, l => Assert.InRange(l, 2, 9));
    }

    [Fact]
    public void Threat_DiffersFromLevel_AuthorWins_AndTheSceneChangesIt()
    {
        var db = Db();
        var fast = Clone(db, "ref", "fast", u => u.Stats[Stats.MoveSpeed] = 6);
        var slowTank = Clone(db, "ref", "slowtank", u =>
        {
            u.Stats[Stats.HpMax] = 300;
            u.Stats[Stats.MoveSpeed] = 0.6;
        });
        var flyer = Clone(db, "ref", "flyer", u => u.MoveCategory = "fly");
        Assert.True(ThreatModel.Base(db, fast) > ThreatModel.Base(db, db.Unit("ref")!));
        Assert.True(ThreatModel.Base(db, flyer) > ThreatModel.Base(db, db.Unit("ref")!));
        // The slow tank has a much higher level but not a proportionally higher threat.
        double levelRatio = PowerModel.Evaluate(db, slowTank).Power / PowerModel.Evaluate(db, db.Unit("ref")!).Power;
        double threatRatio = ThreatModel.Base(db, slowTank) / ThreatModel.Base(db, db.Unit("ref")!);
        Assert.True(threatRatio < levelRatio, $"угроза ×{threatRatio:0.##} должна расти медленнее уровня ×{levelRatio:0.##}");
        fast.ThreatCost = 9;
        Assert.Equal(9, ThreatCost.Of(db, fast));

        // A scene: melee defenders cannot reach a flyer; a long route lowers the threat of walkers.
        var map = TestUtil.Field(40, 5);
        map.Id = "threat_field";
        db.Add(map);
        var sc = new ScenarioDef { Id = "th", Name = "Угроза", Map = map.Id };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 2)));
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(39, 2)));
        sc.Routes.Add(RouteGraph.FromCells("r", HexCoord.Line(new HexCoord(0, 2), new HexCoord(38, 2)).ToList(), "spawn_1", "base_1"));
        sc.Units.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(35, 1) });
        Assert.True(ThreatModel.HasContext(sc));
        var tf = ThreatModel.Evaluate(db, flyer, sc);
        Assert.NotNull(tf.VsDefenders);
        Assert.Contains(tf.Factors, f => f.Key == "air" && f.Value > 1);
        var tr = ThreatModel.Evaluate(db, db.Unit("ref")!, sc);
        Assert.True(tr.InMap < tr.Base, $"на длинном маршруте (38 гекс.) угроза ниже базовой: {tr.InMap} / {tr.Base}");
        Assert.Equal(tf.Effective, ThreatCost.Of(db, flyer, sc));
    }

    [Fact]
    public void BalanceChecks_NameTheRule_NothingIsForbidden()
    {
        var db = Db();
        var tough = Clone(db, "ref", "tough", u =>
        {
            foreach (var t in new[] { "fire", "ice", "electric", "poison" }) u.Defense.Modifiers.Add(new DamageModDef { Match = t, Percent = -75 });
        });
        db.Add(new PassiveDef
        {
            Id = "endless", Name = "Без предела",
            Modifiers = { new ModifierDef { Target = ModifierTarget.OutgoingDamage, Match = "*", Value = 10, PerEach = new PerEachDef { Types = { "tag:pack" } } } },
        });
        var crowd = Clone(db, "ref", "crowd", u => u.Passives.Add("endless"));
        var fast = Clone(db, "ref", "zoom", u => u.Stats[Stats.MoveSpeed] = 7);
        Assert.Contains(BalanceChecks.Check(db, tough), x => x.Rule == "устойчивость");
        Assert.Contains(BalanceChecks.Check(db, crowd), x => x.Rule == "множитель от союзников");
        Assert.Contains(BalanceChecks.Check(db, fast), x => x.Rule == "скорость");
        Assert.Empty(BalanceChecks.Check(db, db.Unit("ref")!));
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Path == "Balance" && i.Source.Contains("tough"));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error && i.Path == "Balance");
    }

    [Fact]
    public void ShippedContent_LevelsAreSensible_ProfilesExist()
    {
        var db = TestUtil.Shipped();
        Assert.Equal("goblin", db.Rules.Power.ReferenceUnit);
        double L(string id) => PowerModel.Evaluate(db, db.Unit(id)!).Level;
        Assert.Equal(1, L("goblin"), 6);
        Assert.True(L("orc_brute") > L("goblin") + 2);
        Assert.True(L("wolf_leader") > L("wolf"));
        Assert.True(L("titan") >= 15, $"титан-босс: {L("titan"):0.0}");
        Assert.Equal(UnitRank.Leader, db.Unit("wolf_leader")!.Rank);
        Assert.Equal(UnitRank.Boss, db.Unit("titan")!.Rank);
        foreach (var u in db.All<UnitDef>()) Assert.True(PowerModel.Evaluate(db, u).Power >= 0);
        var names = new[] { "Равномерное усиление", "Танк", "Боец", "Дальний урон", "Убийца", "Поддержка", "Контролёр", "Босс", "Мобильный противник", "Пользовательский профиль" };
        Assert.Equal(names.OrderBy(n => n), db.All<ScalingProfileDef>().Select(p => p.Name).OrderBy(n => n));
        // The threat of the shipped templates comes from the model now, and keeps its order.
        Assert.True(ThreatCost.Of(db, db.Unit("titan")!) > ThreatCost.Of(db, db.Unit("orc_brute")!));
        Assert.True(ThreatCost.Of(db, db.Unit("orc_brute")!) > ThreatCost.Of(db, db.Unit("goblin")!));
    }
}
